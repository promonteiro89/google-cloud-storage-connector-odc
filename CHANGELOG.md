# Changelog

All notable changes to the **Google Cloud Storage Connector for ODC** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.7.0] - 2026-10-28

Security and reliability release: credential isolation for Workload Identity Federation, a hardened token endpoint, safer `Object_Move` / `Object_UpdateMetadata`, and clear errors where ODC's platform limits used to cut actions off. No action, input or output changed.

### Security

- **A supplied `SubjectToken` is never reused for another caller.** The token is now part of the federated cache key (hashed) and each cached identity holds exactly one token. Before, every caller with the same provider and service account shared one cached identity, so a caller with an expired or wrong token could run on another caller's Google access token.
- **Expired subject tokens are rejected up front.** The JWT `exp` is read (signature not checked) and a token that expired more than 30 seconds ago fails before any network call. Cached entries built from a supplied token expire at its `exp` (or after 5 minutes when it has none), and client-credentials tokens are cached until the earlier of `expires_in` and their own `exp`.
- **Hardened `TokenEndpoint`:**
  - https only; plain `http` on loopback is accepted only by the test harness.
  - A URL with user information (`user:password@`) is rejected, and is never echoed.
  - Identity-provider redirects are no longer followed (a `307`/`308` re-sent the client secret to the redirect target); a `3xx` now fails with a "redirected" error.
  - Error messages never echo the provider's raw response body or the endpoint's query string: they show the OAuth `error` / `error_description` (truncated to 300 characters), or the size and `Content-Type` of a non-JSON response.
  - The production client also renews its connections every 5 minutes, so DNS changes of the identity provider are picked up.
- **`Object_GetSignedUrl`:** `BucketName` and `ObjectName` are required (an empty object name signed a URL for the whole bucket, including for `Delete`); a `ContentType` with line breaks is rejected; `ContentType` is signed only for `Upload` and ignored for `Download` / `Delete`.

### Fixed

- **`Object_Move` onto itself deleted the object.** Moving an object to the same bucket and name is now refused before any call. The move is also guarded by the source's generation: a source replaced during the move is reported ("nothing was moved", or "both objects exist and the source holds newer data") instead of being deleted.
- **`Object_UpdateMetadata` could write stale metadata onto new content.** It set only a metageneration precondition, which turned off the SDK's automatic generation check; a content replacement resets the metageneration, so a stale update still matched. Both preconditions are now sent, and a conflict reports that the object was changed by another request.
- **`Object_Copy` and `Object_Move` named the wrong side of a 404.** A copy to a missing destination bucket reported the *source* bucket as missing. The connector now checks the source again and names whichever side is actually missing.
- **A missing object in a bucket whose name contains "bucket"** (e.g. `acme-bucket`) was reported as a missing bucket, and `Object_Exists` / `Object_GetMetadata` threw instead of returning `False`.
- **Downloads of gzip-encoded objects** no longer fail the hash check on valid data (`DownloadValidationMode.Automatic`).
- Text outputs of `Object_List`, `Bucket_List` and `Object_Download` are never null; `Object_Upload` accepts empty `Content` and an empty `ContentType`; `Bucket_Create` with an empty `Location` uses Google's default; timestamps without an offset are read as UTC.

### Changed

