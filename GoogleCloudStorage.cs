using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Storage.v1;
using Google.Cloud.Storage.V1;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using File = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.File;
using Object = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Object;

namespace OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;

public class GoogleCloudStorage : IGoogleCloudStorage
{
    /// <summary>
    /// Caches of StorageClient/UrlSigner instances per service account. Actions run on every
    /// request, and creating a StorageClient per call parses the RSA private key and allocates a
    /// new HttpClient each time (latency + socket exhaustion under load). Statics survive across
    /// requests in the ODC runtime, and both StorageClient and UrlSigner are thread-safe, so they
    /// are safe to share. Keys are SHA-256 hashes of the credentials, so raw private keys are never
    /// retained as cache keys. In practice an application uses one or few service accounts, so these
    /// caches stay small.
    /// </summary>
    private static readonly ConcurrentDictionary<string, StorageClient> StorageClientCache = new();
    private static readonly ConcurrentDictionary<string, UrlSigner> UrlSignerCache = new();

    public void Object_Upload(Authentication authentication, string bucketName, string objectName, File file)
    {
        var storageClient = GetStorageClient(authentication);
        try
        {
            using var stream = new MemoryStream(file.Content);
            storageClient.UploadObject(bucketName, objectName, file.ContentType, stream);
        }
        catch (Google.GoogleApiException e) { throw FriendlyException(e, authentication.ClientEmail, bucketName, null); }
        catch (TokenResponseException e) { throw FriendlyAuthException(e, authentication.ClientEmail); }
    }

    public void Object_Download(Authentication authentication, string bucketName, string objectName, out File file)
    {
        var storageClient = GetStorageClient(authentication);
        try
        {
            using var stream = new MemoryStream();
            var obj = storageClient.DownloadObject(bucketName, objectName, stream);

            file = new File
            {
                Content = stream.ToArray(),
                ContentType = obj.ContentType
            };
        }
        catch (Google.GoogleApiException e) { throw FriendlyException(e, authentication.ClientEmail, bucketName, objectName); }
        catch (TokenResponseException e) { throw FriendlyAuthException(e, authentication.ClientEmail); }
    }

    public void Object_List(Authentication authentication, string bucketName, string prefix, int maxResults, string pageToken, string delimiter, out IEnumerable<Object> objects, out string nextPageToken, out IEnumerable<Prefix> prefixList)
    {
        var storageClient = GetStorageClient(authentication);
        var objectResults = new List<Object>();
        var prefixResults = new List<Prefix>();
        nextPageToken = string.Empty;

        if (maxResults < 0)
            throw new ArgumentException($"MaxResults cannot be negative (received {maxResults}). Use 0 to return all objects.");

        try
        {
            var options = new ListObjectsOptions();
            if (maxResults > 0) options.PageSize = maxResults;
            if (!string.IsNullOrEmpty(pageToken)) options.PageToken = pageToken;
            if (!string.IsNullOrEmpty(delimiter)) options.Delimiter = delimiter;

            // Iterate the raw API responses (one per HTTP request) so the continuation
            // token and the common prefixes ("folders") are available, not just the items.
            var seenPrefixes = new HashSet<string>();
            foreach (var page in storageClient.ListObjects(bucketName, prefix, options).AsRawResponses())
            {
                if (page.Items != null)
                {
                    foreach (var obj in page.Items)
                    {
                        objectResults.Add(new Object
                        {
                            Name = obj.Name,
                            Size = (long)Math.Min(obj.Size ?? 0, long.MaxValue),
                            ContentType = obj.ContentType,
                            Updated = ParseTimestamp(obj.UpdatedRaw)
                        });
                    }
                }

                if (page.Prefixes != null)
                {
                    foreach (var commonPrefix in page.Prefixes)
                    {
                        if (!seenPrefixes.Add(commonPrefix)) continue;
                        prefixResults.Add(new Prefix { Value = commonPrefix });
                    }
                }

                if (maxResults > 0)
                {
                    // Paged mode: return exactly one page and hand back the continuation token.
                    nextPageToken = page.NextPageToken ?? string.Empty;
                    break;
                }
            }
        }
        catch (Google.GoogleApiException e) { throw FriendlyException(e, authentication.ClientEmail, bucketName, null); }
        catch (TokenResponseException e) { throw FriendlyAuthException(e, authentication.ClientEmail); }

        objects = objectResults;
        prefixList = prefixResults;
    }

