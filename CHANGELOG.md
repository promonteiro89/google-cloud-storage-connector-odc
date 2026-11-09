# Changelog

All notable changes to the **Google Cloud Storage Connector for ODC** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0] - Unreleased

`Object_Upload` and `Object_Download` now work with ODC's **File** data type, so files are no longer limited by ODC's 5.5 MB action payload: they stream between ODC storage and Google, up to 2 GB. The Binary Data behaviour of 1.x stays available under new names. This is a breaking release: apps must update their calls (see **Migrating from 1.x**).

### Breaking changes

- **`Object_Upload` takes an ODC File** (from an Upload widget, an entity attribute or another action) and a new optional `ContentType` input. Empty means the File's own type.
- **`Object_Download` returns an ODC File.** The File is named after the last part of the object name and is temporary until the app saves it to an entity attribute.
- **The 1.x actions are renamed:** the in-memory versions are now `Object_UploadBinary` and `Object_DownloadBinary`, with the same 5.5 MB limit.
- **The `File` structure is renamed `BinaryContent`,** so it is no longer confused with ODC's File type in ODC Studio.

### Added

- **Large files through the connector.** On an ODC tenant:
  - **Uploads:** 200 MB took about 10 s to a bucket in a nearby region, and 22 s to one on another continent. 1 GB took 40 s to a nearby bucket.
  - **Downloads:** 200 MB took about 13 s.
  - **Integrity:** the content matched byte for byte in every run.
- **Uploads use 32 MB chunks.** Google confirms each chunk before the next one is sent, so larger chunks mean fewer waits. On ODC this was 1.4 to 2 times faster than the SDK's 10 MB default. Each upload holds one chunk in memory.
- **A clear error before ODC's 95-second limit.** A File upload or download that has not finished after 85 seconds, retries included, stops with a message, and nothing is stored. A download larger than 2 GB, the most an ODC File holds, is refused before any transfer. Both messages point to `Object_GetSignedUrl` for larger files.
- **Retries:**
  - an interrupted upload chunk is resent;
  - a failed upload start reopens the File and tries again;
  - downloads retry transient errors (408, 429, 5xx, network) as before, up to 3 attempts.
- **Download integrity:** downloads into a File check Google's CRC32C, as `Object_DownloadBinary` does.

### Fixed (from the O11 connector's 1.7.4 reviews)

- **`Object_Delete` reported "nothing was deleted" after a delete that had worked.** When Google applied a delete but its answer was lost, the repeated request answered 412, or 404 if the object was gone, and the action reported a failure. The connector now repeats the delete itself, with the Storage library's retry off, and treats a 412 or 404 on a repeat as deleted. A newer object written meanwhile is never touched. A 412 on the first request still reports that the object was replaced and nothing was deleted.
- **`Object_UpdateMetadata` said "nothing was changed" when only a repeat got a 412.** The first request may have been applied, so the message now says the update "may already have been applied" and asks to read the metadata before retrying.
- **`Object_Move` reported a failed source delete when only a repeat got a 412.** In that case the source was in fact deleted, so the move now succeeds.
- **Error messages could forge lines in the ODC error log.** Object names, prefixes, `Operation`, `AuthenticationMethod` and Google's error text went into messages unchanged. They are now cleaned: line breaks, other control characters and invisible text-direction characters (such as U+202E) become spaces, and each value is cut at 500 characters.
- **A URL-encoded `ClientSecret` was not masked.** If the identity provider echoed the secret back URL-encoded, it appeared in the error. Plain and URL-encoded forms are now masked.
- **The identity provider connection kept cookies.** It is shared by every app that uses the library, so a cookie the provider set for one app's request went with another's. Cookies are now off.
- **A token request could take about 120 seconds.** The 30-second limit applied separately to the headers and the body of each request, and the HTTP Basic retry doubled it. That passed ODC's 95-second action limit, so ODC killed the action without the connector's message. One 30-second deadline now covers the whole exchange.
- **A plain "Not Found" from an emulator or a proxy turned a missing bucket into a missing object** in `Object_Delete`, `Object_Download`, `Object_DownloadBinary`, `Object_UpdateMetadata`, `Object_Move` and `Object_Copy`. As `Object_Exists` and `Object_GetMetadata` already did, an unclear 404 is now settled by looking up the bucket.

### Packaging

- **The release zip now includes `OutSystems.ExternalLibraries.SDK.dll`.** ODC rejects a library with File parameters without it ("IOSFile could not be found", OS-ELGW-40004). The README and the NuGet-update instructions say so. The new dependency `OutSystems.ExternalLibraries.SDK.FileStorage` 1.2.1 is also in the zip.

### Migrating from 1.x

