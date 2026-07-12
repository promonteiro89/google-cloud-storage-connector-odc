# Changelog

All notable changes to the **Google Cloud Storage Connector for ODC** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[1.4.0]: https://github.com/promonteiro89/google-cloud-storage-connector-odc/releases/tag/v1.4.0
