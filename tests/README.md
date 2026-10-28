# Test Suite

Tests every action the connector exposes to ODC developers — **no Google account or credentials required**.

The tests run the real connector through its `IGoogleCloudStorage` interface, exactly as the ODC runtime calls it.

## How it works

- **Offline tests** (signed URLs, input validation, client caching) need no server at all. V4 URL signing is local RSA cryptography, performed with a throwaway 2048-bit key generated in-memory on first run — so there is nothing to install and no secret to manage.
- **Integration tests** run the connector against [fake-gcs-server](https://github.com/fsouza/fake-gcs-server), a local in-memory Google Cloud Storage emulator, via the connector's `GCSCONNECTOR_EMULATOR_HOST` hook (which it honors only when that variable is set — it is never set on a real ODC server, where the connector always talks to production GCS).

The test fixture starts the emulator automatically. If it cannot (e.g. no network and no cached binary), the integration tests **skip** rather than fail, so the offline tests still give a green run on any machine.

## Running

```bash
# Everything (downloads the ~11 MB emulator binary on first run, then caches it)
dotnet test

# Offline tests only — no emulator, no network
dotnet test --filter "FullyQualifiedName~OfflineTests"
```

### Pointing at your own emulator

- Set `GCSCONNECTOR_EMULATOR_HOST` (e.g. `127.0.0.1:4443`) and the fixture uses that running server instead of downloading one.
- Set `FAKE_GCS_EXE` to an existing `fake-gcs-server` binary to skip the download.
- Set `FAKE_GCS_DIR` to control where the downloaded binary is cached (default: the system temp dir).

Prerequisites: the .NET 10 SDK, and `tar` on `PATH` (bundled with macOS, Linux, and Windows 10+) for extracting the downloaded emulator.

## Coverage

| Area | What's verified |
|---|---|
| `Bucket_Create` / `Bucket_Exists` / `Bucket_List` / `Bucket_Delete` | Full lifecycle, duplicate-name error, non-empty-delete error, `Created` timestamps |
| `Object_Upload` / `Object_Download` | Byte-exact round-trips: text, binary, empty (0 bytes), 5 MB, unicode names, `ContentType` preservation |
| `Object_GetMetadata` | Name, bucket, size, content type, hashes, generation, timestamps; missing object → `Exists = False` |
| `Object_Exists` | Present and missing objects |
| `Object_List` | Flat listing, prefix filter, delimiter folders (`PrefixList`), prefix + delimiter, pagination via `MaxResults`/`NextPageToken` |
| `Object_Copy` / `Object_Move` / `Object_Delete` | Cross-bucket copy/move semantics, source retention/removal; Move onto itself refused (object kept); a missing destination bucket vs. a missing source named correctly for Copy and Move |
| `Object_GetSignedUrl` | V4 URL structure, operation case-insensitivity, `ContentType` signed for `Upload` only, line breaks in `ContentType` rejected, empty `BucketName`/`ObjectName` rejected, expiration bounds (0, > 7 days, exactly 7 days), JSON-escaped key |
| Error handling | Missing object/bucket errors (including a bucket named like `acme-bucket`), garbage private key → friendly parse error, negative `MaxResults`, required names on every action, network and timeout messages, `Object_UpdateMetadata` sends generation **and** metageneration preconditions and explains a 412 |
| Caching | `StorageClient`/`UrlSigner` instance reuse, per-credential isolation, key and federation never sharing a client; the expiring cache with a fake clock (sliding 60 min, absolute expiry, a throwing factory leaves nothing, LRU bound); length-prefixed cache keys; one `ServiceAccountCredential` shared by client and signer; 80 s Storage request timeout |
| Custom metadata | Upload round-trip, `Object_UpdateMetadata` add/overwrite/remove with content integrity, `Object_DeleteByPrefix` counts and guards, and its time budget (stops with "Deleted 0 of 3 … call again", nothing lost, then resumes) |
| ODC limits and small fixes | `Object_Download` refuses one byte over the 5.5 MB cap and returns exactly the cap (every write path of the capped stream), null upload content, empty `ContentType` and `Location`, non-null list text outputs, timestamps read as UTC |
| Upload integrity | Uploads send `x-goog-hash: crc32c=…` matching an independent CRC32C of the exact bytes (reference value checked against RFC 3720), the bytes are stored unchanged, and data corrupted in transit is rejected with a friendly "nothing was stored, retry" error |
| Auth method switch | Empty/explicit/case-insensitive `ServiceAccountKey`, invalid method, missing-field messages for both methods, provider formats, https-only token endpoint |
| Federation protocol (contract) | Client-credentials request, RFC 8693 STS exchange, service account impersonation, Storage called with the short-lived token, supplied `SubjectToken`, `client_secret_basic` fallback, and friendly errors for a rejected client, a non-JWT token, an STS rejection, or a missing Token Creator role |
| Federation hardening | Each supplied token gets its own exchange (never another caller's), expired tokens rejected before any request (30 s skew), cache expiry at the token's `exp`; JWT `exp` parsing edge cases; token endpoint: user info rejected and never echoed, loopback `http` only in tests, redirects not followed, no raw bodies or query strings in messages (checked on the whole exception chain), timeouts reported as such, client-credentials lifetime capped at the JWT's `exp` |
| Federation live (CI only) | Real Google Cloud via GitHub OIDC: the federated identity is accepted by Storage, and the signed URL's signature verifies against the service account's published certificate (the verifier itself is proven offline) |

The **live** tests run only in GitHub Actions (job `live-federation`, with `id-token: write` and the `GCP_WIF_PROVIDER`, `GCP_SERVICE_ACCOUNT`, `GCP_PROJECT_ID` repository variables). Everywhere else they skip. They use no secrets: GitHub's OIDC token is the identity provider.