    public void Object_Exists(Authentication authentication, string bucketName, string objectName, out bool exists)
    {
        var storageClient = GetStorageClient(authentication);
        try
        {
            storageClient.GetObject(bucketName, objectName);
            exists = true;
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound && !IsBucketNotFound(e))
        {
            exists = false;
        }
        catch (Google.GoogleApiException e) { throw FriendlyException(e, authentication.ClientEmail, bucketName, objectName); }
        catch (TokenResponseException e) { throw FriendlyAuthException(e, authentication.ClientEmail); }
    }

    public void Object_GetMetadata(Authentication authentication, string bucketName, string objectName, out bool exists, out ObjectMetadata metadata)
    {
        var storageClient = GetStorageClient(authentication);
        exists = false;
        metadata = new ObjectMetadata();

        try
        {
            var obj = storageClient.GetObject(bucketName, objectName);
            exists = true;

            metadata = new ObjectMetadata
            {
                Name = obj.Name ?? string.Empty,
                Bucket = obj.Bucket ?? string.Empty,
                Size = (long)Math.Min(obj.Size ?? 0, long.MaxValue),
                ContentType = obj.ContentType ?? string.Empty,
                ContentEncoding = obj.ContentEncoding ?? string.Empty,
                ContentDisposition = obj.ContentDisposition ?? string.Empty,
                CacheControl = obj.CacheControl ?? string.Empty,
                MD5Hash = obj.Md5Hash ?? string.Empty,
                Crc32c = obj.Crc32c ?? string.Empty,
                ETag = obj.ETag ?? string.Empty,
                Generation = obj.Generation ?? 0,
                Metageneration = obj.Metageneration ?? 0,
                StorageClass = obj.StorageClass ?? string.Empty,
                MediaLink = obj.MediaLink ?? string.Empty,
                TimeCreated = ParseTimestamp(obj.TimeCreatedRaw),
                Updated = ParseTimestamp(obj.UpdatedRaw)
            };
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound && !IsBucketNotFound(e))
        {
            exists = false;
        }
        catch (Google.GoogleApiException e) { throw FriendlyException(e, authentication.ClientEmail, bucketName, objectName); }
        catch (TokenResponseException e) { throw FriendlyAuthException(e, authentication.ClientEmail); }
    }

    public void Object_Delete(Authentication authentication, string bucketName, string objectName)
    {
        var storageClient = GetStorageClient(authentication);
        try
        {
            storageClient.DeleteObject(bucketName, objectName);
        }
        catch (Google.GoogleApiException e) { throw FriendlyException(e, authentication.ClientEmail, bucketName, objectName); }
        catch (TokenResponseException e) { throw FriendlyAuthException(e, authentication.ClientEmail); }
    }

    public void Object_Copy(Authentication authentication, string sourceBucketName, string sourceObjectName, string destinationBucketName, string destinationObjectName)
    {
        var storageClient = GetStorageClient(authentication);
        try
        {
            storageClient.CopyObject(sourceBucketName, sourceObjectName, destinationBucketName, destinationObjectName);
        }
        catch (Google.GoogleApiException e) { throw FriendlyException(e, authentication.ClientEmail, sourceBucketName, sourceObjectName); }
        catch (TokenResponseException e) { throw FriendlyAuthException(e, authentication.ClientEmail); }
    }

    public void Object_Move(Authentication authentication, string sourceBucketName, string sourceObjectName, string destinationBucketName, string destinationObjectName)
    {
        var storageClient = GetStorageClient(authentication);
        try
        {
            storageClient.CopyObject(sourceBucketName, sourceObjectName, destinationBucketName, destinationObjectName);
        }
        catch (Google.GoogleApiException e) { throw FriendlyException(e, authentication.ClientEmail, sourceBucketName, sourceObjectName); }
        catch (TokenResponseException e) { throw FriendlyAuthException(e, authentication.ClientEmail); }

        // Move is copy-then-delete and is not atomic: if the delete fails, both objects exist.
        // Surface that state explicitly instead of a generic error.
        try
        {
            storageClient.DeleteObject(sourceBucketName, sourceObjectName);
        }
        catch (Google.GoogleApiException e)
        {
            throw new Exception($"The object was copied to '{destinationBucketName}/{destinationObjectName}' but the source '{sourceBucketName}/{sourceObjectName}' could not be deleted - both objects currently exist. Cause: {e.Message}", e);
        }
    }

