using Google.Apis.Auth.OAuth2.Responses;
using Google.Cloud.Storage.V1;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;
using OutSystems.ExternalLibraries.SDK;
using OutSystems.ExternalLibraries.SDK.FileStorage;
using OutSystems.ExternalLibraries.SDK.FileStorage.Exceptions;
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

    /// <summary>
    /// The hard limit on one Object_DeleteByPrefix call. It cancels the request in flight, so a call
    /// started just inside <see cref="DeleteByPrefixBudget"/> cannot run past ODC's 95-second kill. Test seam.
    /// </summary>
    internal static TimeSpan DeleteByPrefixDeadline { get; set; } = TimeSpan.FromSeconds(88);

    /// <summary>
    /// How many objects one listing page returns in Object_DeleteByPrefix; each page is deleted before
    /// the next is fetched, so the call never holds the whole listing in memory. Test seam.
    /// </summary>
    internal static int DeleteByPrefixPageSize { get; set; } = 1000;

    /// <summary>
    /// The waits between the attempts of a retried upload or download (so at most one more attempt than
    /// delays). Test seam.
    /// </summary>
    internal static TimeSpan[] TransferRetryDelays { get; set; } = { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1) };

    /// <summary>
    /// The largest File ODC can store (2 GB). Object_Download refuses a larger object before it starts.
    /// </summary>
    internal const long MaxFileBytes = 2_147_483_648;

    /// <summary>
    /// The chunk size of a File upload. Google confirms each chunk before the next one is sent, so larger
    /// chunks mean fewer waits: on ODC, 32 MB chunks sent 200 MB to a nearby bucket in about 10 s, against
    /// 14 s with the SDK's 10 MB default (and 22 s against 39 s to a bucket on another continent). Each
    /// upload holds one chunk in memory. Test seam.
    /// </summary>
    internal static int UploadChunkSize { get; set; } = 32 * 1024 * 1024;

    /// <summary>
    /// The longest one File upload or download may run, retries included. It cancels the transfer in
    /// flight, so a large file fails with a clear message before ODC kills the action at 95 seconds. Test seam.
    /// </summary>
    internal static TimeSpan FileTransferDeadline { get; set; } = TimeSpan.FromSeconds(85);

    /// <summary>No transfer retry starts after this much time, so retries never push a call past ODC's 95 seconds.</summary>
    private static readonly TimeSpan TransferRetryWindow = TimeSpan.FromSeconds(40);

    public void Object_Upload(Authentication authentication, string bucketName, string objectName, IOSFile file, string contentType, IEnumerable<MetadataEntry> metadata)
    {
        Require(bucketName, "BucketName");
        Require(objectName, "ObjectName");
        RequireFile(file);
        var type = string.IsNullOrEmpty(contentType) ? file.ContentType : contentType;
        RejectLineBreaks(type);

        var storageClient = GetStorageClient(authentication);
        var fileStorage = FileStorage.GetInstance();
        using var deadline = new CancellationTokenSource(FileTransferDeadline);
        try
        {
            var gcsObject = new Google.Apis.Storage.v1.Data.Object
            {
                Bucket = bucketName,
                Name = objectName,
                ContentType = string.IsNullOrEmpty(type) ? null : type,
                Metadata = ToMetadataDictionary(metadata)
            };
            var options = new UploadObjectOptions { ChunkSize = UploadChunkSize };

            // Streams from ODC storage to Google, one chunk in memory at a time. Each attempt reopens the File.
            WithTransferRetry(() =>
            {
                using var source = fileStorage.ReadFileAsStreamAsync(file, deadline.Token).GetAwaiter().GetResult();
                return storageClient.UploadObjectAsync(gcsObject, source, options, deadline.Token).GetAwaiter().GetResult();
            });
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw new Exception($"Uploading '{objectName}' ({FormatBytes(file.Size)}) did not finish within {FileTransferDeadline.TotalSeconds:0} seconds, the most an ODC action can spend on it; nothing was stored. A bucket in or near your ODC region is several times faster; for files this large, upload with Object_GetSignedUrl (Operation 'Upload') instead.", e);
        }
        catch (FileStorageException e) { throw FileStorageFailure(e); }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, null); }
    }

    public void Object_Download(Authentication authentication, string bucketName, string objectName, out IOSFile file)
    {
        Require(bucketName, "BucketName");
        Require(objectName, "ObjectName");

        var storageClient = GetStorageClient(authentication);
        var fileStorage = FileStorage.GetInstance();
        using var deadline = new CancellationTokenSource(FileTransferDeadline);
        try
        {
            // Read the size and type first: refuse what ODC cannot store before transferring anything.
            var obj = storageClient.GetObjectAsync(bucketName, objectName, null, deadline.Token).GetAwaiter().GetResult();
            if (obj.Size > (ulong)MaxFileBytes)
                throw new Exception($"Object '{objectName}' is {FormatBytes((long)obj.Size!.Value)}, larger than the 2 GB an ODC File can hold. Use Object_GetSignedUrl with Operation 'Download' and let the client download it directly from Google.");

            var name = FileNameFor(objectName);
            var type = string.IsNullOrEmpty(obj.ContentType) ? "application/octet-stream" : obj.ContentType;
            var options = new DownloadObjectOptions { DownloadValidationMode = DownloadValidationMode.Automatic };

            // Streams from Google into ODC storage, checking Google's CRC32C. A failed attempt leaves only
            // a temporary File, which ODC deletes by itself.
            file = WithTransferRetry(() => fileStorage.CreateFileFromWriterAsync(name, type, async (target, ct) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
                await storageClient.DownloadObjectAsync(bucketName, objectName, target, options, linked.Token);
            }, null, deadline.Token).GetAwaiter().GetResult());
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw new Exception($"Downloading '{objectName}' did not finish within {FileTransferDeadline.TotalSeconds:0} seconds, the most an ODC action can spend on it. A bucket in or near your ODC region is several times faster; for objects this large, use Object_GetSignedUrl (Operation 'Download') and let the client download it directly from Google.", e);
        }
        catch (FileStorageException e) { throw FileStorageFailure(e); }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }
    }

    public void Object_UploadBinary(Authentication authentication, string bucketName, string objectName, BinaryContent content, IEnumerable<MetadataEntry> metadata)
    {
        Require(bucketName, "BucketName");
        Require(objectName, "ObjectName");
        RejectLineBreaks(content.ContentType);

        var storageClient = GetStorageClient(authentication);
        try
        {
            var gcsObject = new Google.Apis.Storage.v1.Data.Object
            {
                Bucket = bucketName,
                Name = objectName,
                ContentType = string.IsNullOrEmpty(content.ContentType) ? null : content.ContentType,
                Metadata = ToMetadataDictionary(metadata)
            };

            // Opening the resumable session is not retried by the SDK; each attempt re-reads the same bytes.
            var bytes = content.Content ?? Array.Empty<byte>();
            WithTransferRetry(() =>
            {
                using var stream = new MemoryStream(bytes);
                return storageClient.UploadObject(gcsObject, stream);
            });
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, null); }
    }

    public void Object_DownloadBinary(Authentication authentication, string bucketName, string objectName, out BinaryContent content)
    {
        Require(bucketName, "BucketName");
        Require(objectName, "ObjectName");

        var storageClient = GetStorageClient(authentication);
        try
        {
            // DownloadObject has no retry options of its own; every attempt starts with a fresh buffer.
            content = WithTransferRetry(() =>
            {
                using var stream = new CappedDownloadStream();
                var obj = storageClient.DownloadObject(bucketName, objectName, stream,
                    new DownloadObjectOptions { DownloadValidationMode = DownloadValidationMode.Automatic });

                return new BinaryContent
                {
                    Content = stream.ToArray(),
                    ContentType = obj.ContentType ?? ""
                };
            });
        }
        catch (Exception e) when (FindInChain<DownloadTooLargeException>(e) != null)
        {
            // The SDK may wrap whatever the target stream throws, so look through the chain.
            throw new Exception($"Object '{objectName}' is larger than {MaxDownloadBytes} bytes (5.5 MB), the most an ODC action can return as Binary Data. Use Object_Download to get it as a File, or Object_GetSignedUrl with Operation 'Download' to let the client download it directly from Google.");
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }
    }

    public void Object_List(Authentication authentication, string bucketName, string prefix, int maxResults, string pageToken, string delimiter, out IEnumerable<Object> objects, out string nextPageToken, out IEnumerable<Prefix> prefixList)
    {
        Require(bucketName, "BucketName");

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
        Require(bucketName, "BucketName");
        Require(objectName, "ObjectName");

        var storageClient = GetStorageClient(authentication);
        try
        {
            storageClient.GetObject(bucketName, objectName);
            exists = true;
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            if (!IsObjectNotFound(storageClient, e, bucketName))
                throw TranslateException(e, authentication, bucketName, null);
            exists = false;
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }
    }

    public void Object_GetMetadata(Authentication authentication, string bucketName, string objectName, out bool exists, out ObjectMetadata metadata, out IEnumerable<MetadataEntry> customMetadata)
    {
        Require(bucketName, "BucketName");
        Require(objectName, "ObjectName");

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
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            if (!IsObjectNotFound(storageClient, e, bucketName))
                throw TranslateException(e, authentication, bucketName, null);
            exists = false;
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }

        customMetadata = custom;
    }

    public void Object_Delete(Authentication authentication, string bucketName, string objectName)
    {
        Require(bucketName, "BucketName");
        Require(objectName, "ObjectName");

        var storageClient = GetStorageClient(authentication);

        // Read the generation first and delete only that generation: a delete that carries a
        // precondition is retried by the SDK on transient errors, and a replaced object is never removed.
        long? generation;
        try
        {
            generation = storageClient.GetObject(bucketName, objectName).Generation;
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.Forbidden)
        {
            // A role that may delete but not read objects: fall back to an unguarded delete (not
            // retried, as before 1.7.2) rather than refuse an operation the role allows.
            generation = null;
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }

        try
        {
            storageClient.DeleteObject(bucketName, objectName, new DeleteObjectOptions { IfGenerationMatch = generation });
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new Exception($"Object '{objectName}' in bucket '{bucketName}' was replaced while it was being deleted; nothing was deleted. Retry the delete if the new version should be removed too.", e);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }
    }

    public void Object_Copy(Authentication authentication, string sourceBucketName, string sourceObjectName, string destinationBucketName, string destinationObjectName)
    {
        Require(sourceBucketName, "SourceBucketName");
        Require(sourceObjectName, "SourceObjectName");
        Require(destinationBucketName, "DestinationBucketName");
        Require(destinationObjectName, "DestinationObjectName");

        var storageClient = GetStorageClient(authentication);
        try
        {
            storageClient.CopyObject(sourceBucketName, sourceObjectName, destinationBucketName, destinationObjectName,
                new CopyObjectOptions { RetryOptions = RetryOptions.IdempotentRetryOptions });
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw CopyNotFoundException(storageClient, e, authentication, sourceBucketName, sourceObjectName, destinationBucketName);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, sourceBucketName, sourceObjectName); }
    }

    public void Object_Move(Authentication authentication, string sourceBucketName, string sourceObjectName, string destinationBucketName, string destinationObjectName)
    {
        Require(sourceBucketName, "SourceBucketName");
        Require(sourceObjectName, "SourceObjectName");
        Require(destinationBucketName, "DestinationBucketName");
        Require(destinationObjectName, "DestinationObjectName");

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
                new CopyObjectOptions { IfSourceGenerationMatch = generation, RetryOptions = RetryOptions.IdempotentRetryOptions });
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new Exception($"The source object '{sourceBucketName}/{sourceObjectName}' changed while it was being moved; nothing was moved. Retry the move.", e);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw CopyNotFoundException(storageClient, e, authentication, sourceBucketName, sourceObjectName, destinationBucketName, generation);
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
        Require(bucketName, "BucketName");
        Require(objectName, "ObjectName");

        var changes = ToMetadataDictionary(metadata);
        bool hasFieldChange = !string.IsNullOrEmpty(contentType) || !string.IsNullOrEmpty(contentEncoding)
            || !string.IsNullOrEmpty(contentDisposition) || !string.IsNullOrEmpty(cacheControl);
        if (!hasFieldChange && changes == null)
            throw new ArgumentException("Nothing to update: provide at least one of ContentType, ContentEncoding, ContentDisposition, CacheControl, or a non-empty Metadata list.");

        var storageClient = GetStorageClient(authentication);

        try
        {
            // Fetch the current object to get its generation and metageneration preconditions, then send
            // a PATCH containing only the changed fields so untouched properties and ACLs are never sent back.
            var obj = storageClient.GetObject(bucketName, objectName);

            var patch = new Google.Apis.Storage.v1.Data.Object
            {
                Bucket = bucketName,
                Name = objectName
            };
            if (!string.IsNullOrEmpty(contentType)) patch.ContentType = contentType;
            if (!string.IsNullOrEmpty(contentEncoding)) patch.ContentEncoding = contentEncoding;
            if (!string.IsNullOrEmpty(contentDisposition)) patch.ContentDisposition = contentDisposition;
            if (!string.IsNullOrEmpty(cacheControl)) patch.CacheControl = cacheControl;

            if (changes != null)
            {
                var patchMetadata = new Dictionary<string, string>();
                foreach (var kv in changes)
                {
                    // In a GCS JSON PATCH, a null dictionary value removes that custom metadata key;
                    // null! satisfies IDictionary<string, string> while serializing as JSON null.
                    patchMetadata[kv.Key] = kv.Value.Length == 0 ? null! : kv.Value;
                }
                patch.Metadata = patchMetadata;
            }

            try
            {
                // Setting any precondition turns off the SDK's automatic ones, so both are explicit: the
                // generation guards against a content replacement (which resets metageneration to 1).
                storageClient.PatchObject(patch, new PatchObjectOptions { IfGenerationMatch = obj.Generation, IfMetagenerationMatch = obj.Metageneration });
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
        Require(bucketName, "BucketName");

        deletedCount = 0;
        var stopwatch = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Prefix cannot be empty - it would delete every object in the bucket. To do that intentionally, delete the bucket or list and delete the objects explicitly.");

        var storageClient = GetStorageClient(authentication);

        // The deadline cancels a request still in flight, so the call ends before ODC's 95-second kill.
        using var deadline = new CancellationTokenSource(DeleteByPrefixDeadline);
        long deleted = 0;

        Exception BudgetExceeded() => new Exception($"Deleted {deleted} objects under prefix '{prefix}' before the {DeleteByPrefixBudget.TotalSeconds:0}-second time budget of one call ran out (ODC limits an action to 95 seconds). Nothing was rolled back; call Object_DeleteByPrefix again with the same prefix to delete the rest.");

        // Delete page by page: each delete carries the generation it was listed with (which makes the SDK
        // retry transient failures), and GCS page tokens are positional, so deleting listed objects does
        // not disturb the paging.
        var pages = storageClient.ListObjectsAsync(bucketName, prefix, new ListObjectsOptions { PageSize = DeleteByPrefixPageSize })
            .AsRawResponses().GetAsyncEnumerator(deadline.Token);
        try
        {
            while (true)
            {
                if (stopwatch.Elapsed >= DeleteByPrefixBudget) throw BudgetExceeded();

                try
                {
                    if (!pages.MoveNextAsync().AsTask().GetAwaiter().GetResult()) break;
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { throw BudgetExceeded(); }
                catch (Exception e) when (IsTranslatable(e))
                {
                    if (deleted == 0) throw TranslateException(e, authentication, bucketName, null);
                    throw new Exception($"Deleted {deleted} objects under prefix '{prefix}', then listing the remaining objects failed: {TranslateException(e, authentication, bucketName, null).Message}", e);
                }

                foreach (var obj in pages.Current.Items ?? new List<Google.Apis.Storage.v1.Data.Object>())
                {
                    // One call may not finish the work: stop before ODC kills the action and report where it
                    // stopped, so the caller can resume with the same prefix.
                    if (stopwatch.Elapsed >= DeleteByPrefixBudget) throw BudgetExceeded();

                    try
                    {
                        storageClient.DeleteObjectAsync(bucketName, obj.Name,
                            new DeleteObjectOptions { IfGenerationMatch = obj.Generation }, deadline.Token).GetAwaiter().GetResult();
                        deleted++;
                    }
                    catch (OperationCanceledException) when (deadline.IsCancellationRequested) { throw BudgetExceeded(); }
                    catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound || e.HttpStatusCode == HttpStatusCode.PreconditionFailed)
                    {
                        // Already gone (404) or replaced since listing (412) - skip without counting and keep going.
                    }
                    catch (Exception e) when (IsTranslatable(e))
                    {
                        // TranslateException gives the same message a whole-object call would give, so the
                        // partial-progress error carries an actionable cause instead of Google's raw text.
                        throw new Exception($"Deleted {deleted} objects under prefix '{prefix}', then failed on '{obj.Name}': {TranslateException(e, authentication, bucketName, obj.Name).Message}", e);
                    }
                }
            }
        }
        finally
        {
            pages.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        deletedCount = deleted;
    }

    public void Object_GetSignedUrl(Authentication authentication, string bucketName, string objectName, int expirationMinutes, out string url, string operation = "Download", string contentType = "")
    {
        Require(bucketName, "BucketName");
        Require(objectName, "ObjectName");

        if (expirationMinutes <= 0)
            throw new ArgumentException($"ExpirationMinutes must be greater than zero (received {expirationMinutes}).");
        if (expirationMinutes > 10080)
            throw new ArgumentException($"ExpirationMinutes cannot exceed 10080 minutes (7 days), the maximum validity of a Google Cloud V4 signed URL (received {expirationMinutes}).");

        // A line break would inject extra signed headers (or extra lines) into the canonical request.
        if (contentType != null && (contentType.Contains('\r') || contentType.Contains('\n')))
            throw new ArgumentException("ContentType cannot contain line breaks.");

        // Validate the Operation before building the signer, so a typo fails without any network call.
        var method = (operation ?? "").Trim().ToUpperInvariant() switch
        {
            "DOWNLOAD" => HttpMethod.Get,
            "UPLOAD" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            _ => throw new ArgumentException($"Invalid Operation '{operation}'. Use 'Download', 'Upload', or 'Delete'.")
        };

        var urlSigner = GetUrlSigner(authentication);

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
        Require(authentication.ProjectId, "ProjectId");

        var storageClient = GetStorageClient(authentication);
        try
        {
            var gcsBuckets = storageClient.ListBuckets(authentication.ProjectId.Trim());

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
        Require(authentication.ProjectId, "ProjectId");
        Require(bucketName, "BucketName");

        var storageClient = GetStorageClient(authentication);
        try
        {
            storageClient.CreateBucket(
                authentication.ProjectId.Trim(),
                new Google.Apis.Storage.v1.Data.Bucket
                {
                    Name = bucketName,
                    Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim()
                }
            );
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // Creating a bucket never 404s on the bucket itself: the project is what was not found.
            string details = e.Error != null && !string.IsNullOrEmpty(e.Error.Message) ? e.Error.Message : e.Message;
            throw new Exception($"Bucket '{bucketName}' was not created: project '{authentication.ProjectId.Trim()}' was not found, or the service account cannot access it. Check ProjectId. Details: {details}", e);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, null); }
    }

    public void Bucket_Delete(Authentication authentication, string bucketName)
    {
        Require(bucketName, "BucketName");

        var storageClient = GetStorageClient(authentication);
        try
        {
            storageClient.DeleteBucket(bucketName);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, null); }
    }

    public void Bucket_Exists(Authentication authentication, string bucketName, out bool exists)
    {
        Require(bucketName, "BucketName");

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
    /// Rejects an empty or whitespace-only required name before any client is created, so a missing
    /// input fails with an actionable message and never reaches the network.
    /// </summary>
    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} cannot be empty.");
    }

    /// <summary>
    /// Rejects a missing File (null, or ODC's NullFile(), which has no storage key) before any request.
    /// </summary>
    private static void RequireFile(IOSFile? file)
    {
        if (file == null || string.IsNullOrEmpty(file.Key))
            throw new ArgumentException("File cannot be empty. Pass a File from an Upload widget, an entity attribute or another action.");
    }

    /// <summary>A line break in a content type would inject extra headers into the upload request.</summary>
    private static void RejectLineBreaks(string? contentType)
    {
        if (contentType != null && (contentType.Contains('\r') || contentType.Contains('\n')))
            throw new ArgumentException("ContentType cannot contain line breaks.");
    }

    /// <summary>
    /// The name of a downloaded File: the last part of the object name ("reports/2026/q1.pdf" gives
    /// "q1.pdf"), or "download" when the name ends with '/'. Cut to 200 characters, well inside the
    /// 1,500 bytes ODC allows for a File's metadata.
    /// </summary>
    internal static string FileNameFor(string objectName)
    {
        var name = objectName.TrimEnd('/');
        name = name.Length == 0 || objectName.EndsWith('/') ? "download" : name[(name.LastIndexOf('/') + 1)..];
        return name.Length > 200 ? name[..200] : name;
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.#} MB" : $"{bytes} bytes";

    /// <summary>ODC storage refused to read or create a File (for example a temporary File that expired).</summary>
    private static Exception FileStorageFailure(FileStorageException e) =>
        new Exception($"ODC file storage failed: {e.Message}", e);

    /// <summary>
    /// Runs one upload or download attempt, retrying transient failures (408, 429, 500, 502, 503, 504,
    /// network and I/O errors) after each of <see cref="TransferRetryDelays"/>. Never retries a too-large
    /// download, a cancellation or timeout (a retry would pass ODC's 95-second limit), or any other 4xx,
    /// and stops retrying once <see cref="TransferRetryWindow"/> has passed.
    /// </summary>
    private static T WithTransferRetry<T>(Func<T> attempt)
    {
        var delays = TransferRetryDelays;
        var stopwatch = Stopwatch.StartNew();
        for (int tried = 0; ; tried++)
        {
            try
            {
                return UnwrapSingleAggregate(attempt);
            }
            catch (Exception e) when (tried < delays.Length && stopwatch.Elapsed < TransferRetryWindow && IsRetryableTransferFailure(e))
            {
                Thread.Sleep(delays[tried]);
            }
        }
    }

    /// <summary>
    /// The SDK's synchronous download waits on a task, so a failure (notably the 80-second timeout's
    /// TaskCanceledException) arrives wrapped in an AggregateException that no handler recognises.
    /// Rethrows a single inner exception as itself, with its original stack trace.
    /// </summary>
    private static T UnwrapSingleAggregate<T>(Func<T> attempt)
    {
        try
        {
            return attempt();
        }
        catch (AggregateException ae) when (ae.InnerExceptions.Count == 1)
        {
            ExceptionDispatchInfo.Capture(ae.InnerExceptions[0]).Throw();
            throw; // unreachable
        }
    }

    private static bool IsRetryableTransferFailure(Exception e)
    {
        if (FindInChain<DownloadTooLargeException>(e) != null) return false;
        if (e is OperationCanceledException) return false; // includes TaskCanceledException
        if (e is Google.GoogleApiException api)
            return api.HttpStatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
        return e is HttpRequestException || e is IOException;
    }

    /// <summary>
    /// Decides whether a 404 from an object GET means the object is missing (true) or the bucket is
    /// (false), without relying on Google's English wording alone: "No such object" is an object, any
    /// other message naming the bucket is a bucket, and an ambiguous one (plain "Not Found") is settled
    /// by looking the bucket up: only a 404 on the bucket means the bucket is missing. If that lookup fails
    /// in any other way (e.g. 403: Storage Object Admin cannot read buckets), the object is taken as missing,
    /// as before 1.7.2.
    /// </summary>
    private static bool IsObjectNotFound(StorageClient client, Google.GoogleApiException e, string bucketName)
    {
        string msg = (e.Error?.Message) ?? e.Message ?? "";
        if (msg.IndexOf("No such object", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (msg.IndexOf("bucket", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        try
        {
            client.GetBucket(bucketName);
            return true;
        }
        catch (Google.GoogleApiException probe) when (probe.HttpStatusCode == HttpStatusCode.NotFound) { return false; }
        catch (Exception) { return true; }
    }

    /// <summary>
    /// A 404 from a copy (an object rewrite) means either the source object or the destination bucket
    /// is missing, and the message wording differs between Google and emulators. Probes the source:
    /// if its generation is not the one the move read, the source changed; if the source exists, the destination bucket is missing; if the probe returns 404, the source
    /// is missing; if the probe fails any other way, reports both candidates and the probe error.
    /// </summary>
    private static Exception CopyNotFoundException(StorageClient storageClient, Google.GoogleApiException e, Authentication authentication, string sourceBucketName, string sourceObjectName, string destinationBucketName, long? expectedSourceGeneration = null)
    {
        try
        {
            var probed = storageClient.GetObject(sourceBucketName, sourceObjectName);
            // A source that was replaced meanwhile is a change, not a missing bucket (Move only).
            if (expectedSourceGeneration.HasValue && probed.Generation != expectedSourceGeneration)
                return new Exception($"The source object '{sourceBucketName}/{sourceObjectName}' changed while it was being moved; nothing was moved. Retry the move.", e);
            return FriendlyException(e, authentication, destinationBucketName, null);
        }
        catch (Google.GoogleApiException probe) when (probe.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return FriendlyException(probe, authentication, sourceBucketName, sourceObjectName);
        }
        catch (Exception probe) when (IsTranslatable(probe))
        {
            return new Exception($"The copy from '{sourceBucketName}/{sourceObjectName}' failed with 404: either that source object or the destination bucket '{destinationBucketName}' does not exist, and checking the source failed: {TranslateException(probe, authentication, sourceBucketName, sourceObjectName).Message}", e);
        }
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
    /// error, a token-endpoint failure, a subject-token failure, an identity provider failure, an
    /// I/O or download-checksum failure, or the network/timeout failures of a request to Google.
    /// </summary>
    internal static bool IsTranslatable(Exception e) =>
        e is Google.GoogleApiException
        || e is TokenResponseException
        || e is Google.Apis.Auth.OAuth2.SubjectTokenException
        || e is IdentityProviderException
        || e is IOException
        || e is HttpRequestException
        || e is TaskCanceledException;

    /// <summary>
    /// Single exception translator for all actions: keeps the per-status Google storage messages,
    /// unwraps the SDK's SubjectTokenException (which would otherwise hide the identity provider's
    /// actionable message), and adds clear messages for I/O, network and timeout failures. The original
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
            case IOException io when io.Message != null && io.Message.StartsWith("Incorrect hash", StringComparison.Ordinal):
                // In Google.Cloud.Storage.V1 5.0, a download CRC32C mismatch throws an IOException starting with "Incorrect hash:".
                return new Exception($"The downloaded data did not match the object's checksum, so no content was returned. This is usually a transient network error - retry the download. Details: {e.Message}", e);
            case IOException:
                return new Exception($"The connection to Google Cloud was interrupted: {InnermostMessage(e).TrimEnd('.')}. Retry the request.", e);
            case HttpRequestException:
                return new Exception($"Could not reach Google Cloud: {InnermostMessage(e).TrimEnd('.')}. Check the outbound HTTPS access to *.googleapis.com.", e);
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

        if ((int)e.HttpStatusCode == 0)
            return new Exception($"The request was rejected before it was sent to Google Cloud Storage because an input is invalid: {details}", e);
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