1. **Upload widget or entity File:** replace the 1.x `Object_Upload` call with the new `Object_Upload` and pass the File. There is no need to read its content into Binary Data first.
2. **Content built in memory** (generated PDFs, CSVs, REST responses), **or ODC Self-Hosted** (which has no File type): call `Object_UploadBinary` / `Object_DownloadBinary`, and rename the `File` structure to `BinaryContent` in your logic.
3. **Downloads:** either keep the content in memory with `Object_DownloadBinary`, or switch to `Object_Download` and save the returned File to an entity, or hand it to the browser with ODC's `GetFileSignedURL`.

### Known limits

- **Browser uploads need a signed-in user.** ODC rejects an anonymous upload to a File with 401.
- **Long transfers started from a screen:** ODC limits the server call to 60 seconds, so start very large transfers from a Timer or a Workflow.
- **ODC Self-Hosted:** untested. File doesn't exist there, so the File actions can't be used, and whether ODC Self-Hosted accepts a library that declares them is not yet verified.

### Tests

- 201 tests (169 in 1.7.2). The offline fake of Google now handles uploads sent in several chunks and resumed after a failure. A new in-memory fake of ODC's file storage covers:
  - chunked uploads;
  - the content-type override;
  - an empty File;
  - resumed and retried uploads;
  - the 85-second deadline both ways;
  - an ODC storage failure;
  - downloads into a File;
  - the 2 GB refusal;
  - a missing object;
  - File names.
- **New tests for the 1.7.4 fixes:**
  - a repeated delete answered 412 (the newer object stays) or 404;
  - three 503s on a delete (exactly three requests);
  - the two `Object_UpdateMetadata` 412 messages;
  - a repeated `Object_Move` source delete;
  - a log-forging object name, `Operation` and Google error text;
  - a URL-encoded secret, in both encodings;
  - the identity provider client's settings;
  - one deadline across both client-authentication attempts;
  - an unclear 404 on five actions, settled either way.
- **Each fix is proven:** breaking the 2 GB check, the chunk size, the reopen-on-retry, the resend tracking, the message cleaning, the encoded-secret masking, the cookie setting, the bucket check or the shared deadline makes a test fail.

## [1.7.2] - 2026-11-09

Hardening release from six independent reviews of 1.7.1 and a cross-check with the O11 connector: uploads, downloads and deletes survive transient Google errors, `Object_DeleteByPrefix` keeps to ODC's time limit on any prefix size, and identity-provider errors can no longer carry secrets into logs. No action, input or output changed.

### Security

- **Identity provider errors are sanitized.** The provider's `error` and `error_description` are cut to 300 characters, control characters become spaces (no forged log lines), and the `ClientSecret` is masked as `***` if the provider echoes it. Before, `error` had no length limit and the secret could reach ODC's logs.
- **A malformed `TokenEndpoint` is never echoed.** A value without `https://` (such as `idp.example.com/token?client_secret=...` or `user:password@host/token`) was repeated whole in the error message. Only a real http(s) URL is shown now, without query or user information.
- **The identity provider's response is limited to 1 MB.** A wrong `TokenEndpoint` pointing at a large file could fill the container's memory.
- **The credential caches stay within 500 entries at all times.** The cap was only applied by the once-a-minute sweep, so a burst of distinct `SubjectToken`s could grow them far past it.

### Fixed

- **`Object_DeleteByPrefix` works page by page.** It listed every object under the prefix into memory before deleting, outside its 75-second budget, so a large prefix could hit ODC's 95-second limit with nothing deleted. It now lists 1,000 objects at a time and deletes each page before fetching the next, checks the budget before every request, and cancels a request still running at 88 seconds. The error now says "Deleted N objects" (the total is no longer known in advance).
- **The 80-second timeout covers every call to Google.** The token exchange (STS), impersonation, URL signing (signBlob) and the service-account token request used .NET's default of 100 seconds, above ODC's 95-second limit.
- **`Object_Exists` and `Object_GetMetadata` no longer depend on Google's wording** to tell a missing bucket from a missing object. An ambiguous 404 is settled by looking up the bucket: only a 404 on the bucket makes it an error (as documented); if the lookup is not allowed (Storage Object Admin cannot read buckets), the object is reported missing, as before.
- **A download that timed out reported "One or more errors occurred."** The SDK's synchronous download wraps its failures in an `AggregateException`; the connector now unwraps it, so a timeout gives the connector's timeout message (and is not retried).
- **`Bucket_Create` with a wrong `ProjectId`** reported "Bucket does not exist (names are case-sensitive)". It now names the project.
- **`Object_Move`** blamed the destination bucket when the source was deleted and recreated during the move; it now reports that the source changed and that nothing was moved.
- **`Object_GetSignedUrl` checks `Operation` before any network call**, so a typo such as `downloads` no longer surfaces as a federation error or timeout.
- **`Object_Upload` rejects a `ContentType` with line breaks** with the same message as `Object_GetSignedUrl`, instead of a raw .NET `FormatException`.
- `ProjectId` is required by `Bucket_List` and `Bucket_Create` (checked before any request) and trimmed; `ClientEmail` is trimmed for the service-account key method.