    public void Object_GetSignedUrl(Authentication authentication, string bucketName, string objectName, int expirationMinutes, out string url, string operation = "Download", string contentType = "")
    {
        if (expirationMinutes <= 0)
            throw new ArgumentException($"ExpirationMinutes must be greater than zero (received {expirationMinutes}).");
        if (expirationMinutes > 10080)
            throw new ArgumentException($"ExpirationMinutes cannot exceed 10080 minutes (7 days), the maximum validity of a Google Cloud V4 signed URL (received {expirationMinutes}).");

        var urlSigner = GetUrlSigner(authentication);

        var method = (operation ?? "").Trim().ToUpperInvariant() switch
        {
            "DOWNLOAD" => HttpMethod.Get,
            "UPLOAD" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            _ => throw new ArgumentException($"Invalid Operation '{operation}'. Use 'Download', 'Upload', or 'Delete'.")
        };

        var template = UrlSigner.RequestTemplate
            .FromBucket(bucketName)
            .WithObjectName(objectName)
            .WithHttpMethod(method);

        // When a ContentType is provided it becomes part of the signature, so Google
        // rejects requests whose Content-Type header does not match (relevant for Upload).
        if (!string.IsNullOrEmpty(contentType))
        {
            template = template.WithContentHeaders(new Dictionary<string, IEnumerable<string>>
            {
                { "Content-Type", new[] { contentType } }
            });
        }

        url = urlSigner.Sign(template, UrlSigner.Options.FromDuration(TimeSpan.FromMinutes(expirationMinutes)));
    }

    public void Bucket_List(Authentication authentication, out IEnumerable<Bucket> buckets)
    {
        var storageClient = GetStorageClient(authentication);
        try
        {
            var gcsBuckets = storageClient.ListBuckets(authentication.ProjectId);

            buckets = gcsBuckets.Select(b => new Bucket
            {
                Name = b.Name,
                Location = b.Location,
                StorageClass = b.StorageClass,
                Created = ParseTimestamp(b.TimeCreatedRaw)
            }).ToList();
        }
        catch (Google.GoogleApiException e) { throw FriendlyException(e, authentication.ClientEmail, null, null); }
        catch (TokenResponseException e) { throw FriendlyAuthException(e, authentication.ClientEmail); }
    }

    public void Bucket_Create(Authentication authentication, string bucketName, string location)
    {
        var storageClient = GetStorageClient(authentication);
        try
        {
            storageClient.CreateBucket(
                authentication.ProjectId,
                new Google.Apis.Storage.v1.Data.Bucket
                {
                    Name = bucketName,
                    Location = location
                }
            );
        }
        catch (Google.GoogleApiException e) { throw FriendlyException(e, authentication.ClientEmail, bucketName, null); }
        catch (TokenResponseException e) { throw FriendlyAuthException(e, authentication.ClientEmail); }
    }

    public void Bucket_Delete(Authentication authentication, string bucketName)
    {
        var storageClient = GetStorageClient(authentication);
        try
        {
            storageClient.DeleteBucket(bucketName);
        }
        catch (Google.GoogleApiException e) { throw FriendlyException(e, authentication.ClientEmail, bucketName, null); }
        catch (TokenResponseException e) { throw FriendlyAuthException(e, authentication.ClientEmail); }
    }

    public void Bucket_Exists(Authentication authentication, string bucketName, out bool exists)
    {
        var storageClient = GetStorageClient(authentication);
        try
        {
            storageClient.GetBucket(bucketName);
            exists = true;
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            exists = false;
        }
        catch (Google.GoogleApiException e) { throw FriendlyException(e, authentication.ClientEmail, bucketName, null); }
        catch (TokenResponseException e) { throw FriendlyAuthException(e, authentication.ClientEmail); }
    }

    // ---- Credential / client helpers -------------------------------------------------