- **One error translator for every action.** Network failures ("Could not reach Google Cloud"), timeouts, and identity provider errors (previously hidden behind the Google SDK's generic subject-token error) now give actionable messages everywhere, including in `Object_DeleteByPrefix`'s partial-failure report. The signing error suggests "Service Account Token Creator" only for a `403`.
- **ODC platform limits are handled by the connector:**
  - Every request to Google Cloud Storage times out after **80 seconds**, below ODC's 95-second action limit, with the connector's own message.
  - `Object_Download` refuses objects larger than **5.5 MB** (ODC's payload limit) with an error pointing to `Object_GetSignedUrl`, instead of transferring the whole object first.
  - `Object_DeleteByPrefix` stops after **75 seconds** and reports how many objects it deleted; call it again with the same prefix to continue.
- Empty bucket and object names are rejected by every action before any call to Google.
- **Credential caches are bounded and expire:** entries are dropped after 60 minutes without use (or at a supplied token's expiry), at most 500 per cache, least recently used first. A service account key is parsed once and shared by the storage client and the URL signer; cache keys length-prefix every part, so different credentials can never collide.

### Compatibility

- No signature changes: apps don't need to remap any action. Republish consuming apps to pick up this revision.
- Behaviour that can surface in existing apps:
  - a `TokenEndpoint` that redirects now fails;
  - an expired `SubjectToken` now fails before calling Google;
  - `ContentType` no longer restricts `Download` and `Delete` signed URLs;
  - downloads over 5.5 MB fail with the connector's message (they already failed at ODC's payload limit).

## [1.6.1] - 2026-10-09

Maintenance release: end-to-end upload integrity. No changes to actions, inputs, or outputs; a drop-in replacement for 1.6.0.

### Changed

- **`Google.Cloud.Storage.V1` `4.15.0` → `5.0.0`.** Uploads now send the CRC32C checksum of the exact bytes with the request, and Google rejects any mismatch **before** storing the object. Previously the check ran after the upload: a corrupted object was briefly stored and then deleted, which could also lose the previous version when overwriting. Now a failed check never touches existing data.
- **Refreshed the other Google client libraries:** `Google.Apis` / `Google.Apis.Auth` / `Google.Apis.Core` `1.76.0` → **`1.77.0`**, `Google.Apis.Storage.v1` `1.76.0.4250` → **`1.77.0.4290`**, and `Google.Api.Gax` / `Google.Api.Gax.Rest` `4.15.0` → **`4.15.1`**.

### Added

- A clear error when Google rejects an upload for a checksum mismatch: it says nothing was stored, any existing object is unchanged, and the upload can be retried.
- Upload integrity tests: the checksum sent matches an independent CRC32C of the content, and corrupted data is rejected with the friendly error.

### Documentation

- Signed URL `Upload`s go directly from the client to Google, so the automatic checksum applies only to `Object_Upload`; the README points to Google's data validation guide for direct uploads.
- Corrected the large-file guidance to ODC's 5.5 MB custom code payload limit.

## [1.6.0] - 2026-09-28

Keyless authentication with Workload Identity Federation, alongside the existing service account key.

### Added

- **Workload Identity Federation** (Google's recommended method for workloads outside Google Cloud). Set `AuthenticationMethod` to `WorkloadIdentityFederation` and no Google key exists anywhere: the connector obtains a JWT from **any OIDC identity provider** (Entra ID, Okta, Auth0, Keycloak, …), exchanges it with Google's Security Token Service, and impersonates a service account for a short-lived access token.
  - **Server-side, no user interaction:** tokens come from the standard OAuth 2.0 **client-credentials** grant (`TokenEndpoint`, `ClientId`, `ClientSecret`, optional `Scope` / `Audience`). It uses `client_secret_post`, with an automatic fallback to `client_secret_basic`.
  - **Bring your own token:** alternatively, pass a JWT in `SubjectToken`.
  - **Signed URLs** are signed by the service account through the IAM Credentials `signBlob` API.
  - Tokens are cached and refreshed automatically before they expire.
- New `Authentication` fields: `AuthenticationMethod`, `WorkloadIdentityProvider`, `ServiceAccountEmail`, `TokenEndpoint`, `ClientId`, `ClientSecret`, `Scope`, `Audience`, `SubjectToken`.
- Actionable errors for the new failure modes: missing fields, a malformed provider, the identity provider rejecting the client, a non-JWT token, Google rejecting the token exchange (issuer/audience/attribute condition), and a missing Token Creator role for signing.
- Tests: validation and federation protocol contract tests (a fake identity provider, STS, IAM Credentials and Storage; every hop asserted), plus a **live** CI job that runs keyless against real Google Cloud using GitHub's OIDC token, with no secrets.

### Changed

- `Authentication.ClientEmail` and `PrivateKey` are no longer mandatory (federation doesn't use them). An empty `AuthenticationMethod` still means `ServiceAccountKey`, so **existing apps behave exactly as before**.
- The unauthenticated / access-denied messages now name the identity actually in use and give hints for whichever method is configured.

### Compatibility

- Non-breaking: no action signatures changed. Consumers refresh the connector reference to see the new `Authentication` fields.

## [1.5.2] - 2026-09-01

Maintenance release. No changes to actions, inputs, or outputs — a drop-in replacement for 1.5.0.

### Changed

- **Refreshed the Google client libraries.** `Google.Apis` / `Google.Apis.Auth` / `Google.Apis.Core` `1.75.0` → **`1.76.0`**, `Google.Apis.Storage.v1` → **`1.76.0.4250`**, and `Google.Api.Gax` / `Google.Api.Gax.Rest` → **`4.15.0`**. `Google.Cloud.Storage.V1` remains at `4.15.0`. The `Google.Api.Gax.*` and `Google.Apis.Storage.v1` transitive dependencies are now pinned explicitly, so the published package no longer ships them at `Google.Cloud.Storage.V1`'s older minimums.

### Compatibility

- No breaking changes and no signature changes — consuming apps do **not** need to refresh the connector reference for this release.

## [1.5.0] - 2026-07-18

Custom object metadata, in-place metadata editing, and folder deletion.

### Added

- **Custom object metadata.** `Object_Upload` now accepts an optional `Metadata` input — a list of `MetadataEntry` key-value pairs (e.g. tenant, document type) stored with the object — and `Object_GetMetadata` returns it through the new `CustomMetadata` output.
- **`Object_UpdateMetadata` action.** Changes `ContentType`, `ContentEncoding`, `ContentDisposition`, `CacheControl`, and custom metadata **without re-uploading the object's content**. Only the provided fields change: empty text inputs leave fields untouched, an entry with an empty `Value` removes that key, and a call with nothing to update is rejected with a clear error. The write is guarded by a metageneration precondition, so concurrent metadata updates fail cleanly instead of silently overwriting each other.
- **`Object_DeleteByPrefix` action.** Deletes a "folder" — every object under a prefix — server-side and returns the number of objects deleted. The prefix is mandatory and non-empty as a safety guard against wiping an entire bucket; concurrent deletions are tolerated, and a mid-operation failure reports exactly how many objects were already deleted.
- **`MetadataEntry` structure** (`Key`/`Value`) backing all of the above.

### ⚠️ Breaking changes

- **`Object_Upload` and `Object_GetMetadata` signatures changed** — `Object_Upload` gained a `Metadata` input and `Object_GetMetadata` a `CustomMetadata` output. Existing logic keeps working, but consuming apps must refresh and republish the connector reference to pick up the new signatures.

## [1.4.0] - 2026-07-12

New object-listing, folder-navigation, and signed-URL capabilities, plus more actionable error reporting. Runs on .NET 10.

### Added

- **`Bucket_Exists`** — checks whether a bucket exists and is accessible to the service account, without listing its contents.
- **`Object_List` pagination** — new `MaxResults` and `PageToken` inputs and a `NextPageToken` output. Pass a `MaxResults` greater than `0` to return one page at a time, then feed `NextPageToken` back as `PageToken` until it comes back empty (`MaxResults = 0` returns everything in one call).
- **`Object_List` folder navigation** — new `Delimiter` input (typically `/`) and `PrefixList` output that groups nested objects into "folders", backed by a new **`Prefix`** structure.
- **`Object_GetSignedUrl` content-type binding** — new optional `ContentType` parameter. When set, it becomes part of the V4 signature, so an upload (`PUT`) is only accepted if the client sends exactly that `Content-Type`.

### Changed

- **Signed-URL validation** — `ExpirationMinutes` must be between `1` and `10080` (the 7-day maximum for a V4 signed URL); out-of-range values raise a clear error instead of failing at Google.
- **Cached, thread-safe clients** — `StorageClient` and `UrlSigner` are now cached per service account (keyed by a SHA-256 hash of the credentials, never the raw key) and reused across requests, avoiding per-call RSA parsing and `HttpClient` allocation.
- **Actionable error messages** — Google Cloud Storage failures are translated into clear guidance (missing bucket vs. missing object, access denied, unauthenticated, bucket-not-empty, credential mismatch), with the original exception preserved as the inner exception.
- **`Object_Move` failure clarity** — because a move is copy-then-delete, if the copy succeeds but the source delete fails, the connector now reports explicitly that both objects currently exist rather than surfacing a generic error.

### Fixed

- Defensive RFC3339 timestamp parsing for object and bucket timestamps.
- Clearer error when the `PrivateKey` cannot be parsed, pointing at the expected `-----BEGIN/END PRIVATE KEY-----` format.
- 404 disambiguation so a missing bucket and a missing object no longer produce the same message.

### ⚠️ Breaking changes

- **`Object_List` signature changed.** It gained required inputs (`MaxResults`, `PageToken`, `Delimiter`) and outputs (`NextPageToken`, `PrefixList`). Apps that consume `Object_List` in Service Studio must remap the action after upgrading. All other changes are backward-compatible — the new `ContentType` on `Object_GetSignedUrl` is optional.

[1.7.0]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.7.0
[1.6.1]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.6.1
[1.6.0]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.6.0
[1.5.2]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.5.2
[1.5.0]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.5.0
[1.4.0]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.4.0
