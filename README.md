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
├── Resources/                  # Embedded branded icons
└── Structures/                 # Strongly-typed ODC structures
```

The connector is architected as an **adapter**. It bridges the OutSystems Developer Cloud runtime with the official Google Cloud Storage .NET SDK using the **Bridge Pattern**. This ensures that the OutSystems application logic remains decoupled from the low-level SDK implementation details.

### Key Architectural Decisions:
- **Cached, thread-safe clients:** `StorageClient` and `UrlSigner` instances are cached per service account (keyed by a SHA-256 hash of the credentials, never the raw key) and reused across requests. This avoids re-parsing the RSA private key and allocating a new `HttpClient` on every call — both types are thread-safe, so sharing them is safe under high concurrency and prevents socket exhaustion.
- **Actionable errors:** Google API failures are translated into clear, actionable messages (missing bucket vs. object, access denied, unauthenticated, bucket-not-empty, credential mismatch), with the original exception preserved as the inner exception for diagnostics.
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
  - `Service Account Token Creator` (mandatory for **Signed URLs**)

---

## Quick Start

```bash
# Build the project
dotnet build GoogleCloudStorage.csproj -c Release

# Publish for ODC (standard deployment)
dotnet publish GoogleCloudStorage.csproj -c Release -f net10.0 --no-self-contained
```

After publishing, zip the contents of the `publish/` folder (**excluding** `OutSystems.ExternalLibraries.SDK.dll`) and upload it to the ODC Portal.

---

## Authentication

Authentication is handled via the `Authentication` structure. Credentials should be stored securely in **ODC App Settings (Site Properties)** and passed to each action at runtime.

| Parameter | Source in GCP JSON | Description |
|-----------|-------------------|-------------|
| `ProjectId` | `project_id` | Your Google Cloud Project ID |
| `ClientEmail` | `client_email` | Service Account identification email |
| `PrivateKey` | `private_key` | Full RSA Private Key (with BEGIN/END headers) |

---

## Action Reference

### Object Operations

#### `Object_Upload`
Persists a file to a specific GCS bucket.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Destination bucket |
| `objectName` | `Text` | Full path/filename in the bucket |
| `file` | `File` | Structure containing Binary Content and ContentType |

#### `Object_Download`
Retrieves a file and its metadata from GCS.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Source bucket |
| `objectName` | `Text` | Full path/filename in the bucket |

**Outputs:**
| Output | Type | Description |
|--------|------|-------------|
| `file` | `File` | Structure containing Binary Content and system ContentType |

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

> **Pagination:** pass a `MaxResults` greater than `0` to return a single page, then feed the returned `NextPageToken` back as `PageToken` until it comes back empty. With `MaxResults = 0` every object is returned in one call (no `NextPageToken`).

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

#### `Object_GetMetadata`
Retrieves an object's full metadata (size, content type, hashes, generation, storage class, timestamps) without downloading its content. Returns `Exists = False` if the object is not found, leaving the `metadata` output empty.

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

#### `Object_Delete`
Permanently removes an object from a bucket.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Source bucket |
| `objectName` | `Text` | Full path/filename to delete |

#### `Object_Copy`
Copies an object to another location, within the same bucket or across buckets, without downloading its content. Overwrites the destination if it exists.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `sourceBucketName` | `Text` | Bucket that currently contains the object |
| `sourceObjectName` | `Text` | Full path/filename of the source object |
| `destinationBucketName` | `Text` | Bucket to copy into (can equal the source) |
| `destinationObjectName` | `Text` | Full path/filename for the destination |

#### `Object_Move`
Moves an object to another location (copy + delete of the source), within the same bucket or across buckets. Use the same source and destination bucket to rename. Overwrites the destination if it exists.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `sourceBucketName` | `Text` | Bucket that currently contains the object |
| `sourceObjectName` | `Text` | Full path/filename of the source object |
| `destinationBucketName` | `Text` | Bucket to move into (can equal the source) |
| `destinationObjectName` | `Text` | Full path/filename for the destination |

> **Note:** Move is copy-then-delete and is not atomic — the source is removed only after a successful copy.

#### `Object_GetSignedUrl`
Generates a time-limited V4 signed URL for secure, direct-to-browser file access. The `operation` controls the action the URL permits: download, upload, or delete.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Source bucket |
| `objectName` | `Text` | Full path/filename |
| `expirationMinutes` | `Integer` | Link validity duration, `1`–`10080` (a V4 signed URL is valid for at most 7 days). Values outside this range raise a clear error. |
| `operation` | `Text` | Optional. `Download` (GET), `Upload` (PUT), or `Delete` (DELETE). Case-insensitive. Defaults to `Download`. |
| `contentType` | `Text` | Optional, for `Upload` URLs. The exact `Content-Type` the client will send in the PUT request. It becomes part of the signature, so Google rejects uploads with a different `Content-Type`. Leave empty to allow any. |

**Outputs:**
| Output | Type | Description |
|--------|------|-------------|
| `url` | `Text` | Temporary secure URL. For `Upload`, the client sends an HTTP PUT with the file as the body. |

> **Multi-upload:** signed URLs are bound to a specific object path, so request one `Upload` URL per file (pass each file's `objectName`).
>
> **Content-Type binding:** if you pass `contentType`, the client's PUT must send exactly that `Content-Type` header, or Google rejects the upload with a signature mismatch. Leave it empty to accept any content type.

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
Provisions a new globally unique storage container.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Globally unique name |
| `location` | `Text` | Geographic region (e.g., `US`, `EU`, `asia-east1`) |

#### `Bucket_Delete`
Decommissioning of an empty storage container.

**Arguments:**
| Argument | Type | Description |
|----------|------|-------------|
| `authentication` | `Authentication` | GCP credentials |
| `bucketName` | `Text` | Name of the bucket to delete |

#### `Bucket_Exists`
Checks whether a bucket exists and is accessible to the service account, without listing its contents.

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
Encapsulates Google Cloud Service Account credentials.
- `ProjectId`: Text
- `ClientEmail`: Text
- `PrivateKey`: Text

### `File`
Used for binary data exchange.
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
├── GoogleCloudStorage.csproj   # Dependencies: Google.Cloud.Storage.V1, Google.Apis.Auth
├── IGoogleCloudStorage.cs      # OSInterface & OSAction definitions
├── GoogleCloudStorage.cs       # StorageClient implementation & credential handling
├── Resources/                  # Branding assets
│   ├── app_icon.png            # Library icon
│   └── action_icon.png         # Action-level icon
└── Structures/                 # ODC-compatible structs
    ├── Authentication.cs       # Credential model
    ├── File.cs                 # Binary wrapper
    ├── Bucket.cs               # Container metadata
    ├── Object.cs               # File metadata (list entry)
    ├── ObjectMetadata.cs       # Full object metadata
    └── Prefix.cs               # Folder-style entry (Object_List with Delimiter)
```

---

## Build and Deployment

1. **Publish:** Run `dotnet publish` as shown in Quick Start.
2. **Clean:** Delete `OutSystems.ExternalLibraries.SDK.dll` from the `publish/` directory.
3. **Zip:** Compress all remaining files into a flat structure (no subfolders).
4. **Deploy:** Upload to ODC Portal > External Logic.

---

## Best Practices

- **Security:** Mark `PrivateKey` as a **Secret** App Setting in ODC to ensure it is encrypted and masked in logs.
- **Efficiency:** For files larger than 100MB, always use `Object_GetSignedUrl` to avoid server-side memory pressure.
- **Naming:** Follow GCS bucket naming constraints (3-63 characters, lowercase letters, numbers, and hyphens).

---

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