    /// <summary>
    /// Computes a cache key from the service account credentials (never the raw private key).
    /// </summary>
    private static string GetCredentialCacheKey(string clientEmail, string privateKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(clientEmail + "\n" + privateKey));
        return Convert.ToBase64String(hash);
    }

    private static ServiceAccountCredential GetServiceAccountCredential(Authentication authentication)
    {
        try
        {
            var initializer = new ServiceAccountCredential.Initializer(authentication.ClientEmail)
            {
                Scopes = new[] { StorageService.Scope.CloudPlatform }
            }.FromPrivateKey(authentication.PrivateKey.Replace("\\n", "\n"));

            return new ServiceAccountCredential(initializer);
        }
        catch (Exception e)
        {
            throw new ArgumentException("The PrivateKey could not be parsed. Provide the full 'private_key' value from the service account JSON key, including the -----BEGIN PRIVATE KEY----- and -----END PRIVATE KEY----- lines.", e);
        }
    }

    /// <summary>
    /// Returns a cached StorageClient for the given service account, creating it on first use.
    /// Honors the GCSCONNECTOR_EMULATOR_HOST environment variable (never set on a real ODC
    /// server): when present, connects unauthenticated to a local GCS emulator such as
    /// fake-gcs-server, enabling integration tests without Google credentials. The name is
    /// deliberately extension-specific (not Google's STORAGE_EMULATOR_HOST) so a machine-wide
    /// variable set for other tooling can never silently redirect this connector.
    /// </summary>
    private static StorageClient GetStorageClient(Authentication authentication)
    {
        string? emulatorHost = Environment.GetEnvironmentVariable("GCSCONNECTOR_EMULATOR_HOST");
        if (!string.IsNullOrEmpty(emulatorHost))
        {
            string baseUri = (emulatorHost.Contains("://") ? emulatorHost : "http://" + emulatorHost).TrimEnd('/') + "/storage/v1/";
            return StorageClientCache.GetOrAdd(
                "emulator|" + baseUri,
                _ => new StorageClientBuilder { BaseUri = baseUri, UnauthenticatedAccess = true }.Build());
        }

        return StorageClientCache.GetOrAdd(
            GetCredentialCacheKey(authentication.ClientEmail, authentication.PrivateKey),
            _ => StorageClient.Create(GetServiceAccountCredential(authentication).ToGoogleCredential()));
    }

    /// <summary>
    /// Returns a cached UrlSigner for the given service account, creating it on first use.
    /// </summary>
    private static UrlSigner GetUrlSigner(Authentication authentication)
    {
        return UrlSignerCache.GetOrAdd(
            GetCredentialCacheKey(authentication.ClientEmail, authentication.PrivateKey),
            _ => UrlSigner.FromCredential(GetServiceAccountCredential(authentication)));
    }

    /// <summary>
    /// Parses a GCS RFC3339 timestamp defensively. Parsing the raw string with a flexible parser
    /// tolerates any format drift and returns 1900-01-01 (the OutSystems null date) when missing
    /// or unparseable.
    /// </summary>
    private static DateTime ParseTimestamp(string? raw)
    {
        if (!string.IsNullOrEmpty(raw) && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return dto.UtcDateTime;
        return new DateTime(1900, 1, 1);
    }

    // ---- Error translation -----------------------------------------------------------

    /// <summary>
    /// True when a 404 from GCS refers to the bucket itself rather than an object inside it
    /// (Google reports "The specified bucket does not exist." vs "No such object: ...").
    /// </summary>
    private static bool IsBucketNotFound(Google.GoogleApiException e)
    {
        string msg = (e.Error?.Message) ?? e.Message ?? "";
        return msg.IndexOf("bucket", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Translates a GoogleApiException into an exception with an actionable message for OutSystems
    /// logs, instead of Google's raw API error. The original exception is kept as InnerException.
    /// </summary>
    private static Exception FriendlyException(Google.GoogleApiException e, string clientEmail, string? bucketName, string? objectName)
    {
        string details = e.Error != null && !string.IsNullOrEmpty(e.Error.Message) ? e.Error.Message : e.Message;

        if (e.HttpStatusCode == HttpStatusCode.NotFound && bucketName != null)
        {
            if (objectName != null && !IsBucketNotFound(e))
                return new Exception($"Object '{objectName}' was not found in bucket '{bucketName}'. Details: {details}", e);
            return new Exception($"Bucket '{bucketName}' does not exist (names are case-sensitive and must match exactly). Details: {details}", e);
        }
        if (e.HttpStatusCode == HttpStatusCode.Forbidden)
            return new Exception($"Access denied for service account '{clientEmail}'. Grant it the required IAM role in Google Cloud (Storage Object Admin for object operations, Storage Admin for bucket operations). Details: {details}", e);
        if (e.HttpStatusCode == HttpStatusCode.Unauthorized)
            return new Exception($"Google rejected the request as unauthenticated. Check that ClientEmail and PrivateKey belong to the same service account and that the key has not been revoked. Details: {details}", e);
        if (e.HttpStatusCode == HttpStatusCode.Conflict)
        {
            if (details.IndexOf("not empty", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Exception($"Bucket '{bucketName}' is not empty. Delete all objects in it before deleting the bucket. Details: {details}", e);
            return new Exception($"Conflict: {details} (for Bucket_Create this usually means the name is already taken - bucket names are global across all of Google Cloud Storage).", e);
        }
        return new Exception($"Google Cloud Storage error ({(int)e.HttpStatusCode} {e.HttpStatusCode}): {details}", e);
    }

    /// <summary>
    /// Translates a token endpoint failure (typically 'invalid_grant') into an actionable message.
    /// </summary>
    private static Exception FriendlyAuthException(TokenResponseException e, string clientEmail)
    {
        return new Exception($"Google rejected the service account credentials for '{clientEmail}' (ClientEmail/PrivateKey mismatch, deleted service account, revoked key, or server clock skew). Details: {e.Message}", e);
    }
}