### Changed

- **Transient errors are retried on uploads, downloads and deletes.** `Object_Upload` and `Object_Download` retry 408, 429, 5xx and network errors (up to 3 attempts, none started after 40 seconds; a timeout, an oversized download or another 4xx is never retried). `Object_Delete` reads the object's generation first and deletes only that generation, so Google's client retries it safely; an object replaced in the meantime is not deleted and the action reports it. With a role that may delete but not read objects (the read answers 403), it falls back to the unguarded delete of 1.7.1, which is not retried.

### Tests and CI

- 169 tests (127 in 1.7.1). The offline fake of Google now behaves like the real service: deletes and copies answer 404/412, listings are paged, and downloads are served with Google's checksum headers, including gzip transcoding. New tests cover every change above, plus the DeleteByPrefix 404 skip, the copy and move retries and gzip downloads, which no test covered before. Each new safety check is proven by a deliberate break that makes a test fail.
- CI fails when any non-live test is skipped (an emulator that failed to start used to pass as green), runs with read-only repository permissions, and pins every GitHub Action to a commit SHA.

### Documentation

- `Bucket_Exists` needs `storage.buckets.get`, which Storage Object Admin does not include. The DeleteByPrefix count can be slightly low when a retried delete had already succeeded. The retries and the `ProjectId` requirement are described per action.

## [1.7.1] - 2026-11-02

Hardening release from an independent review of 1.7.0: transient errors on copies and deletes are retried safely, metadata edits no longer rewrite the whole object, and no server-side setting can redirect storage traffic any more. No action, input or output changed.

### Security

- **The `GCSCONNECTOR_EMULATOR_HOST` switch is gone.** That environment variable redirected all storage traffic to an unauthenticated emulator. The connector now reads no environment variable or setting for it; the tests use an internal seam that only the test assembly can reach.

### Fixed

- **`Object_UpdateMetadata` sends a PATCH instead of a PUT**, with only the changed fields and the same generation and metageneration preconditions. A full PUT without the object's ACL could reset per-object ACLs on fine-grained buckets. Removed custom metadata keys are sent as `null`.
- **`Object_Copy` and `Object_Move` no longer guess which side is missing when the check fails.** After a 404, the source is checked again: only a 404 on that check means the source is missing. Any other failure (403, timeout, 5xx) gives a message naming both the source object and the destination bucket.
- **Empty or whitespace-only names** for buckets and objects are rejected by every action before any request, naming the parameter. Google's client-side validation errors no longer read "Google Cloud Storage error (0 0)".
- Network interruptions and download checksum mismatches (`IOException`) get readable messages, and identity-provider errors no longer end with a double period.

### Changed

- **Transient errors are retried where it is safe.** `Object_DeleteByPrefix` deletes each object only if it is still the generation it listed, so Google's client retries transient failures; an object replaced (412) or deleted (404) after the listing is skipped and not counted. `Object_Copy` and `Object_Move` copies are retried too (a move's copy is pinned to the source generation). `Object_Delete` is unchanged: without a generation a delete is not idempotent, so the caller retries.

### Dependencies

- **Refreshed the Google client libraries:** `Google.Apis` / `Google.Apis.Auth` / `Google.Apis.Core` `1.77.0` → **`1.78.0`**, `Google.Apis.Storage.v1` `1.77.0.4290` → **`1.77.0.4299`**, and `Google.Api.Gax` / `Google.Api.Gax.Rest` `4.15.1` → **`4.15.2`**. `Google.Cloud.Storage.V1` stays at `5.0.0` (the latest).
- **`OutSystems.ExternalLibraries.SDK` `1.5.0` → `1.7.0`.** It is used at build time only and is not shipped in the zip; the zip has the same files as 1.7.0.
- Test packages: `Microsoft.NET.Test.Sdk` `18.11.0`, `xunit` `2.9.3`, `xunit.runner.visualstudio` `4.0.1`, `Xunit.SkippableFact` `1.5.85`.

### Documentation

- Bucket naming rules (underscores and dots are allowed), the 1,000-entry page limit and folder entries counting towards `MaxResults`, Object Versioning and soft delete on deletes, gzip-encoded objects downloaded decompressed, and the project structure.

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

[1.7.2]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.7.2
[1.7.1]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.7.1
[1.7.0]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.7.0
[1.6.1]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.6.1
[1.6.0]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.6.0
[1.5.2]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.5.2
[1.5.0]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.5.0
[1.4.0]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.4.0
