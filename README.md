# Google Cloud Storage Connector for ODC

[![Platform](https://img.shields.io/badge/Platform-OutSystems_ODC-red.svg)](https://www.outsystems.com/odc/)
[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![GCS SDK](https://img.shields.io/badge/SDK-Google_Cloud_Storage-green.svg)](https://cloud.google.com/dotnet/docs/reference/Google.Cloud.Storage.V1/latest)

A high-performance .NET 10.0 External Logic component for OutSystems Developer Cloud (ODC) that provides a seamless integration with Google Cloud Storage (GCS). Designed for enterprise-grade scalability, security, and developer efficiency.

## Table of Contents

- [Architecture](#architecture)
- [Prerequisites](#prerequisites)
- [Quick Start](#quick-start)
- [Authentication](#authentication)
- [Action Reference](#action-reference)
  - [Object Operations](#object-operations)
  - [Bucket Operations](#bucket-operations)
- [Data Structures](#data-structures)
- [Project Structure](#project-structure)
- [Build and Deployment](#build-and-deployment)
- [Best Practices](#best-practices)
- [License](#license)

---

## Architecture

```
GoogleCloudStorage_ODC/
├── GoogleCloudStorage.csproj   # Project definition
├── IGoogleCloudStorage.cs      # ODC External Logic Interface
├── GoogleCloudStorage.cs       # Implementation logic (Adapter)
├── GoogleCloudStorage.Auth.cs  # Authentication: Workload Identity Federation + service account key
├── Resources/                  # Embedded branded icons
└── Structures/                 # Strongly-typed ODC structures
```

The connector is architected as an **adapter**. It bridges the OutSystems Developer Cloud runtime with the official Google Cloud Storage .NET SDK using the **Bridge Pattern**. This ensures that the OutSystems application logic remains decoupled from the low-level SDK implementation details.

### Key Architectural Decisions:
- **Keyless authentication:** Workload Identity Federation (Google's recommended method for workloads outside Google Cloud) works with any OIDC identity provider, server-to-server. Service account keys remain supported as a legacy fallback. See [Authentication](#authentication).
- **Cached, thread-safe clients:** `StorageClient` and `UrlSigner` instances are cached per credential (keyed by a SHA-256 hash of every credential field, never the raw secret; the two methods never share an entry, and each supplied `SubjectToken` gets its own entry) and reused across requests. This avoids re-parsing the RSA private key and allocating a new `HttpClient` on every call — both types are thread-safe, so sharing them is safe under high concurrency and prevents socket exhaustion. The caches are bounded (500 entries each, least recently used first) and an entry expires after 60 minutes without use, or when the supplied token it was built from expires.
- **Actionable errors:** every action translates failures into clear, actionable messages: missing bucket vs. object, access denied, unauthenticated, bucket-not-empty, credential mismatch, identity provider errors, network failures and timeouts. The original exception is preserved as the inner exception for diagnostics. Messages never contain secrets, token endpoint credentials or query strings, or raw identity provider responses.
- **Within ODC's platform limits:** every action stops itself before ODC's 95-second limit, so the connector's own message reaches the app instead of a platform timeout. `Object_Upload` and `Object_Download` stream ODC Files and stop after 85 seconds. Every other action runs under one 85-second deadline that covers all of its requests and retries, and its error says what the action was doing and what state that leaves. `Object_DeleteByPrefix` stops after 75 seconds and reports its progress. As a backstop, a single request to Google, including the token exchange, impersonation and URL-signing calls, times out after 90 seconds, and a token request to your identity provider after 30 seconds. `Object_DownloadBinary` refuses objects over ODC's 5.5 MB payload limit.
- **Safe retries:** transient Google errors (408, 429, 500, 502, 503, 504) and network errors are retried, up to 3 attempts, wherever a repeat cannot change the outcome: uploads and downloads; reads, listings (page by page), copies and URL signing; deletes and metadata updates (each guarded by the object's generation); bucket creation and deletion.
- **Honest answers after a repeat:** when a request is repeated after an attempt whose outcome is unknown (a network error, or a 500, 502, 503 or 504) and the repeat finds the object gone or changed, the connector checks before it answers, and reports success only when the earlier attempt really did the work. After a 408 or 429, which Google did not apply, a changed object is always reported.
- **Safe error messages:** object names, bucket names, prefixes and other inputs, and Google's or the identity provider's error text, are cleaned before they go into an error message. Line breaks, other control characters, the Unicode line and paragraph separators and invisible text-direction characters become spaces, so a crafted name cannot forge or reorder lines in the ODC error log. A `ClientSecret` that the identity provider echoes back, plain or encoded the way it was sent, is masked, and so is any text shaped like a bearer token or a Basic credential, in case a proxy echoes the request it refused.
- **Checked inputs:** a bucket name may contain only lowercase letters, digits, dashes, underscores and dots, and must start and end with a letter or digit; an object cannot be named `.` or `..`; a content type must be printable ASCII; a `SubjectToken` must have the shape of a JWT; `ServiceAccountEmail` must be an e-mail address or a numeric unique ID. Anything else is rejected before any request is sent.
- **End-to-end upload integrity:** `Object_Upload` and `Object_UploadBinary` send the CRC32C checksum of the exact bytes with the upload (Google.Cloud.Storage.V1 5.x). If anything changes the data in transit, Google rejects the upload before storing it, so a corrupted object is never created and an existing object is never overwritten with bad data.
- **V4 Signed URLs:** Offloads large file data transfers directly to the client browser, bypassing the ODC server to optimize memory and bandwidth.
- **Resource Embedding:** Branded icons are embedded directly into the assembly to provide a premium integrated experience in Service Studio.

---

## Prerequisites

- [OutSystems Developer Cloud (ODC)](https://www.outsystems.com/odc/)
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- An active Google Cloud Project with Billing enabled.
- A Service Account with the following IAM roles:
  - `Storage Object Admin` (full object control)
  - `Storage Admin` (required for bucket management)
  - `Bucket_Exists` with Storage Object Admin only: add `Storage Legacy Bucket Reader` on the bucket (it grants `storage.buckets.get`).
- With **Workload Identity Federation**: grant the federated identity `Workload Identity User` on the service account, plus `Service Account Token Creator` for **Signed URLs** (signing goes through the IAM `signBlob` API). With a **service account key**, signed URLs are signed locally and need no extra role.

---

## Quick Start

```bash
# Build the project
dotnet build GoogleCloudStorage.csproj -c Release

# Publish for ODC (standard deployment)
dotnet publish GoogleCloudStorage.csproj -c Release -f net10.0 --no-self-contained
```

After publishing, zip the contents of `bin/Release/net10.0/publish/` (**excluding** the `.pdb`, and **keeping** `OutSystems.ExternalLibraries.SDK.dll`), keeping its `runtimes/` folder, and upload the zip to the ODC Portal. See [Build and Deployment](#build-and-deployment).

---

## Authentication

Every action takes an `Authentication` structure. Two methods are supported, selected with `AuthenticationMethod`:

| Method | Google's guidance | Google credential stored | Signed URLs |
|---|---|---|---|
| **`WorkloadIdentityFederation`** | **Recommended** for workloads outside Google Cloud | **None**, only short-lived tokens | Signed by the service account through the IAM `signBlob` API |
| **`ServiceAccountKey`** *(default when empty)* | Last resort | A long-lived private key | Signed locally with the key |

Existing apps keep working unchanged: an empty `AuthenticationMethod` means `ServiceAccountKey`. Because the method is just a value, you can drive it from a setting and migrate one environment at a time. Store every credential value in **ODC App Settings**, marking secrets as **Secret**.

### Workload Identity Federation (recommended, keyless)

The connector never holds a Google key. On each token refresh (roughly hourly) it:

1. obtains a JWT from **your identity provider** using the standard OAuth 2.0 **client-credentials** grant. This is server-to-server with no user interaction, so it works in server actions and timers. Alternatively, you pass a JWT you already have in `SubjectToken`;
2. exchanges it with **Google's Security Token Service** for a federated token;
3. **impersonates your service account** for a short-lived access token, which Storage calls use.

Any OIDC identity provider that issues **signed JWTs (RS256/ES256)** works: Microsoft Entra ID, Okta, Auth0, Keycloak, Ping, ADFS, and others.

| Field | Description |
|---|---|
| `AuthenticationMethod` | `WorkloadIdentityFederation` |
| `ProjectId` | Your Google Cloud project ID (required by `Bucket_List` / `Bucket_Create`; surrounding spaces are ignored) |
| `WorkloadIdentityProvider` | `//iam.googleapis.com/projects/PROJECT_NUMBER/locations/global/workloadIdentityPools/POOL/providers/PROVIDER` |
| `ServiceAccountEmail` | The service account the connector acts as. Must be an e-mail address (`NAME@PROJECT_ID.iam.gserviceaccount.com`) or the account's numeric unique ID; surrounding spaces are ignored. |
| `TokenEndpoint` | Your identity provider's OAuth 2.0 token endpoint (**https**). Use the final URL: redirects are not followed, and a URL with user information (`user:password@`) is rejected. Private and on-premises hosts (Keycloak, ADFS) are fine. |
| `ClientId` / `ClientSecret` | Your app registration at the identity provider (store the secret as **Secret**). Spaces and line breaks around `ClientId` are ignored; `ClientSecret` is sent exactly as given. |
| `Scope` | Optional `scope` for the token request (surrounding spaces are ignored) |
| `Audience` | Optional `audience` for the token request (some providers require it; surrounding spaces are ignored) |
| `SubjectToken` | Optional: a JWT you obtained yourself. When set, `TokenEndpoint`/`ClientId`/`ClientSecret` are not needed. A token whose `exp` has passed is rejected before calling Google. Each distinct token is exchanged and cached on its own, until its `exp` (or 5 minutes when it has none). A value that does not have the shape of a JWT (three base64url parts separated by dots), or is longer than 32,768 characters, is rejected and never cached. A token that Google's token exchange refuses is dropped from the cache at once. |

Client authentication uses `client_secret_post`. If the provider rejects the client (`invalid_client`), the connector retries once with HTTP Basic (`client_secret_basic`) and remembers which method worked. One token request, both attempts included, may take 30 seconds; a slower provider fails with "did not respond within 30 seconds".

**Provider examples** (confirm your tokens' `iss` and `aud` claims by decoding one, e.g. at [jwt.ms](https://jwt.ms)):

| Provider | `TokenEndpoint` | `Scope` / `Audience` | Notes |
|---|---|---|---|
| Microsoft Entra ID | `https://login.microsoftonline.com/TENANT_ID/oauth2/v2.0/token` | `Scope` = `api://YOUR_APP_ID_URI/.default` | Request a token for **your own app registration**, not Microsoft Graph. The issuer is `https://sts.windows.net/TENANT_ID/` for v1 tokens (the default) or `https://login.microsoftonline.com/TENANT_ID/v2.0` for v2. |
| Okta | `https://YOUR_ORG.okta.com/oauth2/AUTH_SERVER_ID/v1/token` | `Scope` = your custom scope | Needs a **custom authorization server** (the org server doesn't issue client-credentials tokens with custom scopes). |
| Auth0 | `https://YOUR_TENANT.auth0.com/oauth/token` | `Audience` = your API identifier | Machine-to-machine application. |
| Keycloak | `https://HOST/realms/REALM/protocol/openid-connect/token` | — | Enable **Service accounts** on the client. |

**Google Cloud setup (one-time).** The federation pieces cost nothing: IAM, STS and IAM Credentials are free.

```bash
PROJECT_ID=my-project
PROJECT_NUMBER=$(gcloud projects describe $PROJECT_ID --format='value(projectNumber)')
SA=gcs-connector@$PROJECT_ID.iam.gserviceaccount.com

gcloud services enable iam.googleapis.com iamcredentials.googleapis.com sts.googleapis.com --project=$PROJECT_ID
gcloud iam service-accounts create gcs-connector --project=$PROJECT_ID

# Pool + OIDC provider. ISSUER and AUDIENCE are the 'iss' and 'aud' claims of your provider's tokens;
# APP_SUBJECT is their 'sub' claim (for Entra ID client credentials: the service principal's object ID).
gcloud iam workload-identity-pools create odc-apps --location=global --project=$PROJECT_ID
gcloud iam workload-identity-pools providers create-oidc my-idp \
  --location=global --workload-identity-pool=odc-apps --project=$PROJECT_ID \
  --issuer-uri="ISSUER" --allowed-audiences="AUDIENCE" \
  --attribute-mapping="google.subject=assertion.sub" \
  --attribute-condition="assertion.sub == 'APP_SUBJECT'"

# Let that identity act as the service account (+ sign URLs), and give the service account bucket access.
MEMBER="principal://iam.googleapis.com/projects/$PROJECT_NUMBER/locations/global/workloadIdentityPools/odc-apps/subject/APP_SUBJECT"
gcloud iam service-accounts add-iam-policy-binding $SA --role=roles/iam.workloadIdentityUser --member="$MEMBER" --project=$PROJECT_ID
gcloud iam service-accounts add-iam-policy-binding $SA --role=roles/iam.serviceAccountTokenCreator --member="$MEMBER" --project=$PROJECT_ID
gcloud storage buckets add-iam-policy-binding gs://MY_BUCKET --role=roles/storage.objectAdmin --member="serviceAccount:$SA"
```

Then set `WorkloadIdentityProvider` to `//iam.googleapis.com/projects/$PROJECT_NUMBER/locations/global/workloadIdentityPools/odc-apps/providers/my-idp` and `ServiceAccountEmail` to `$SA`. If your identity provider isn't reachable from the internet, upload its public keys (JWKS) to the provider instead of relying on its discovery URL. The connector itself must still be able to reach the provider's token endpoint.

### Service Account Key (legacy)

| Field | Source in the service account JSON key | Description |
|---|---|---|
| `AuthenticationMethod` | — | Empty or `ServiceAccountKey` |
| `ProjectId` | `project_id` | Your Google Cloud project ID |
| `ClientEmail` | `client_email` | Service account email |
| `PrivateKey` | `private_key` | Full RSA private key (with BEGIN/END headers) |

> **Key creation may be blocked.** Google Cloud organizations created since 2024 enforce the policy `constraints/iam.disableServiceAccountKeyCreation` by default, so creating a key fails. Use Workload Identity Federation instead (no key needed), or have someone with `Organization Policy Administrator` (`roles/orgpolicy.policyAdmin`) relax the policy for the project.

---

## Action Reference

### Object Operations

#### `Object_Upload`
Uploads an ODC **File** (from an Upload widget, an entity attribute or another action) to a bucket, optionally with custom metadata. The content streams from ODC storage to Google in 32 MB chunks, so it never passes through the action's 5.5 MB payload limit; ODC Files go up to 2 GB.

- **Time is the limit.** One upload may run for 85 seconds, retries included, within ODC's 95-second action limit; a slower one stops with an error. A request in flight at that moment may still complete at Google, so check with `Object_Exists` before relying on either outcome. Measured on ODC: 200 MB took about 10 s to a bucket in a nearby region and 22 s to one on another continent. Keep the bucket in or near your ODC region, and use `Object_GetSignedUrl` (Operation `Upload`) for files too large to send in that time.
- **Retries:** transient errors (408, 429, 500, 502, 503, 504, network) are retried. An interrupted chunk is resent, and a failed start reopens the File, up to 3 attempts in total.
- **Content type:** the content type must be printable ASCII (for example `application/pdf`). One with a line break, another control character or a non-ASCII character is rejected before anything is sent.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Destination bucket |
| `objectName` | `Text` | Full path/filename in the bucket |
| `file` | `File` | The ODC File to upload |
| `contentType` | `Text` | Optional MIME type to store. Empty = the File's own MIME type. |
| `metadata` | `List of MetadataEntry` | Optional custom key-value metadata to store with the object (e.g. tenant, document type). Retrievable via `Object_GetMetadata`. Leave empty for none. |

> **Integrity check:** the upload carries a CRC32C checksum of the bytes sent. If the data Google receives doesn't match, the upload fails with an "Upload rejected by Google Cloud Storage … retry the upload" error, nothing is stored, and any existing object with that name is left unchanged.

#### `Object_Download`
Downloads an object into a new ODC **File**, streaming it from Google into ODC storage and checking Google's CRC32C. Objects up to 2 GB can be downloaded, the most an ODC File can hold; a larger object is refused before anything is transferred.

- **Name and type:** the File is named after the last part of the object name (`reports/2026/q1.pdf` gives `q1.pdf`), with the object's content type.
- **Temporary File:** the returned File is temporary, so save it to an entity attribute to keep it; ODC deletes an unsaved one after a short while. To hand it to the browser, use ODC's `GetFileSignedURL`, or the Download node in a screen action.
- **One consistent object:** the download fetches the generation whose size and type were just read. If the object is replaced or deleted meanwhile, the action says so ("was replaced or deleted while it was being downloaded") instead of returning the new bytes with the old type.
- **gzip:** objects stored with `Content-Encoding: gzip` arrive **decompressed**. Google's CRC32C covers the stored (compressed) bytes, so these downloads are not checksum-checked, and the 2 GB check is made on the stored size.
- **Limits:** the same 85-second limit and retries as `Object_Upload` apply.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Source bucket |
| `objectName` | `Text` | Full path/filename in the bucket |

**Outputs:**
| Output | Type | Description |
|--------|------|-------------|
| `file` | `File` | The downloaded File (temporary until saved to an entity) |

#### `Object_UploadBinary`
Uploads content held in memory (`BinaryContent`: Binary Data and a content type) to a bucket, optionally with custom metadata.

- **When to use it:** for content your app builds in memory (a generated PDF, a CSV, a REST response). ODC app logic cannot turn Binary Data into a File, so use this action for that content. It is also the one to use on **ODC Self-Hosted**, where the File actions have not been verified. Otherwise prefer `Object_Upload`.
- **Size limit:** ODC limits an action's input to 5.5 MB. The limit covers the whole request, so content close to it may be refused by ODC before it reaches the connector.
- **Same behaviour as `Object_Upload`:** the CRC32C integrity check, the retries and the content type check apply.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Destination bucket |
| `objectName` | `Text` | Full path/filename in the bucket |
| `content` | `BinaryContent` | The content and its MIME type |
| `metadata` | `List of MetadataEntry` | Optional custom key-value metadata, as in `Object_Upload` |

#### `Object_DownloadBinary`
Downloads an object into memory as Binary Data, with its content type.

- **Size limit:** objects larger than **5.5 MB**, ODC's payload limit for an action, are refused with an error pointing to `Object_Download` and `Object_GetSignedUrl`.
- **gzip:** objects stored with `Content-Encoding: gzip` are returned **decompressed**, so `Content` can be larger than the stored `Size`. Google's CRC32C covers the stored bytes, so these downloads are not checksum-checked.
- **Retries:** transient errors are retried, up to 3 attempts in total.
- **Time limit:** a download that stalls is cut off by the action's 85-second deadline, with the connector's own message.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Source bucket |
| `objectName` | `Text` | Full path/filename in the bucket |

**Outputs:**
| Output | Type | Description |
|--------|------|-------------|
| `content` | `BinaryContent` | The content and its MIME type |

#### `Object_List`
Lists objects in a bucket, optionally filtered by prefix, with support for pagination (`MaxResults`/`PageToken`) and folder-style navigation (`Delimiter`).

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Source bucket |
| `prefix` | `Text` | Prefix filter for hierarchical navigation |
| `maxResults` | `Integer` | Maximum objects to return in this call; `0` returns everything. When greater than `0`, use `NextPageToken` to fetch the next page. |
| `pageToken` | `Text` | Continuation token from a previous call's `NextPageToken`; empty starts from the first page |
| `delimiter` | `Text` | Typically `/` — groups nested objects into `PrefixList` for folder-style browsing; empty lists recursively |

**Outputs:**
| Output | Type | Description |
|--------|------|-------------|
| `objects` | `List of Object` | Collection of GCS object metadata |
| `nextPageToken` | `Text` | Non-empty when more results exist (paged mode only) — pass it as `PageToken` in the next call |
| `prefixList` | `List of Prefix` | The "folders" found directly under `Prefix` when `Delimiter` is set |

> **Page size:** Google returns at most **1,000** entries per page, even when `MaxResults` is higher, and with a `Delimiter` the folder entries in `PrefixList` count towards `MaxResults` together with `Objects`.
>
> **Pagination:** pass a `MaxResults` greater than `0` to return a single page, then feed the returned `NextPageToken` back as `PageToken` until it comes back empty. With `MaxResults = 0` every object is returned in one call (no `NextPageToken`). A listing that takes longer than 85 seconds stops with an error asking you to page it, and a very long list can exceed ODC's 5.5 MB output limit: page large buckets.

#### `Object_Exists`
Checks whether an object exists in a bucket via a lightweight metadata probe.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Source bucket |
| `objectName` | `Text` | Full path/filename to check |

**Outputs:**
| Output | Type | Description |
|--------|------|-------------|
| `exists` | `Boolean` | True if the object exists |

> **Permissions:** with a role that cannot list objects (no `storage.objects.list`), Google answers 403 instead of 404 for an object that does not exist, so the action fails with an access-denied error instead of returning False. Storage Object Viewer and the roles above it include that permission.

#### `Object_GetMetadata`
Retrieves an object's full metadata (size, content type, hashes, generation, storage class, timestamps, and custom metadata) without downloading its content. Returns `Exists = False` if the object is not found, leaving the `metadata` and `customMetadata` outputs empty.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Source bucket |
| `objectName` | `Text` | Full path/filename to inspect |

**Outputs:**
| Output | Type | Description |
|--------|------|-------------|
| `exists` | `Boolean` | True if the object was found |
| `metadata` | `ObjectMetadata` | Full object metadata (only populated when `exists` is True) |
| `customMetadata` | `List of MetadataEntry` | The object's custom key-value metadata. Empty when the object has none or does not exist. |

#### `Object_Delete`
Deletes an object from a bucket. On a bucket with **Object Versioning** or **soft delete** enabled, Google keeps a noncurrent or soft-deleted copy that can be restored (and is billed) until it expires; on other buckets the object is gone. The connector reads the object's generation first and deletes only that generation, so transient errors are retried and an object replaced in the meantime is never deleted (the action then reports it). The connector repeats the request itself. If an earlier attempt may have been applied although it failed (a network error, or a 500, 502, 503 or 504) and the repeat finds the object gone, the action reports success. If the repeat answers that the generation no longer matches, the connector looks at the object: when it is gone the action reports success, and when a newer object exists the action reports that it was replaced, without touching it. After a 408 or 429, a generation that no longer matches is always reported as a replacement. With a role that may delete but not read objects, it falls back to an unguarded delete, which is not retried.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Source bucket |
| `objectName` | `Text` | Full path/filename to delete |

#### `Object_Copy`
Copies an object to another location, within the same bucket or across buckets, without downloading its content. Overwrites the destination if it exists. Transient Google errors and network errors are retried.

> **Large objects across locations or storage classes:** Google copies these in several steps. If they do not finish within the action's 85 seconds, the action stops with an error, and the destination exists only if the last step completed. Copy such objects within one location and storage class, or use Google's Storage Transfer Service.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `sourceBucketName` | `Text` | Bucket that currently contains the object |
| `sourceObjectName` | `Text` | Full path/filename of the source object |
| `destinationBucketName` | `Text` | Bucket to copy into (can equal the source) |
| `destinationObjectName` | `Text` | Full path/filename for the destination |

#### `Object_Move`
Moves an object to another location (copy + delete of the source), within the same bucket or across buckets. Use the same source and destination bucket with a different name to rename. Moving an object onto itself (same bucket and name) is refused. Overwrites the destination if it exists.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `sourceBucketName` | `Text` | Bucket that currently contains the object |
| `sourceObjectName` | `Text` | Full path/filename of the source object |
| `destinationBucketName` | `Text` | Bucket to move into (can equal the source) |
| `destinationObjectName` | `Text` | Full path/filename for the destination |

> **Note:** Move is copy-then-delete and is not atomic. Both steps are guarded by the source's generation: if another request replaces the source during the move, nothing is lost. The move either reports that nothing was moved, or that both objects exist and the source holds newer data. If the copy succeeds but the source can't be deleted, the action reports explicitly that both objects now exist; when the delete may still have gone through (a timeout, a 5xx), it says that both objects may exist and to check the source. A missing destination bucket and a missing source object are reported as such. If the delete of the source had to be repeated after an attempt whose outcome is unknown, the connector checks whether the source is in fact gone before it reports anything.

#### `Object_UpdateMetadata`
Changes an object's content headers and custom metadata **without re-uploading its content**. Only the fields you provide change: empty text inputs are left untouched, and an empty `metadata` list leaves custom metadata untouched. Within `metadata`, an entry with an empty `Value` **removes** that key. A call with nothing to update is rejected. The write is guarded by generation and metageneration preconditions, so a concurrent metadata update, or a replacement of the object's content, makes the call fail cleanly ("changed by another request … retry") instead of being silently overwritten. `ContentType` must be printable ASCII, and the other three header values cannot contain line breaks or other control characters. If the request had to be sent again after an attempt whose outcome is unknown (a network error, or a 500, 502, 503 or 504) and the repeat finds the object changed, the first request may have been applied, so the message says to read the metadata with `Object_GetMetadata` before retrying.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Source bucket |
| `objectName` | `Text` | Full path/filename of the object to update |
| `contentType` | `Text` | New MIME type. Empty = unchanged. |
| `contentEncoding` | `Text` | New content encoding (e.g. `gzip`). Empty = unchanged. |
| `contentDisposition` | `Text` | New content disposition (e.g. `attachment; filename="report.pdf"`). Empty = unchanged. |
| `cacheControl` | `Text` | New cache control (e.g. `public, max-age=3600`). Empty = unchanged. |
| `metadata` | `List of MetadataEntry` | Custom metadata changes. Empty list = unchanged. An entry with an empty `Value` removes that key; others are set/overwritten. |

#### `Object_DeleteByPrefix`
Deletes every object under a prefix (a "folder" and everything below it) server-side, and returns how many were deleted. The prefix is **mandatory and cannot be empty**, as a safety guard against wiping an entire bucket. Objects are listed and deleted one page (1,000 objects) at a time and one object at a time, so the call never holds the whole listing in memory. Each object is deleted only if it is still the generation that was listed, so transient errors are retried automatically. An object deleted (404) or replaced (412) after the listing is skipped and not counted, so new data written under the prefix during the call is never deleted. A network error on a listing page or on a delete is retried instead of ending the call, and a delete whose answer was lost that way is still counted. (When Google's library repeats a delete after a 5xx answer that had in fact been applied, the repeat gets a 404 and is not counted, so the count can be slightly lower than the number of objects removed.) One call works for at most **75 seconds** and is cut off at 88 seconds even if a request is still running; it then stops with an error saying how many objects it deleted, and calling it again with the same prefix continues. A mid-operation failure also reports how many objects were already deleted, with the translated cause. Versioning and soft delete apply as for `Object_Delete`.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Source bucket |
| `prefix` | `Text` | All objects whose names start with this prefix are deleted (e.g. `uploads/2025/`). Cannot be empty. |

**Outputs:**
| Output | Type | Description |
|--------|------|-------------|
| `deletedCount` | `Long Integer` | Number of objects that were deleted |

> **Time budget:** one call deletes for at most **75 seconds** (ODC stops an action at 95). If the prefix holds more objects than that, the action stops with an error saying how many were deleted. Nothing is rolled back; call it again with the same prefix to delete the rest.

#### `Object_GetSignedUrl`
Generates a time-limited V4 signed URL for secure, direct-to-browser file access. The `operation` controls the action the URL permits: download, upload, or delete.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Source bucket |
| `objectName` | `Text` | Full path/filename. Required: an empty name would sign a URL for the whole bucket. |
| `expirationMinutes` | `Integer` | Link validity duration, `1`–`10080` (a V4 signed URL is valid for at most 7 days). Values outside this range raise a clear error. With `WorkloadIdentityFederation`, Google guarantees a `signBlob` signature for **12 hours** only: a longer URL may stop working earlier when Google rotates its signing keys. Use a service account key if you need longer-lived URLs. |
| `operation` | `Text` | Optional. `Download` (GET), `Upload` (PUT), or `Delete` (DELETE). Case-insensitive. Defaults to `Download` when the input is not set; an empty text is rejected ("Invalid Operation ''"). |
| `contentType` | `Text` | Optional, `Upload` URLs only (ignored for `Download` and `Delete`). The exact `Content-Type` the client will send in the PUT request. It becomes part of the signature, so Google rejects uploads with a different `Content-Type`. Leave empty to allow any. Must be printable ASCII. |

**Outputs:**
| Output | Type | Description |
|--------|------|-------------|
| `url` | `Text` | Temporary secure URL. For `Upload`, the client sends an HTTP PUT with the file as the body. |

> **Multi-upload:** signed URLs are bound to a specific object path, so request one `Upload` URL per file (pass each file's `objectName`).
>
> **Content-Type binding:** if you pass `contentType`, the client's PUT must send exactly that `Content-Type` header, or Google rejects the upload with a signature mismatch. Leave it empty to accept any content type.
>
> **Checksums:** an `Upload` URL is used by the client directly, without the connector, so `Object_Upload`'s automatic CRC32C check doesn't apply to it. If you need end-to-end validation for direct uploads, follow Google's [data validation guide](https://cloud.google.com/storage/docs/data-validation) in the client.
>
> **Browser uploads need CORS:** a browser can only PUT to a signed URL if the bucket's CORS configuration allows your app's origin, the `PUT` method and the `Content-Type` header. Set it once per bucket (`gcloud storage buckets update gs://BUCKET --cors-file=cors.json`).
>
> **What an `Upload` URL cannot limit:** whoever holds the URL can upload content of any size to that object name, replacing an existing object, until the URL expires. Keep the expiry short, sign one URL per upload, and check the object's size and type with `Object_GetMetadata` afterwards.

---

### Bucket Operations

#### `Bucket_List`
Lists all buckets in the specified project.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |

**Outputs:**
| Output | Type | Description |
|--------|------|-------------|
| `buckets` | `List of Bucket` | Collection of project bucket metadata |

#### `Bucket_Create`
Provisions a new globally unique storage container. The request is repeated on transient errors. If a repeat answers that the bucket already exists right after an attempt whose outcome is unknown, the connector reports success only when it can read the bucket and the bucket was created since the call began. Buckets are created with Google's defaults for access control: turn on uniform bucket-level access and public access prevention in Google Cloud if your organisation requires them.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Globally unique name |
| `location` | `Text` | Geographic region (e.g., `US`, `EU`, `asia-east1`) |

#### `Bucket_Delete`
Decommissioning of an empty storage container. The request is repeated on transient errors; a repeat that finds the bucket gone right after an attempt whose outcome is unknown reports success.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Name of the bucket to delete |

#### `Bucket_Exists`
Checks whether a bucket exists and is accessible to the service account, without listing its contents. It needs `storage.buckets.get`, which **Storage Object Admin does not include**: grant Storage Admin, or keep Storage Object Admin and add Storage Legacy Bucket Reader on the bucket (or a custom role with that permission), otherwise the action fails with an access-denied error instead of returning a value.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Name of the bucket to check |

**Outputs:**
| Output | Type | Description |
|--------|------|-------------|
| `exists` | `Boolean` | True if the bucket exists and the service account can access it |

---

## Data Structures

### `Authentication`
Google Cloud credentials for either method (see [Authentication](#authentication)).
- `ProjectId`: Text (mandatory)
- `AuthenticationMethod`: Text: `WorkloadIdentityFederation` or `ServiceAccountKey` (empty = `ServiceAccountKey`)
- `ClientEmail`, `PrivateKey`: Text (ServiceAccountKey only)
- `WorkloadIdentityProvider`, `ServiceAccountEmail`, `TokenEndpoint`, `ClientId`, `ClientSecret`, `Scope`, `Audience`, `SubjectToken`: Text (WorkloadIdentityFederation only)

### `BinaryContent`
Content held in memory, used by `Object_UploadBinary` and `Object_DownloadBinary`. Called `File` before 2.0.0; renamed so it is not confused with ODC's File data type.
- `Content`: Binary Data
- `ContentType`: Text (MIME type)

### `Object`
Represents object metadata.
- `Name`: Text (Full path)
- `Size`: Long Integer
- `ContentType`: Text
- `Updated`: Date Time (UTC)

### `Bucket`
Represents storage container metadata.
- `Name`: Text
- `Location`: Text
- `StorageClass`: Text
- `Created`: Date Time (UTC)

### `Prefix`
A folder-style entry returned by `Object_List` when `Delimiter` is set — a common prefix shared by the objects grouped under it.
- `Value`: Text (e.g., `images/thumbnails/`)

### `MetadataEntry`
A single custom metadata key-value pair stored with an object. Used by `Object_Upload` and `Object_UploadBinary` (input), `Object_GetMetadata` (output), and `Object_UpdateMetadata` (input).
- `Key`: Text (e.g., `tenant`, `documentType`)
- `Value`: Text (an empty `Value` removes the key in `Object_UpdateMetadata`)

### `ObjectMetadata`
Represents the complete metadata of an object (returned by `Object_GetMetadata`).
- `Name`: Text (Full path)
- `Bucket`: Text
- `Size`: Long Integer
- `ContentType`: Text
- `ContentEncoding`: Text
- `ContentDisposition`: Text
- `CacheControl`: Text
- `MD5Hash`: Text
- `Crc32c`: Text
- `ETag`: Text
- `Generation`: Long Integer
- `Metageneration`: Long Integer
- `StorageClass`: Text
- `MediaLink`: Text
- `TimeCreated`: Date Time (UTC)
- `Updated`: Date Time (UTC)

---

## Project Structure

```
GoogleCloudStorage_ODC/
├── GoogleCloudStorage.csproj   # OutSystems.ExternalLibraries.SDK, Google.Cloud.Storage.V1, Google.Apis.Auth,
│                               #   Google.Apis.Storage.v1, Google.Api.Gax.Rest (pinned)
├── IGoogleCloudStorage.cs      # OSInterface & OSAction definitions
├── GoogleCloudStorage.cs       # Actions, input guards and the shared error translator
├── GoogleCloudStorage.Auth.cs  # Authentication (key and Workload Identity Federation), caches
├── Resources/                  # Branding assets
│   ├── app_icon.png            # Library icon
│   └── action_icon.png         # Action-level icon
└── Structures/                 # ODC-compatible structs
    ├── Authentication.cs       # Credential model
    ├── BinaryContent.cs        # In-memory content (Binary actions)
    ├── Bucket.cs               # Container metadata
    ├── Object.cs               # File metadata (list entry)
    ├── ObjectMetadata.cs       # Full object metadata
    ├── Prefix.cs               # Folder-style entry (Object_List with Delimiter)
    └── MetadataEntry.cs        # Custom key-value metadata pair
tests/GoogleCloudStorage.Tests/ # xUnit suite: offline, emulator, federation contract, live (CI)
```

---

## Build and Deployment

1. **Publish:** Run `dotnet publish` as shown in Quick Start. The output is in `bin/Release/net10.0/publish/`.
2. **Clean:** Delete the `.pdb` from that folder. **Keep `OutSystems.ExternalLibraries.SDK.dll`:** since 2.0.0 the File actions need it, and ODC rejects the library without it ("IOSFile could not be found").
3. **Zip:** Compress the folder's contents, not the folder itself: the DLLs and `.deps.json` at the root of the zip, plus the `runtimes/` folder that `dotnet publish` produces (a dependency of `Google.Apis.Auth`).
4. **Deploy:** Upload to ODC Portal > External Logic.

---

## Testing

A full test suite lives in [`tests/`](tests/README.md) and covers every action — **no Google account or credentials required**:

```bash
dotnet test                                          # offline + emulator integration
dotnet test --filter "FullyQualifiedName~OfflineTests"   # offline only, no network
```

- **Offline tests** (signed URLs, validation, caching) use a throwaway in-memory RSA key — V4 signing is local cryptography.
- **Integration tests** run the connector against [fake-gcs-server](https://github.com/fsouza/fake-gcs-server) via the connector's internal `EmulatorBaseUri` test seam, which only the test assembly can set; no environment variable or setting can redirect the connector's storage traffic, so a real ODC server always talks to production GCS. The test fixture starts the emulator automatically and skips (rather than fails) if it can't. See [`tests/README.md`](tests/README.md) for details.
- **Upload integrity tests** check that uploads carry the CRC32C of the exact bytes and that data corrupted in transit is rejected with a clear error, against an in-process fake of Google's resumable-upload endpoint.
- **Repeat, deadline and network-retry tests** script Google's answers request by request: a repeated delete after each kind of failure, every action stalled at each of its steps, and a connection that fails once on every read, listing page, copy and delete.
- **Guarantee tests** prove the documented promises one by one: download checksums, which statuses are retried and how often, the HTTP method each `Operation` signs, and the time budget and progress messages of `Object_DeleteByPrefix`. Each of these tests fails when its guarantee is removed from the code.
- **File transfer tests** stream ODC Files through an in-memory fake of ODC's file storage and the fake of Google: chunked uploads, resumed and retried transfers, the 2 GB check, the 85-second deadline and storage failures.
- **Federation contract tests** drive the full Workload Identity Federation chain (identity provider → Google STS → impersonation → Storage / `signBlob`) against an in-process fake, asserting the exact requests on every hop.
- **Live federation tests** (CI only) run keyless against **real Google Cloud**. GitHub Actions' own OIDC token acts as the identity provider, so no secrets exist anywhere. They verify Google accepts the federated identity and that signed URLs verify against the service account's published certificate.

---

## Best Practices

- **Security:** Mark `PrivateKey` as a **Secret** App Setting in ODC to ensure it is encrypted and masked in logs.
- **Large files:** use `Object_Upload` / `Object_Download` with ODC Files: they stream outside ODC's 5.5 MB payload limit, up to 2 GB or what transfers in 85 seconds (about 1 GB to a bucket near your ODC region). Start very large transfers from a Timer or a Workflow rather than a screen, where ODC limits a server call to 60 seconds. Beyond that, use `Object_GetSignedUrl` and let the client transfer the file directly with Google. Large `Object_List` results count toward the 5.5 MB limit: page them with `MaxResults` / `NextPageToken`.
- **Bucket location:** keep the bucket in or near your ODC tenant's region. On ODC, an upload to a bucket on another continent was 2 to 3 times slower.
- **Naming:** Bucket names use lowercase letters, digits, hyphens (`-`), underscores (`_`) and dots (`.`), start and end with a letter or digit, and are 3–63 characters long (up to 222 with dots, each dot-separated part at most 63). See Google's [bucket naming rules](https://cloud.google.com/storage/docs/buckets#naming).
- **Tag your objects:** Use the `metadata` input on `Object_Upload` (or `Object_UpdateMetadata` later) to attach business context — tenant, owner, document type — that you can read back cheaply with `Object_GetMetadata` without downloading the file.
- **Edit metadata in place:** Use `Object_UpdateMetadata` to fix a `ContentType` or relabel objects without re-uploading their content; it is precondition-guarded, so concurrent edits fail cleanly.
- **Delete folders safely:** Use `Object_DeleteByPrefix` to remove a whole "folder" in one call — the mandatory, non-empty prefix guards against wiping an entire bucket by accident.

---

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

The release package contains third-party libraries (Google's Cloud Storage and API client libraries, the OutSystems External Libraries SDK, Newtonsoft.Json and Microsoft runtime libraries), each under its own licence: see [NOTICE](NOTICE).
