# Changelog

All notable changes to the **Google Cloud Storage Connector for ODC** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[1.5.2]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.5.2
[1.5.0]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.5.0
[1.4.0]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.4.0
