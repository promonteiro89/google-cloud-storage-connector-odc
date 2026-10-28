using Google.Apis.Auth.OAuth2.Responses;
using Google.Cloud.Storage.V1;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using File = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.File;
using Object = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Object;

namespace OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;

// Authentication (both methods), client caches and auth-aware errors live in GoogleCloudStorage.Auth.cs.
public partial class GoogleCloudStorage : IGoogleCloudStorage
{
    /// <summary>
    /// The most bytes an ODC action can return in one payload (5.5 MB, as documented for the ODC
    /// action payload limit). Object_Download refuses to buffer more than this.
    /// </summary>
    internal const int MaxDownloadBytes = 5_767_168;

    /// <summary>
    /// The time one Object_DeleteByPrefix call may spend deleting before it stops and reports its
    /// partial progress (ODC limits an action to 95 seconds). Test seam.
    /// </summary>
    internal static TimeSpan DeleteByPrefixBudget { get; set; } = TimeSpan.FromSeconds(75);

    public void Object_Upload(Authentication authentication, string bucketName, string objectName, File file, IEnumerable<MetadataEntry> metadata)
    {
        Require(bucketName, "BucketName", isBucket: true);
        Require(objectName, "ObjectName", isBucket: false);

        var storageClient = GetStorageClient(authentication);
        try
        {
            var gcsObject = new Google.Apis.Storage.v1.Data.Object
            {
                Bucket = bucketName,
                Name = objectName,
                ContentType = string.IsNullOrEmpty(file.ContentType) ? null : file.ContentType,
                Metadata = ToMetadataDictionary(metadata)
            };

            using var stream = new MemoryStream(file.Content ?? Array.Empty<byte>());
            storageClient.UploadObject(gcsObject, stream);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, null); }
    }

    public void Object_Download(Authentication authentication, string bucketName, string objectName, out File file)
    {
        Require(bucketName, "BucketName", isBucket: true);
        Require(objectName, "ObjectName", isBucket: false);

        var storageClient = GetStorageClient(authentication);
        try
        {
            using var stream = new CappedDownloadStream();
            var obj = storageClient.DownloadObject(bucketName, objectName, stream,
                new DownloadObjectOptions { DownloadValidationMode = DownloadValidationMode.Automatic });

            file = new File
            {
                Content = stream.ToArray(),
                ContentType = obj.ContentType ?? ""
            };
        }
        catch (Exception e) when (FindInChain<DownloadTooLargeException>(e) != null)
        {
            // The SDK may wrap whatever the target stream throws, so look through the chain.
            throw new Exception($"Object '{objectName}' is larger than {MaxDownloadBytes} bytes (5.5 MB), the most an ODC action can return. Use Object_GetSignedUrl with Operation 'Download' and let the client download it directly from Google.");
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }
    }

    public void Object_List(Authentication authentication, string bucketName, string prefix, int maxResults, string pageToken, string delimiter, out IEnumerable<Object> objects, out string nextPageToken, out IEnumerable<Prefix> prefixList)
    {
        Require(bucketName, "BucketName", isBucket: true);

        var objectResults = new List<Object>();
        var prefixResults = new List<Prefix>();
        nextPageToken = string.Empty;

        if (maxResults < 0)
            throw new ArgumentException($"MaxResults cannot be negative (received {maxResults}). Use 0 to return all objects.");

        var storageClient = GetStorageClient(authentication);
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
                            Name = obj.Name ?? "",
                            Size = (long)Math.Min(obj.Size ?? 0, long.MaxValue),
                            ContentType = obj.ContentType ?? "",
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
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, null); }

        objects = objectResults;
        prefixList = prefixResults;
    }

    public void Object_Exists(Authentication authentication, string bucketName, string objectName, out bool exists)
    {
        Require(bucketName, "BucketName", isBucket: true);
        Require(objectName, "ObjectName", isBucket: false);

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
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }
    }

    public void Object_GetMetadata(Authentication authentication, string bucketName, string objectName, out bool exists, out ObjectMetadata metadata, out IEnumerable<MetadataEntry> customMetadata)
    {
        Require(bucketName, "BucketName", isBucket: true);
        Require(objectName, "ObjectName", isBucket: false);

        var storageClient = GetStorageClient(authentication);
        exists = false;
        metadata = new ObjectMetadata();
        var custom = new List<MetadataEntry>();

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

            if (obj.Metadata != null)
            {
                foreach (var kv in obj.Metadata)
                    custom.Add(new MetadataEntry { Key = kv.Key, Value = kv.Value ?? string.Empty });
            }
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound && !IsBucketNotFound(e))
        {
            exists = false;
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }

        customMetadata = custom;
    }

    public void Object_Delete(Authentication authentication, string bucketName, string objectName)
    {
        Require(bucketName, "BucketName", isBucket: true);
        Require(objectName, "ObjectName", isBucket: false);

        var storageClient = GetStorageClient(authentication);
        try
        {
            storageClient.DeleteObject(bucketName, objectName);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }
    }

    public void Object_Copy(Authentication authentication, string sourceBucketName, string sourceObjectName, string destinationBucketName, string destinationObjectName)
    {
        Require(sourceBucketName, "SourceBucketName", isBucket: true);
        Require(sourceObjectName, "SourceObjectName", isBucket: false);
        Require(destinationBucketName, "DestinationBucketName", isBucket: true);
        Require(destinationObjectName, "DestinationObjectName", isBucket: false);

        var storageClient = GetStorageClient(authentication);
        try
        {
            storageClient.CopyObject(sourceBucketName, sourceObjectName, destinationBucketName, destinationObjectName);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw CopyNotFoundException(storageClient, e, authentication, sourceBucketName, sourceObjectName, destinationBucketName);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, sourceBucketName, sourceObjectName); }
    }

    public void Object_Move(Authentication authentication, string sourceBucketName, string sourceObjectName, string destinationBucketName, string destinationObjectName)
    {
        Require(sourceBucketName, "SourceBucketName", isBucket: true);
        Require(sourceObjectName, "SourceObjectName", isBucket: false);
        Require(destinationBucketName, "DestinationBucketName", isBucket: true);
        Require(destinationObjectName, "DestinationObjectName", isBucket: false);

        // Copying an object onto itself and then deleting the source would destroy the only copy.
        if (string.Equals(sourceBucketName, destinationBucketName, StringComparison.Ordinal)
            && string.Equals(sourceObjectName, destinationObjectName, StringComparison.Ordinal))
            throw new ArgumentException($"Source and destination are the same object ('{sourceBucketName}/{sourceObjectName}'); Object_Move would delete it. Use a different destination name.");

        var storageClient = GetStorageClient(authentication);

        // Read the source before touching it: its generation guards both the copy and the delete,
        // so a source replaced by someone else mid-move is reported instead of being lost.
        // Nullable on purpose: a generation of 0 would mean "only if the object does not exist".
        long? generation = null;
        try
        {
            generation = storageClient.GetObject(sourceBucketName, sourceObjectName).Generation;
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, sourceBucketName, sourceObjectName); }

        try
        {
            storageClient.CopyObject(sourceBucketName, sourceObjectName, destinationBucketName, destinationObjectName,
                new CopyObjectOptions { IfSourceGenerationMatch = generation });
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new Exception($"The source object '{sourceBucketName}/{sourceObjectName}' changed while it was being moved; nothing was moved. Retry the move.", e);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw CopyNotFoundException(storageClient, e, authentication, sourceBucketName, sourceObjectName, destinationBucketName);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, sourceBucketName, sourceObjectName); }

        // Move is copy-then-delete and is not atomic: if the delete fails, both objects exist.
        // Surface that state explicitly instead of a generic error. The delete is guarded by the
        // generation read before the copy, so an overwritten source (newer data) is left alone.
        try
        {
            storageClient.DeleteObject(sourceBucketName, sourceObjectName,
                new DeleteObjectOptions { IfGenerationMatch = generation });
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // Already gone (deleted concurrently) - the move's end state is reached.
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new Exception($"The object was copied to '{destinationBucketName}/{destinationObjectName}' (generation {generation}), but the source '{sourceBucketName}/{sourceObjectName}' was modified during the move and was not deleted - both objects exist and the source holds newer data.", e);
        }
        catch (Exception e) when (IsTranslatable(e))
        {
            throw new Exception($"The object was copied to '{destinationBucketName}/{destinationObjectName}' but the source '{sourceBucketName}/{sourceObjectName}' could not be deleted - both objects currently exist. Cause: {TranslateException(e, authentication, sourceBucketName, sourceObjectName).Message}", e);
        }
    }

    public void Object_UpdateMetadata(Authentication authentication, string bucketName, string objectName, string contentType, string contentEncoding, string contentDisposition, string cacheControl, IEnumerable<MetadataEntry> metadata)
    {
        Require(bucketName, "BucketName", isBucket: true);
        Require(objectName, "ObjectName", isBucket: false);

        var changes = ToMetadataDictionary(metadata);
        bool hasFieldChange = !string.IsNullOrEmpty(contentType) || !string.IsNullOrEmpty(contentEncoding)
            || !string.IsNullOrEmpty(contentDisposition) || !string.IsNullOrEmpty(cacheControl);
        if (!hasFieldChange && changes == null)
            throw new ArgumentException("Nothing to update: provide at least one of ContentType, ContentEncoding, ContentDisposition, CacheControl, or a non-empty Metadata list.");

        var storageClient = GetStorageClient(authentication);

        try
        {
            // Read-modify-write: fetch the current object, apply only the provided changes, and
            // write it back guarded by preconditions so a concurrent change or content replacement
            // fails cleanly (412) instead of being silently overwritten.
            var obj = storageClient.GetObject(bucketName, objectName);

            if (!string.IsNullOrEmpty(contentType)) obj.ContentType = contentType;
            if (!string.IsNullOrEmpty(contentEncoding)) obj.ContentEncoding = contentEncoding;
            if (!string.IsNullOrEmpty(contentDisposition)) obj.ContentDisposition = contentDisposition;
            if (!string.IsNullOrEmpty(cacheControl)) obj.CacheControl = cacheControl;

            if (changes != null)
            {
                var merged = obj.Metadata != null ? new Dictionary<string, string>(obj.Metadata) : new Dictionary<string, string>();
                foreach (var kv in changes)
                {
                    if (kv.Value.Length == 0) merged.Remove(kv.Key);
                    else merged[kv.Key] = kv.Value;
                }
                obj.Metadata = merged;
            }

            try
            {
                // Setting any precondition turns off the SDK's automatic ones, so both are explicit: the
                // generation guards against a content replacement (which resets metageneration to 1).
                storageClient.UpdateObject(obj, new UpdateObjectOptions { IfGenerationMatch = obj.Generation, IfMetagenerationMatch = obj.Metageneration });
            }
            catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw new Exception($"Object '{objectName}' in bucket '{bucketName}' was changed by another request while its metadata was being updated, so nothing was changed. Read it again and retry.", e);
            }
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }
    }

    public void Object_DeleteByPrefix(Authentication authentication, string bucketName, string prefix, out long deletedCount)
    {
        Require(bucketName, "BucketName", isBucket: true);

        deletedCount = 0;
        var stopwatch = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Prefix cannot be empty - it would delete every object in the bucket. To do that intentionally, delete the bucket or list and delete the objects explicitly.");

        var storageClient = GetStorageClient(authentication);

        // Materialize the names first so deletions can't interfere with listing pagination.
        var names = new List<string>();
        try
        {
            foreach (var obj in storageClient.ListObjects(bucketName, prefix))
                names.Add(obj.Name);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, null); }

        long deleted = 0;
        foreach (var name in names)
        {
            // One call may not finish the work: stop before ODC kills the action and report where it
            // stopped, so the caller can resume with the same prefix.
            if (stopwatch.Elapsed >= DeleteByPrefixBudget)
                throw new Exception($"Deleted {deleted} of {names.Count} objects under prefix '{prefix}' before the {DeleteByPrefixBudget.TotalSeconds:0}-second time budget of one call ran out (ODC limits an action to 95 seconds). Nothing was rolled back; call Object_DeleteByPrefix again with the same prefix to delete the rest.");

            try
            {
                storageClient.DeleteObject(bucketName, name);
                deleted++;
            }
            catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
            {
                // Already gone (deleted concurrently) - the desired state is reached, keep going.
            }
            catch (Exception e) when (IsTranslatable(e))
            {
                // TranslateException gives the same message a whole-object call would give, so the
                // partial-progress error carries an actionable cause instead of Google's raw text.
                throw new Exception($"Deleted {deleted} of {names.Count} objects under prefix '{prefix}', then failed on '{name}': {TranslateException(e, authentication, bucketName, name).Message}", e);
            }
        }

        deletedCount = deleted;
    }

    public void Object_GetSignedUrl(Authentication authentication, string bucketName, string objectName, int expirationMinutes, out string url, string operation = "Download", string contentType = "")
    {
        Require(bucketName, "BucketName", isBucket: true);
        Require(objectName, "ObjectName", isBucket: false);

        if (expirationMinutes <= 0)
            throw new ArgumentException($"ExpirationMinutes must be greater than zero (received {expirationMinutes}).");
        if (expirationMinutes > 10080)
            throw new ArgumentException($"ExpirationMinutes cannot exceed 10080 minutes (7 days), the maximum validity of a Google Cloud V4 signed URL (received {expirationMinutes}).");

        // A line break would inject extra signed headers (or extra lines) into the canonical request.
        if (contentType != null && (contentType.Contains('\r') || contentType.Contains('\n')))
            throw new ArgumentException("ContentType cannot contain line breaks.");

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
        // rejects requests whose Content-Type header does not match. Only an Upload sends one;
        // signing it for Download/Delete would make the URL unusable.
        if (method == HttpMethod.Put && !string.IsNullOrEmpty(contentType))
        {
            template = template.WithContentHeaders(new Dictionary<string, IEnumerable<string>>
            {
                { "Content-Type", new[] { contentType } }
            });
        }

        try
        {
            url = urlSigner.Sign(template, UrlSigner.Options.FromDuration(TimeSpan.FromMinutes(expirationMinutes)));
        }
        catch (Google.GoogleApiException e) { throw FriendlySigningException(e, authentication); }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }
    }

    public void Bucket_List(Authentication authentication, out IEnumerable<Bucket> buckets)
    {
        var storageClient = GetStorageClient(authentication);
        try
        {
            var gcsBuckets = storageClient.ListBuckets(authentication.ProjectId);

            buckets = gcsBuckets.Select(b => new Bucket
            {
                Name = b.Name ?? "",
                Location = b.Location ?? "",
                StorageClass = b.StorageClass ?? "",
                Created = ParseTimestamp(b.TimeCreatedRaw)
            }).ToList();
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, null, null); }
    }

    public void Bucket_Create(Authentication authentication, string bucketName, string location)
    {
        Require(bucketName, "BucketName", isBucket: true);

        var storageClient = GetStorageClient(authentication);
        try
        {
            storageClient.CreateBucket(
                authentication.ProjectId,
                new Google.Apis.Storage.v1.Data.Bucket
                {
                    Name = bucketName,
                    Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim()
                }
            );
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, null); }
    }

    public void Bucket_Delete(Authentication authentication, string bucketName)
    {
        Require(bucketName, "BucketName", isBucket: true);

        var storageClient = GetStorageClient(authentication);
        try
        {
            storageClient.DeleteBucket(bucketName);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, null); }
    }

    public void Bucket_Exists(Authentication authentication, string bucketName, out bool exists)
    {
        Require(bucketName, "BucketName", isBucket: true);

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
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, null); }
    }

    // ---- Helpers ---------------------------------------------------------------------

    /// <summary>
    /// Rejects an empty required name before any client is created, so a missing input fails with
    /// an actionable message and never reaches the network. Bucket names reject whitespace-only
    /// values; object names reject only null/empty (spaces are legal in object names).
    /// </summary>
    private static void Require(string? value, string name, bool isBucket)
    {
        if (isBucket ? string.IsNullOrWhiteSpace(value) : string.IsNullOrEmpty(value))
            throw new ArgumentException($"{name} cannot be empty.");
    }

    /// <summary>
    /// A 404 from a copy (an object rewrite) means either the source object or the destination bucket
    /// is missing, and the message wording differs between Google and emulators. Asks again whether
    /// the source exists and names whichever side is missing.
    /// </summary>
    private static Exception CopyNotFoundException(StorageClient storageClient, Google.GoogleApiException e, Authentication authentication, string sourceBucketName, string sourceObjectName, string destinationBucketName)
    {
        bool sourceExists = true;
        try { storageClient.GetObject(sourceBucketName, sourceObjectName); }
        catch (Exception probe) when (IsTranslatable(probe)) { sourceExists = false; }
        return sourceExists
            ? FriendlyException(e, authentication, destinationBucketName, null)
            : FriendlyException(e, authentication, sourceBucketName, sourceObjectName);
    }

    /// <summary>
    /// The exception the capped download stream throws as soon as an object proves larger than
    /// <see cref="MaxDownloadBytes"/>. Private on purpose: it never escapes an action.
    /// </summary>
    private sealed class DownloadTooLargeException : Exception
    {
    }

    /// <summary>
    /// The buffer Object_Download writes into: a MemoryStream that refuses to grow past
    /// <see cref="MaxDownloadBytes"/>, so a huge object fails fast instead of exhausting memory.
    /// Every write path the SDK may use is capped.
    /// </summary>
    private sealed class CappedDownloadStream : MemoryStream
    {
        private void EnsureRoom(long additionalBytes)
        {
            if (Length + additionalBytes > MaxDownloadBytes)
                throw new DownloadTooLargeException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureRoom(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureRoom(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureRoom(1);
            base.WriteByte(value);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            EnsureRoom(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureRoom(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    /// <summary>
    /// Parses a GCS RFC3339 timestamp defensively. Parsing the raw string with a flexible parser
    /// tolerates any format drift and returns 1900-01-01 (the OutSystems null date) when missing
    /// or unparseable. Timestamps without an offset are treated as UTC (never as server-local time).
    /// </summary>
    internal static DateTime ParseTimestamp(string? raw)
    {
        if (!string.IsNullOrEmpty(raw) && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
            return dto.UtcDateTime;
        return new DateTime(1900, 1, 1);
    }

    /// <summary>
    /// Converts a MetadataEntry list to a dictionary. Entries with an empty Key are ignored; empty
    /// Values are preserved (Object_UpdateMetadata interprets them as key removal). Returns null
    /// when the list is null/empty or has no usable entries, meaning "no custom metadata provided".
    /// </summary>
    private static Dictionary<string, string>? ToMetadataDictionary(IEnumerable<MetadataEntry>? metadata)
    {
        if (metadata == null) return null;
        var dict = new Dictionary<string, string>();
        foreach (var entry in metadata)
        {
            if (string.IsNullOrEmpty(entry.Key)) continue;
            dict[entry.Key] = entry.Value ?? string.Empty;
        }
        return dict.Count > 0 ? dict : null;
    }

    // ---- Error translation -----------------------------------------------------------

    /// <summary>
    /// True when a 404 from GCS refers to the bucket itself rather than an object inside it
    /// (Google reports "The specified bucket does not exist." vs "No such object: ...").
    /// The object message names the bucket, so it is checked first.
    /// </summary>
    internal static bool IsBucketNotFound(Google.GoogleApiException e)
    {
        string msg = (e.Error?.Message) ?? e.Message ?? "";
        if (msg.IndexOf("No such object", StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        return msg.IndexOf("bucket", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// True for every exception type the actions translate into an actionable message: a Google API
    /// error, a token-endpoint failure, a subject-token failure, an identity provider failure, or
    /// the network/timeout failures of a request to Google.
    /// </summary>
    internal static bool IsTranslatable(Exception e) =>
        e is Google.GoogleApiException
        || e is TokenResponseException
        || e is Google.Apis.Auth.OAuth2.SubjectTokenException
        || e is IdentityProviderException
        || e is HttpRequestException
        || e is TaskCanceledException;

    /// <summary>
    /// Single exception translator for all actions: keeps the per-status Google storage messages,
    /// unwraps the SDK's SubjectTokenException (which would otherwise hide the identity provider's
    /// actionable message), and adds clear messages for network and timeout failures. The original
    /// exception is kept as the InnerException.
    /// </summary>
    internal static Exception TranslateException(Exception e, Authentication authentication, string? bucketName, string? objectName)
    {
        switch (e)
        {
            case Google.GoogleApiException api:
                return FriendlyException(api, authentication, bucketName, objectName);
            case TokenResponseException token:
                return FriendlyAuthException(token, authentication);
            case Google.Apis.Auth.OAuth2.SubjectTokenException:
                // The SDK wraps every subject-token failure in SubjectTokenException, hiding our
                // IdentityProviderException behind a generic message; unwrap it when present.
                var identityProvider = FindInChain<IdentityProviderException>(e);
                if (identityProvider != null)
                    return new Exception(identityProvider.Message, e);
                return new Exception($"Workload Identity Federation could not obtain the identity provider token: {InnermostMessage(e)}", e);
            case IdentityProviderException:
                return new Exception(e.Message, e);
            case HttpRequestException:
                return new Exception($"Could not reach Google Cloud: {InnermostMessage(e)} Check the outbound HTTPS access to *.googleapis.com.", e);
            default:
                // Actions take no CancellationToken, so a TaskCanceledException is a request timeout.
                return new Exception($"The request to Google Cloud timed out (no response within {StorageRequestTimeout.TotalSeconds:0} seconds). Retry the action; for large files use Object_GetSignedUrl and let the client transfer the file directly.", e);
        }
    }

    /// <summary>Finds the first exception of the given type in the InnerException chain.</summary>
    internal static T? FindInChain<T>(Exception e) where T : Exception
    {
        for (Exception? x = e; x != null; x = x.InnerException)
            if (x is T found) return found;
        return null;
    }

    /// <summary>The message of the deepest exception in the InnerException chain.</summary>
    internal static string InnermostMessage(Exception e)
    {
        var x = e;
        while (x.InnerException != null) x = x.InnerException;
        return x.Message;
    }

    /// <summary>
    /// Since Google.Cloud.Storage.V1 5.0, uploads send the client-computed CRC32C in an x-goog-hash
    /// header and the server rejects a mismatch with 400, e.g. 'Provided CRC32C "..." doesn't match
    /// calculated CRC32C "..."'.
    /// </summary>
    private static bool IsChecksumMismatch(string details) =>
        (details.IndexOf("CRC32C", StringComparison.OrdinalIgnoreCase) >= 0 || details.IndexOf("MD5", StringComparison.OrdinalIgnoreCase) >= 0)
        && details.IndexOf("match", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>
    /// Translates a GoogleApiException into an exception with an actionable message for OutSystems
    /// logs, instead of Google's raw API error. The original exception is kept as InnerException.
    /// </summary>
    private static Exception FriendlyException(Google.GoogleApiException e, Authentication authentication, string? bucketName, string? objectName)
    {
        string details = e.Error != null && !string.IsNullOrEmpty(e.Error.Message) ? e.Error.Message : e.Message;

        if (e.HttpStatusCode == HttpStatusCode.NotFound && bucketName != null)
        {
            if (objectName != null && !IsBucketNotFound(e))
                return new Exception($"Object '{objectName}' was not found in bucket '{bucketName}'. Details: {details}", e);
            return new Exception($"Bucket '{bucketName}' does not exist (names are case-sensitive and must match exactly). Details: {details}", e);
        }
        if (e.HttpStatusCode == HttpStatusCode.Forbidden)
            return new Exception($"Access denied for service account '{IdentityOf(authentication)}'. Grant it the required IAM role in Google Cloud (Storage Object Admin for object operations, Storage Admin for bucket operations). Details: {details}", e);
        if (e.HttpStatusCode == HttpStatusCode.Unauthorized)
            return new Exception($"Google rejected the request as unauthenticated. {UnauthenticatedHint(authentication)} Details: {details}", e);
        if (e.HttpStatusCode == HttpStatusCode.BadRequest && IsChecksumMismatch(details))
            return new Exception($"Upload rejected by Google Cloud Storage: the data it received did not match the CRC32C checksum computed by the connector, so nothing was stored and any existing object was left unchanged. This is usually a transient network error - retry the upload. Details: {details}", e);
        if (e.HttpStatusCode == HttpStatusCode.Conflict)
        {
            if (details.IndexOf("not empty", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Exception($"Bucket '{bucketName}' is not empty. Delete all objects in it before deleting the bucket. Details: {details}", e);
            return new Exception($"Conflict: {details} (for Bucket_Create this usually means the name is already taken - bucket names are global across all of Google Cloud Storage).", e);
        }
        return new Exception($"Google Cloud Storage error ({(int)e.HttpStatusCode} {e.HttpStatusCode}): {details}", e);
    }
}
