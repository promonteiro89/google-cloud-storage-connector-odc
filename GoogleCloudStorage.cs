using Google.Apis.Auth.OAuth2.Responses;
using Google.Cloud.Storage.V1;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
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

    /// <summary>The longest signed URL, in minutes, with Workload Identity Federation (12 hours).</summary>
    internal const int MaxFederatedSignedUrlMinutes = 720;

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

    /// <summary>
    /// The longest any action other than the File transfers and Object_DeleteByPrefix (which have their own
    /// limits) may run, all of its requests and retries included. It cancels the request in flight, so the
    /// action fails with a clear message before ODC kills it at 95 seconds. Test seam.
    /// </summary>
    internal static TimeSpan ActionDeadline { get; set; } = TimeSpan.FromSeconds(85);

    /// <summary>No retry starts after this much time, so retries never push a call past ODC's 95 seconds. Test seam.</summary>
    internal static TimeSpan TransferRetryWindow { get; set; } = TimeSpan.FromSeconds(40);

    public void Object_Upload(Authentication authentication, string bucketName, string objectName, IOSFile file, string contentType, IEnumerable<MetadataEntry> metadata, out long generation,
        bool onlyIfNotExists = false, string contentDisposition = "", string cacheControl = "", string contentEncoding = "")
    {
        // Assigned first so every path has a value.
        generation = 0;
        RequireBucket(bucketName, "BucketName");
        RequireObject(objectName, "ObjectName");
        RequireFile(file);
        var type = string.IsNullOrEmpty(contentType) ? file.ContentType : contentType;
        ValidateContentType(type);
        RequireHeaderValue(contentDisposition, "ContentDisposition");
        RequireHeaderValue(cacheControl, "CacheControl");
        RequireHeaderValue(contentEncoding, "ContentEncoding");

        var storageClient = GetStorageClient(authentication);
        var fileStorage = FileStorage.GetInstance();
        using var deadline = new CancellationTokenSource(FileTransferDeadline);
        var startedUtc = DateTime.UtcNow;
        bool resent = false;
        try
        {
            var gcsObject = NewUploadObject(bucketName, objectName, type, metadata, contentDisposition, cacheControl, contentEncoding);
            var options = new UploadObjectOptions { ChunkSize = UploadChunkSize, IfGenerationMatch = onlyIfNotExists ? 0 : null };

            // Streams from ODC storage to Google, one chunk in memory at a time. Each attempt reopens the File.
            var stored = WithTransferRetry(() =>
            {
                using var source = fileStorage.ReadFileAsStreamAsync(file, deadline.Token).GetAwaiter().GetResult();
                return storageClient.UploadObjectAsync(gcsObject, source, options, deadline.Token).GetAwaiter().GetResult();
            }, () => resent = true);
            generation = stored.Generation ?? 0;
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.PreconditionFailed && onlyIfNotExists)
        {
            var ours = resent
                ? ObjectStoredByThisCall(storageClient, bucketName, objectName, file.Size, () => Md5Base64(fileStorage, file, deadline.Token), startedUtc, deadline.Token)
                : null;
            if (ours == null) throw AlreadyExists(bucketName, objectName, e);
            generation = ours.Generation ?? 0;
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw new Exception($"Uploading '{Clean(objectName)}' ({FormatBytes(file.Size)}) did not finish within {FileTransferDeadline.TotalSeconds:0} seconds, the most an ODC action can spend on it; the object may not have been stored: check with Object_Exists before relying on it. A bucket in or near your ODC region is several times faster; for files this large, upload with Object_GetSignedUrl (Operation 'Upload') instead.", e);
        }
        catch (FileStorageException e) { throw FileStorageFailure(e); }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, null); }
    }

    public void Object_Download(Authentication authentication, string bucketName, string objectName, out IOSFile file)
    {
        RequireBucket(bucketName, "BucketName");
        RequireObject(objectName, "ObjectName");

        var storageClient = GetStorageClient(authentication);
        var fileStorage = FileStorage.GetInstance();
        using var deadline = new CancellationTokenSource(FileTransferDeadline);
        bool downloadStarted = false;
        try
        {
            // Read the size and type first: refuse what ODC cannot store before transferring anything.
            var obj = WithNetworkRetry(() => storageClient.GetObjectAsync(bucketName, objectName, null, deadline.Token).GetAwaiter().GetResult());
            if (obj.Size > (ulong)MaxFileBytes)
                throw new Exception($"Object '{Clean(objectName)}' is {FormatBytes((long)obj.Size!.Value)}, larger than the 2 GB an ODC File can hold. Use Object_GetSignedUrl with Operation 'Download' and let the client download it directly from Google.");

            var name = FileNameFor(objectName);
            var type = string.IsNullOrEmpty(obj.ContentType) ? "application/octet-stream" : obj.ContentType;
            var options = new DownloadObjectOptions { DownloadValidationMode = DownloadValidationMode.Automatic, Generation = obj.Generation };

            // Streams from Google into ODC storage, checking Google's CRC32C. A failed attempt leaves only
            // a temporary File, which ODC deletes by itself.
            file = WithTransferRetry(() =>
            {
                Exception? writerFailure = null;
                downloadStarted = true;
                try
                {
                    return fileStorage.CreateFileFromWriterAsync(name, type, async (target, ct) =>
                    {
                        try
                        {
                            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
                            await storageClient.DownloadObjectAsync(bucketName, objectName, target, options, linked.Token);
                        }
                        catch (Exception e)
                        {
                            writerFailure = e;
                            throw;
                        }
                    }, null, deadline.Token).GetAwaiter().GetResult();
                }
                catch (FileStorageException) when (writerFailure != null)
                {
                    ExceptionDispatchInfo.Capture(writerFailure).Throw();
                    throw;
                }
            });
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw new Exception($"Downloading '{Clean(objectName)}' did not finish within {FileTransferDeadline.TotalSeconds:0} seconds, the most an ODC action can spend on it. A bucket in or near your ODC region is several times faster; for objects this large, use Object_GetSignedUrl (Operation 'Download') and let the client download it directly from Google.", e);
        }
        catch (FileStorageException e) { throw FileStorageFailure(e); }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound && downloadStarted)
        {
            throw new Exception($"Object '{Clean(objectName)}' in bucket '{Clean(bucketName)}' was replaced or deleted while it was being downloaded. Retry the download.", e);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw NotFoundException(storageClient, e, authentication, bucketName, objectName, deadline.Token);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }
    }

    public void Object_UploadBinary(Authentication authentication, string bucketName, string objectName, BinaryContent content, IEnumerable<MetadataEntry> metadata, out long generation,
        bool onlyIfNotExists = false, string contentDisposition = "", string cacheControl = "", string contentEncoding = "")
    {
        generation = 0;
        RequireBucket(bucketName, "BucketName");
        RequireObject(objectName, "ObjectName");
        ValidateContentType(content.ContentType);
        RequireHeaderValue(contentDisposition, "ContentDisposition");
        RequireHeaderValue(cacheControl, "CacheControl");
        RequireHeaderValue(contentEncoding, "ContentEncoding");

        var storageClient = GetStorageClient(authentication);
        using var deadline = new CancellationTokenSource(ActionDeadline);
        var bytes = content.Content ?? Array.Empty<byte>();
        var startedUtc = DateTime.UtcNow;
        bool resent = false;
        try
        {
            var gcsObject = NewUploadObject(bucketName, objectName, content.ContentType, metadata, contentDisposition, cacheControl, contentEncoding);
            var options = onlyIfNotExists ? new UploadObjectOptions { IfGenerationMatch = 0 } : null;

            // Opening the resumable session is not retried by the SDK; each attempt re-reads the same bytes.
            var stored = WithTransferRetry(() =>
            {
                using var stream = new MemoryStream(bytes);
                return storageClient.UploadObjectAsync(gcsObject, stream, options, deadline.Token).GetAwaiter().GetResult();
            }, () => resent = true);
            generation = stored.Generation ?? 0;
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.PreconditionFailed && onlyIfNotExists)
        {
            var ours = resent
                ? ObjectStoredByThisCall(storageClient, bucketName, objectName, bytes.Length, () => Md5Base64(bytes), startedUtc, deadline.Token)
                : null;
            if (ours == null) throw AlreadyExists(bucketName, objectName, e);
            generation = ours.Generation ?? 0;
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw ActionTimedOut($"Uploading '{Clean(objectName)}'", "The object may or may not have been stored: check with Object_Exists, or upload it again (an upload replaces the object).", e);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, null); }
    }

    public void Object_DownloadBinary(Authentication authentication, string bucketName, string objectName, out BinaryContent content)
    {
        RequireBucket(bucketName, "BucketName");
        RequireObject(objectName, "ObjectName");

        var storageClient = GetStorageClient(authentication);
        using var deadline = new CancellationTokenSource(ActionDeadline);
        try
        {
            // DownloadObject has no retry options of its own; every attempt starts with a fresh buffer.
            content = WithTransferRetry(() =>
            {
                using var stream = new CappedDownloadStream();
                var obj = storageClient.DownloadObjectAsync(bucketName, objectName, stream,
                    new DownloadObjectOptions { ChunkSize = 1024 * 1024, DownloadValidationMode = DownloadValidationMode.Automatic },
                    deadline.Token).GetAwaiter().GetResult();

                return new BinaryContent
                {
                    Content = stream.ToArray(),
                    ContentType = obj.ContentType ?? ""
                };
            });
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw ActionTimedOut($"Downloading '{Clean(objectName)}'", "Nothing was changed. Retry the action; for a large object use Object_Download or Object_GetSignedUrl.", e);
        }
        catch (Exception e) when (FindInChain<DownloadTooLargeException>(e) != null)
        {
            // The SDK may wrap whatever the target stream throws, so look through the chain.
            throw new Exception($"Object '{Clean(objectName)}' is larger than {MaxDownloadBytes} bytes (5.5 MB), the most an ODC action can return as Binary Data. Use Object_Download to get it as a File, or Object_GetSignedUrl with Operation 'Download' to let the client download it directly from Google.");
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw NotFoundException(storageClient, e, authentication, bucketName, objectName, deadline.Token);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }
    }

    public void Object_List(Authentication authentication, string bucketName, string prefix, int maxResults, string pageToken, string delimiter, out IEnumerable<Object> objects, out string nextPageToken, out IEnumerable<Prefix> prefixList)
    {
        RequireBucket(bucketName, "BucketName");

        var objectResults = new List<Object>();
        var prefixResults = new List<Prefix>();
        nextPageToken = string.Empty;

        if (maxResults < 0)
            throw new ArgumentException($"MaxResults cannot be negative (received {maxResults}). Use 0 to return all objects.");

        var storageClient = GetStorageClient(authentication);
        using var deadline = new CancellationTokenSource(ActionDeadline);
        try
        {
            var options = new ListObjectsOptions();
            if (maxResults > 0) options.PageSize = maxResults;
            if (!string.IsNullOrEmpty(pageToken)) options.PageToken = pageToken;
            if (!string.IsNullOrEmpty(delimiter)) options.Delimiter = delimiter;

            // One request per page (each repeatable after a network failure), so the continuation
            // token and the common prefixes ("folders") are available, not just the items.
            var seenPrefixes = new HashSet<string>();
            while (true)
            {
                var page = ReadObjectsPage(storageClient, bucketName, prefix, options, deadline.Token);

                if (page.Items != null)
                {
                    foreach (var obj in page.Items)
                    {
                        objectResults.Add(new Object
                        {
                            Name = obj.Name ?? "",
                            Size = (long)Math.Min(obj.Size ?? 0, long.MaxValue),
                            ContentType = obj.ContentType ?? "",
                            Updated = ParseTimestamp(obj.UpdatedRaw),
                            Generation = obj.Generation ?? 0,
                            MD5Hash = obj.Md5Hash ?? "",
                            Crc32c = obj.Crc32c ?? ""
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

                if (string.IsNullOrEmpty(page.NextPageToken)) break;
                options = new ListObjectsOptions { Delimiter = options.Delimiter, PageSize = options.PageSize, PageToken = page.NextPageToken };
            }
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw maxResults > 0
                ? ActionTimedOut($"Listing the objects of bucket '{Clean(bucketName)}'", "Nothing was changed. Retry the action.", e)
                : ActionTimedOut($"Listing the objects of bucket '{Clean(bucketName)}'", "There are too many objects for one call: set MaxResults and read the list page by page with PageToken.", e);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, null); }

        objects = objectResults;
        prefixList = prefixResults;
    }

    public void Object_Exists(Authentication authentication, string bucketName, string objectName, out bool exists)
    {
        RequireBucket(bucketName, "BucketName");
        RequireObject(objectName, "ObjectName");

        var storageClient = GetStorageClient(authentication);
        using var deadline = new CancellationTokenSource(ActionDeadline);
        try
        {
            WithNetworkRetry(() => storageClient.GetObjectAsync(bucketName, objectName, null, deadline.Token).GetAwaiter().GetResult());
            exists = true;
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw ActionTimedOut($"Checking object '{Clean(objectName)}'", "Nothing was changed. Retry the action.", e);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            if (!IsObjectNotFound(storageClient, e, bucketName, deadline.Token))
                throw TranslateException(e, authentication, bucketName, null);
            exists = false;
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }
    }

    public void Object_GetMetadata(Authentication authentication, string bucketName, string objectName, out bool exists, out ObjectMetadata metadata, out IEnumerable<MetadataEntry> customMetadata)
    {
        RequireBucket(bucketName, "BucketName");
        RequireObject(objectName, "ObjectName");

        var storageClient = GetStorageClient(authentication);
        using var deadline = new CancellationTokenSource(ActionDeadline);
        exists = false;
        metadata = new ObjectMetadata();
        var custom = new List<MetadataEntry>();

        try
        {
            var obj = WithNetworkRetry(() => storageClient.GetObjectAsync(bucketName, objectName, null, deadline.Token).GetAwaiter().GetResult());
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
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw ActionTimedOut($"Reading the metadata of object '{Clean(objectName)}'", "Nothing was changed. Retry the action.", e);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            if (!IsObjectNotFound(storageClient, e, bucketName, deadline.Token))
                throw TranslateException(e, authentication, bucketName, null);
            exists = false;
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }

        customMetadata = custom;
    }

    public void Object_Delete(Authentication authentication, string bucketName, string objectName)
    {
        RequireBucket(bucketName, "BucketName");
        RequireObject(objectName, "ObjectName");

        var storageClient = GetStorageClient(authentication);
        using var deadline = new CancellationTokenSource(ActionDeadline);

        // Read the generation first and delete only that generation: a delete that carries a
        // precondition is retried by the SDK on transient errors, and a replaced object is never removed.
        long? generation;
        try
        {
            generation = WithNetworkRetry(() => storageClient.GetObjectAsync(bucketName, objectName, null, deadline.Token).GetAwaiter().GetResult().Generation);
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw ActionTimedOut($"Deleting object '{Clean(objectName)}'", "Nothing was deleted. Retry the action.", e);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.Forbidden)
        {
            // A role that may delete but not read objects: fall back to an unguarded delete (not
            // retried, as before 1.7.2) rather than refuse an operation the role allows.
            generation = null;
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw NotFoundException(storageClient, e, authentication, bucketName, objectName, deadline.Token);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }

        // The connector repeats the guarded delete itself (the SDK's retry is off), so it knows when an
        // earlier attempt may have been applied although it failed: the repeat then answers 404, or 412.
        var send = new SendState();
        try
        {
            SendWithRetry(send, retry: generation != null, () =>
                storageClient.DeleteObjectAsync(bucketName, objectName, new DeleteObjectOptions { IfGenerationMatch = generation, RetryOptions = RetryOptions.Never }, deadline.Token).GetAwaiter().GetResult());
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw ActionTimedOut($"Deleting object '{Clean(objectName)}'", "The object may or may not have been deleted: check with Object_Exists.", e);
        }
        catch (Google.GoogleApiException e) when (send.MayHaveApplied && e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // Deleted by the earlier request whose answer was lost.
        }
        catch (Google.GoogleApiException e) when (send.MayHaveApplied && e.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            // Either the earlier request deleted the object, or someone replaced it meanwhile: a 412 alone
            // cannot tell, so look. Only a missing object means the delete happened.
            try
            {
                WithNetworkRetry(() => storageClient.GetObjectAsync(bucketName, objectName, null, deadline.Token).GetAwaiter().GetResult());
            }
            catch (Google.GoogleApiException probe) when (probe.HttpStatusCode == HttpStatusCode.NotFound) { return; }
            catch (Exception probe) when (IsTranslatable(probe))
            {
                throw new Exception($"The delete of object '{Clean(objectName)}' in bucket '{Clean(bucketName)}' could not be confirmed: it was sent again after a failed attempt whose outcome is unknown, and the repeat found the object changed. Check it with Object_Exists.", e);
            }
            throw ReplacedDuringDelete(objectName, bucketName, e);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw ReplacedDuringDelete(objectName, bucketName, e);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw NotFoundException(storageClient, e, authentication, bucketName, objectName, deadline.Token);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }
    }

    public void Object_Copy(Authentication authentication, string sourceBucketName, string sourceObjectName, string destinationBucketName, string destinationObjectName)
    {
        RequireBucket(sourceBucketName, "SourceBucketName");
        RequireObject(sourceObjectName, "SourceObjectName");
        RequireBucket(destinationBucketName, "DestinationBucketName");
        RequireObject(destinationObjectName, "DestinationObjectName");

        var storageClient = GetStorageClient(authentication);
        using var deadline = new CancellationTokenSource(ActionDeadline);
        try
        {
            WithNetworkRetry(() => storageClient.CopyObjectAsync(sourceBucketName, sourceObjectName, destinationBucketName, destinationObjectName,
                new CopyObjectOptions { RetryOptions = RetryOptions.IdempotentRetryOptions }, deadline.Token).GetAwaiter().GetResult());
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw ActionTimedOut($"Copying '{Clean(sourceBucketName)}/{Clean(sourceObjectName)}' to '{Clean(destinationBucketName)}/{Clean(destinationObjectName)}'", "Google copies a large object between locations or storage classes in several steps, which one ODC action may not be able to finish. The destination object exists only if the last step completed: check with Object_Exists.", e);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            var notFound = CopyNotFoundException(storageClient, e, authentication, sourceBucketName, sourceObjectName, destinationBucketName, cancellationToken: deadline.Token);
            // The look at the source may have been cut short by the deadline: then that is the real cause.
            if (deadline.IsCancellationRequested)
                throw ActionTimedOut($"Copying '{Clean(sourceBucketName)}/{Clean(sourceObjectName)}' to '{Clean(destinationBucketName)}/{Clean(destinationObjectName)}'", "Nothing was copied. Retry the action.", e);
            throw notFound;
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, sourceBucketName, sourceObjectName); }
    }

    public void Object_Move(Authentication authentication, string sourceBucketName, string sourceObjectName, string destinationBucketName, string destinationObjectName)
    {
        RequireBucket(sourceBucketName, "SourceBucketName");
        RequireObject(sourceObjectName, "SourceObjectName");
        RequireBucket(destinationBucketName, "DestinationBucketName");
        RequireObject(destinationObjectName, "DestinationObjectName");

        // Copying an object onto itself and then deleting the source would destroy the only copy.
        if (string.Equals(sourceBucketName, destinationBucketName, StringComparison.Ordinal)
            && string.Equals(sourceObjectName, destinationObjectName, StringComparison.Ordinal))
            throw new ArgumentException($"Source and destination are the same object ('{Clean(sourceBucketName)}/{Clean(sourceObjectName)}'); Object_Move would delete it. Use a different destination name.");

        var storageClient = GetStorageClient(authentication);
        using var deadline = new CancellationTokenSource(ActionDeadline);

        // Read the source before touching it: its generation guards both the copy and the delete,
        // so a source replaced by someone else mid-move is reported instead of being lost.
        // Nullable on purpose: a generation of 0 would mean "only if the object does not exist".
        long? generation = null;
        try
        {
            generation = WithNetworkRetry(() => storageClient.GetObjectAsync(sourceBucketName, sourceObjectName, null, deadline.Token).GetAwaiter().GetResult().Generation);
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw ActionTimedOut($"Moving '{Clean(sourceBucketName)}/{Clean(sourceObjectName)}' to '{Clean(destinationBucketName)}/{Clean(destinationObjectName)}'", "Nothing was moved. Retry the action.", e);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw NotFoundException(storageClient, e, authentication, sourceBucketName, sourceObjectName, deadline.Token);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, sourceBucketName, sourceObjectName); }

        try
        {
            WithNetworkRetry(() => storageClient.CopyObjectAsync(sourceBucketName, sourceObjectName, destinationBucketName, destinationObjectName,
                new CopyObjectOptions { IfSourceGenerationMatch = generation, RetryOptions = RetryOptions.IdempotentRetryOptions }, deadline.Token).GetAwaiter().GetResult());
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw ActionTimedOut($"Moving '{Clean(sourceBucketName)}/{Clean(sourceObjectName)}' to '{Clean(destinationBucketName)}/{Clean(destinationObjectName)}'", "Google copies a large object between locations or storage classes in several steps, which one ODC action may not be able to finish. The source was not deleted; the destination object exists only if the last step completed: check with Object_Exists.", e);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new Exception($"The source object '{Clean(sourceBucketName)}/{Clean(sourceObjectName)}' changed while it was being moved; nothing was moved. Retry the move.", e);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            var notFound = CopyNotFoundException(storageClient, e, authentication, sourceBucketName, sourceObjectName, destinationBucketName, generation, deadline.Token);
            // The look at the source may have been cut short by the deadline: then that is the real cause.
            if (deadline.IsCancellationRequested)
                throw ActionTimedOut($"Moving '{Clean(sourceBucketName)}/{Clean(sourceObjectName)}' to '{Clean(destinationBucketName)}/{Clean(destinationObjectName)}'", "Nothing was moved. Retry the action.", e);
            throw notFound;
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, sourceBucketName, sourceObjectName); }

        // Move is copy-then-delete and is not atomic: if the delete fails, both objects exist.
        // Surface that state explicitly instead of a generic error. The delete is guarded by the
        // generation read before the copy, so an overwritten source (newer data) is left alone.
        var send = new SendState();
        try
        {
            SendWithRetry(send, retry: true, () => storageClient.DeleteObjectAsync(sourceBucketName, sourceObjectName,
                new DeleteObjectOptions { IfGenerationMatch = generation, RetryOptions = RetryOptions.Never }, deadline.Token).GetAwaiter().GetResult());
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw ActionTimedOut($"Moving '{Clean(sourceBucketName)}/{Clean(sourceObjectName)}' to '{Clean(destinationBucketName)}/{Clean(destinationObjectName)}'", "The object was copied to the destination, but deleting the source did not finish: both objects may exist. Check the source with Object_Exists.", e);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // Already gone (deleted by an earlier attempt, or concurrently) - the move's end state is reached.
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.PreconditionFailed)
        {
            // After an attempt whose outcome is unknown, a 412 may only mean that attempt deleted the
            // source: look before reporting. If the look itself fails, the state is not known: say so.
            if (send.MayHaveApplied)
            {
                try
                {
                    WithNetworkRetry(() => storageClient.GetObjectAsync(sourceBucketName, sourceObjectName, null, deadline.Token).GetAwaiter().GetResult());
                }
                catch (Google.GoogleApiException probe) when (probe.HttpStatusCode == HttpStatusCode.NotFound) { return; }
                catch (Exception probe) when (IsTranslatable(probe))
                {
                    throw new Exception($"The object was copied to '{Clean(destinationBucketName)}/{Clean(destinationObjectName)}', but deleting the source '{Clean(sourceBucketName)}/{Clean(sourceObjectName)}' could not be confirmed: it was sent again after a failed attempt whose outcome is unknown, and the repeat found the source changed. Both objects may exist: check the source with Object_Exists.", e);
                }
            }
            throw new Exception($"The object was copied to '{Clean(destinationBucketName)}/{Clean(destinationObjectName)}', but the source '{Clean(sourceBucketName)}/{Clean(sourceObjectName)}' was modified during the move and was not deleted - both objects exist and the source holds newer data.", e);
        }
        catch (Exception e) when (IsTranslatable(e))
        {
            // A refusal (403, 400...) leaves the source in place for certain. After a timeout, or after
            // attempts whose outcome is unknown, Google may still have deleted it.
            string cause = TranslateException(e, authentication, sourceBucketName, sourceObjectName).Message;
            if (send.MayHaveApplied || IsAmbiguousFailure(e) || e is OperationCanceledException)
                throw new Exception($"The object was copied to '{Clean(destinationBucketName)}/{Clean(destinationObjectName)}', but deleting the source '{Clean(sourceBucketName)}/{Clean(sourceObjectName)}' could not be confirmed. Both objects may exist: check the source with Object_Exists. Cause: {cause}", e);
            throw new Exception($"The object was copied to '{Clean(destinationBucketName)}/{Clean(destinationObjectName)}' but the source '{Clean(sourceBucketName)}/{Clean(sourceObjectName)}' could not be deleted - both objects currently exist. Cause: {cause}", e);
        }
    }

    public void Object_UpdateMetadata(Authentication authentication, string bucketName, string objectName, string contentType, string contentEncoding, string contentDisposition, string cacheControl, IEnumerable<MetadataEntry> metadata)
    {
        RequireBucket(bucketName, "BucketName");
        RequireObject(objectName, "ObjectName");

        var changes = ToMetadataDictionary(metadata);
        bool hasFieldChange = !string.IsNullOrEmpty(contentType) || !string.IsNullOrEmpty(contentEncoding)
            || !string.IsNullOrEmpty(contentDisposition) || !string.IsNullOrEmpty(cacheControl);
        if (!hasFieldChange && changes == null)
            throw new ArgumentException("Nothing to update: provide at least one of ContentType, ContentEncoding, ContentDisposition, CacheControl, or a non-empty Metadata list.");
        // These four are stored with the object and sent back as response headers when it is served.
        ValidateContentType(contentType);
        RejectControlCharacters(contentEncoding, "ContentEncoding");
        RejectControlCharacters(contentDisposition, "ContentDisposition");
        RejectControlCharacters(cacheControl, "CacheControl");

        var storageClient = GetStorageClient(authentication);
        using var deadline = new CancellationTokenSource(ActionDeadline);

        bool sending = false;
        try
        {
            // Fetch the current object to get its generation and metageneration preconditions, then send
            // a PATCH containing only the changed fields so untouched properties and ACLs are never sent back.
            var obj = WithNetworkRetry(() => storageClient.GetObjectAsync(bucketName, objectName, null, deadline.Token).GetAwaiter().GetResult());

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

            var send = new SendState();
            try
            {
                // Setting any precondition turns off the SDK's automatic ones, so both are explicit: the
                // generation guards against a content replacement (which resets metageneration to 1).
                // The connector repeats the request itself (the SDK's retry is off), so it knows when a 412
                // answers a repeat, which the first request's own change may have caused.
                sending = true;
                SendWithRetry(send, retry: true, () => storageClient.PatchObjectAsync(patch,
                    new PatchObjectOptions { IfGenerationMatch = obj.Generation, IfMetagenerationMatch = obj.Metageneration, RetryOptions = RetryOptions.Never }, deadline.Token).GetAwaiter().GetResult());
            }
            catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.PreconditionFailed && send.MayHaveApplied)
            {
                throw new Exception($"The metadata update of object '{Clean(objectName)}' in bucket '{Clean(bucketName)}' may already have been applied: it was sent again after a failed attempt whose outcome is unknown, and the repeat found the object changed. Read its metadata with Object_GetMetadata before retrying.", e);
            }
            catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw new Exception($"Object '{Clean(objectName)}' in bucket '{Clean(bucketName)}' was changed by another request while its metadata was being updated, so nothing was changed. Read it again and retry.", e);
            }
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw sending
                ? ActionTimedOut($"Updating the metadata of object '{Clean(objectName)}'", "The update may or may not have been applied: read the object with Object_GetMetadata.", e)
                : ActionTimedOut($"Updating the metadata of object '{Clean(objectName)}'", "Nothing was changed. Retry the action.", e);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            throw NotFoundException(storageClient, e, authentication, bucketName, objectName, deadline.Token);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }
    }

    public void Object_DeleteByPrefix(Authentication authentication, string bucketName, string prefix, out long deletedCount)
    {
        RequireBucket(bucketName, "BucketName");

        deletedCount = 0;
        var stopwatch = Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Prefix cannot be empty - it would delete every object in the bucket. To do that intentionally, delete the bucket or list and delete the objects explicitly.");

        var storageClient = GetStorageClient(authentication);

        // The deadline cancels a request still in flight, so the call ends before ODC's 95-second kill.
        using var deadline = new CancellationTokenSource(DeleteByPrefixDeadline);
        long deleted = 0;

        Exception BudgetExceeded() => new Exception($"Deleted {deleted} objects under prefix '{Clean(prefix)}' before the {DeleteByPrefixBudget.TotalSeconds:0}-second time budget of one call ran out (ODC limits an action to 95 seconds). Nothing was rolled back; call Object_DeleteByPrefix again with the same prefix to delete the rest.");

        // Delete page by page: each delete carries the generation it was listed with (which makes the SDK
        // retry transient failures), and GCS page tokens are positional, so deleting listed objects does
        // not disturb the paging.
        string? token = null;
        while (true)
        {
            if (stopwatch.Elapsed >= DeleteByPrefixBudget) throw BudgetExceeded();

            Google.Apis.Storage.v1.Data.Objects page;
            try
            {
                page = ReadObjectsPage(storageClient, bucketName, prefix, new ListObjectsOptions { PageSize = DeleteByPrefixPageSize, PageToken = token }, deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { throw BudgetExceeded(); }
            catch (Exception e) when (IsTranslatable(e))
            {
                if (deleted == 0) throw TranslateException(e, authentication, bucketName, null);
                throw new Exception($"Deleted {deleted} objects under prefix '{Clean(prefix)}', then listing the remaining objects failed: {TranslateException(e, authentication, bucketName, null).Message}", e);
            }

            foreach (var obj in page.Items ?? new List<Google.Apis.Storage.v1.Data.Object>())
            {
                // One call may not finish the work: stop before ODC kills the action and report where it
                // stopped, so the caller can resume with the same prefix.
                if (stopwatch.Elapsed >= DeleteByPrefixBudget) throw BudgetExceeded();

                var send = new SendState();
                try
                {
                    WithNetworkRetry(() =>
                    {
                        storageClient.DeleteObjectAsync(bucketName, obj.Name,
                            new DeleteObjectOptions { IfGenerationMatch = obj.Generation }, deadline.Token).GetAwaiter().GetResult();
                        return 0;
                    }, send);
                    deleted++;
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { throw BudgetExceeded(); }
                catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound || e.HttpStatusCode == HttpStatusCode.PreconditionFailed)
                {
                    // Already gone (404) or replaced since listing (412) - skip without counting and keep going.
                    // A 404 after an attempt whose answer was lost is that attempt's own doing: count it.
                    if (e.HttpStatusCode == HttpStatusCode.NotFound && send.MayHaveApplied) deleted++;
                }
                catch (Exception e) when (IsTranslatable(e))
                {
                    // TranslateException gives the same message a whole-object call would give, so the
                    // partial-progress error carries an actionable cause instead of Google's raw text.
                    throw new Exception($"Deleted {deleted} objects under prefix '{Clean(prefix)}', then failed on '{Clean(obj.Name)}': {TranslateException(e, authentication, bucketName, obj.Name).Message}", e);
                }
            }

            if (string.IsNullOrEmpty(page.NextPageToken)) break;
            token = page.NextPageToken;
        }

        deletedCount = deleted;
    }

    public void Object_GetSignedUrl(Authentication authentication, string bucketName, string objectName, int expirationMinutes, out string url, string operation = "Download", string contentType = "")
    {
        RequireBucket(bucketName, "BucketName");
        RequireObject(objectName, "ObjectName");

        if (expirationMinutes <= 0)
            throw new ArgumentException($"ExpirationMinutes must be greater than zero (received {expirationMinutes}).");
        if (expirationMinutes > 10080)
            throw new ArgumentException($"ExpirationMinutes cannot exceed 10080 minutes (7 days), the maximum validity of a Google Cloud V4 signed URL (received {expirationMinutes}).");

        ValidateContentType(contentType);

        // Validate the Operation before building the signer, so a typo fails without any network call.
        var method = (operation ?? "").Trim().ToUpperInvariant() switch
        {
            "DOWNLOAD" => HttpMethod.Get,
            "UPLOAD" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            _ => throw new ArgumentException($"Invalid Operation '{Clean(operation)}'. Use 'Download', 'Upload', or 'Delete'.")
        };

        // Federation signs with a key Google rotates, so it guarantees a signature for 12 hours only.
        if (expirationMinutes > MaxFederatedSignedUrlMinutes && ResolveAuthMethod(authentication) == AuthMethod.WorkloadIdentityFederation)
            throw new ArgumentException($"ExpirationMinutes cannot exceed {MaxFederatedSignedUrlMinutes} minutes (12 hours) with Workload Identity Federation: Google signs these URLs with a key it rotates, so a longer-lived URL could stop working before it expires (received {expirationMinutes}).");

        var urlSigner = GetUrlSigner(authentication);
        using var deadline = new CancellationTokenSource(ActionDeadline);

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
            // With federation, signing is a request to IAM signBlob. Google's credential code repeats it
            // after a 500, 502, 503 or 504, but not after a network failure, a 408 or a 429: those three
            // are repeated here, and only those, so attempts are never multiplied with the library's.
            url = WithNetworkRetry(
                () => urlSigner.SignAsync(template, UrlSigner.Options.FromDuration(TimeSpan.FromMinutes(expirationMinutes)), deadline.Token).GetAwaiter().GetResult(),
                alsoRepeat: e => e is Google.GoogleApiException { HttpStatusCode: HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests });
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw ActionTimedOut($"Signing the URL for object '{Clean(objectName)}'", "Retry the action.", e);
        }
        catch (Google.GoogleApiException e) { throw FriendlySigningException(e, authentication); }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, objectName); }
    }

    public void Bucket_List(Authentication authentication, out IEnumerable<Bucket> buckets)
    {
        Require(authentication.ProjectId, "ProjectId");

        var storageClient = GetStorageClient(authentication);
        using var deadline = new CancellationTokenSource(ActionDeadline);
        try
        {
            var results = new List<Bucket>();
            var options = new ListBucketsOptions();
            while (true)
            {
                var page = ReadBucketsPage(storageClient, authentication.ProjectId.Trim(), options, deadline.Token);
                foreach (var b in page.Items ?? new List<Google.Apis.Storage.v1.Data.Bucket>())
                {
                    results.Add(new Bucket
                    {
                        Name = b.Name ?? "",
                        Location = b.Location ?? "",
                        StorageClass = b.StorageClass ?? "",
                        Created = ParseTimestamp(b.TimeCreatedRaw)
                    });
                }

                if (string.IsNullOrEmpty(page.NextPageToken)) break;
                options = new ListBucketsOptions { PageToken = page.NextPageToken };
            }

            buckets = results;
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw ActionTimedOut($"Listing the buckets of project '{Clean(authentication.ProjectId.Trim())}'", "Nothing was changed. Retry the action.", e);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, null, null); }
    }

    public void Bucket_Create(Authentication authentication, string bucketName, string location, string storageClass = "", bool allowFineGrainedAcl = false, bool allowPublicAccess = false)
    {
        Require(authentication.ProjectId, "ProjectId");
        RequireBucket(bucketName, "BucketName");
        string? normalizedClass = ToStorageClass(storageClass);

        var storageClient = GetStorageClient(authentication);
        using var deadline = new CancellationTokenSource(ActionDeadline);
        var send = new SendState();
        var started = DateTime.UtcNow;
        try
        {
            SendWithRetry(send, retry: true, () => storageClient.CreateBucketAsync(
                authentication.ProjectId.Trim(),
                new Google.Apis.Storage.v1.Data.Bucket
                {
                    Name = bucketName,
                    Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim(),
                    StorageClass = normalizedClass,
                    // Secure by default: uniform access (no per-object ACLs) and public access prevention
                    // enforced. Both can be turned off by the caller. Same defaults as the O11 connector.
                    IamConfiguration = new Google.Apis.Storage.v1.Data.Bucket.IamConfigurationData
                    {
                        UniformBucketLevelAccess = new Google.Apis.Storage.v1.Data.Bucket.IamConfigurationData.UniformBucketLevelAccessData { Enabled = !allowFineGrainedAcl },
                        PublicAccessPrevention = allowPublicAccess ? "inherited" : "enforced"
                    }
                }, new CreateBucketOptions { RetryOptions = RetryOptions.Never }, deadline.Token).GetAwaiter().GetResult());
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw ActionTimedOut($"Creating bucket '{Clean(bucketName)}'", "The bucket may or may not have been created: check with Bucket_Exists.", e);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // Creating a bucket never 404s on the bucket itself: the project is what was not found.
            string details = ErrorDetails(e);
            throw new Exception($"Bucket '{Clean(bucketName)}' was not created: project '{Clean(authentication.ProjectId.Trim())}' was not found, or the service account cannot access it. Check ProjectId. Details: {Clean(details)}", e);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.Conflict && send.MayHaveApplied)
        {
            // A repeat after an attempt whose outcome is unknown answered "already exists". If the bucket
            // is readable and was created since this call began (allowing a minute of clock difference
            // with Google), that attempt created it. An older bucket is someone else's.
            try
            {
                var created = ParseTimestamp(GetBucketWithin(storageClient, bucketName, deadline.Token).TimeCreatedRaw);
                if (created >= started.AddMinutes(-1) && created <= DateTime.UtcNow.AddMinutes(1)) return;
            }
            catch (Exception probe) when (IsTranslatable(probe)) { }
            if (deadline.IsCancellationRequested)
                throw ActionTimedOut($"Creating bucket '{Clean(bucketName)}'", "The bucket may or may not have been created: check with Bucket_Exists.", e);
            throw TranslateException(e, authentication, bucketName, null);
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, null); }
    }

    public void Bucket_Delete(Authentication authentication, string bucketName)
    {
        RequireBucket(bucketName, "BucketName");

        var storageClient = GetStorageClient(authentication);
        using var deadline = new CancellationTokenSource(ActionDeadline);
        var send = new SendState();
        try
        {
            SendWithRetry(send, retry: true, () => storageClient.DeleteBucketAsync(bucketName, new DeleteBucketOptions { RetryOptions = RetryOptions.Never }, deadline.Token).GetAwaiter().GetResult());
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw ActionTimedOut($"Deleting bucket '{Clean(bucketName)}'", "The bucket may or may not have been deleted: check with Bucket_Exists.", e);
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound && send.MayHaveApplied)
        {
            // Deleted by the earlier request whose answer was lost.
        }
        catch (Exception e) when (IsTranslatable(e)) { throw TranslateException(e, authentication, bucketName, null); }
    }

    public void Bucket_Exists(Authentication authentication, string bucketName, out bool exists)
    {
        RequireBucket(bucketName, "BucketName");

        var storageClient = GetStorageClient(authentication);
        using var deadline = new CancellationTokenSource(ActionDeadline);
        try
        {
            GetBucketWithin(storageClient, bucketName, deadline.Token);
            exists = true;
        }
        catch (OperationCanceledException e) when (deadline.IsCancellationRequested)
        {
            throw ActionTimedOut($"Checking bucket '{Clean(bucketName)}'", "Nothing was changed. Retry the action.", e);
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
            throw new ArgumentException($"{Clean(name)} cannot be empty.");
    }

    /// <summary>
    /// What a bucket name can be made of: lowercase letters, digits, dashes, underscores and dots, up to
    /// 222 characters, starting and ending with a letter or digit. Google's SDK has a looser pattern:
    /// it ends with $, which also matches before a final line break, and it accepts "." and "..", which
    /// a URL treats as path steps. Google's further rules (a minimum length, reserved names) are left to
    /// Google, which answers them with a clear error.
    /// </summary>
    private static readonly Regex ValidBucketName = new("^[0-9a-z](?:[0-9a-z._-]{0,220}[0-9a-z])?\\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Rejects an empty or malformed bucket name before any client is created. Without it the SDK throws
    /// its own exception, which carries the raw name (line breaks included) into the ODC error log, and a
    /// name ending in a line break would be signed into a URL.
    /// </summary>
    private static void RequireBucket(string? value, string name)
    {
        Require(value, name);
        if (!ValidBucketName.IsMatch(value!))
            throw new ArgumentException($"{name} '{Clean(value)}' is not a valid bucket name. Bucket names contain only lowercase letters, digits, dashes, underscores and dots, start and end with a letter or digit, and are at most 222 characters long.");
    }

    /// <summary>
    /// Rejects an empty object name, and the names "." and "..", which Google does not allow: in the
    /// download URL they are path steps, so the request would go to the bucket or its listing instead.
    /// </summary>
    private static void RequireObject(string? value, string name)
    {
        Require(value, name);
        if (value == "." || value == "..")
            throw new ArgumentException($"{name} '{value}' is not a valid object name: Google Cloud Storage does not allow the names '.' and '..'.");
    }

    /// <summary>
    /// A header value stored with an object (content encoding, content disposition, cache control) can
    /// never hold a control character: a line break in it would become a second header when the object is served.
    /// </summary>
    private static void RejectControlCharacters(string? value, string name) => RequireHeaderValue(value, name);

    /// <summary>
    /// Makes caller input and Google's error text safe to put in an error message: control characters
    /// (line breaks, tabs) and invisible formatting characters (such as the bidirectional overrides
    /// U+202E and U+2066-2069, also above U+FFFF), the Unicode line and paragraph separators (U+2028,
    /// U+2029) and unpaired surrogates become spaces, so a crafted object name cannot forge or reorder
    /// lines in the ODC error log, and the text is cut at 500 characters.
    /// </summary>
    internal static string Clean(string? text) => Clean(text, 500);

    /// <summary><see cref="Clean(string?)"/> with another length limit; the cut never splits a surrogate pair.</summary>
    internal static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        // Cut first: a proxy's multi-megabyte error page must not be walked in full. Cleaning can only
        // shorten the text (a pair of UTF-16 units becomes one space), by half at most.
        bool cutEarly = text.Length > 2 * maxLength + 2;
        if (cutEarly) text = TruncateUtf16(text, 2 * maxLength + 2);
        var cleaned = MaskCredentials(CleanCharacters(text));
        return cutEarly || cleaned.Length > maxLength ? TruncateUtf16(cleaned, maxLength) + "..." : cleaned;
    }

    /// <summary>
    /// A proxy or an identity provider may echo the request it rejected, Authorization header included,
    /// and Google's token exchange may repeat the token it refused. Anything shaped like a bearer token,
    /// a Basic credential, a Google access token or a JWT (which always starts with "eyJ") is masked.
    /// </summary>
    private static readonly Regex CredentialShapes = new(
        @"\b(?<scheme>Bearer)\s+[A-Za-z0-9._~+/=_-]{20,}|\b(?<scheme>Basic)\s+(?=[A-Za-z0-9+/]*[0-9+/=])[A-Za-z0-9+/]{16,}={0,2}|\bya29\.[A-Za-z0-9._-]{10,}|\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static string MaskCredentials(string text) =>
        CredentialShapes.Replace(text, m => m.Groups["scheme"].Success ? m.Groups["scheme"].Value + " ***" : "***");

    /// <summary>
    /// Replaces, code point by code point, every character that can break or reorder a log line with one
    /// space. Walking by code point (not by char) is what catches format characters above U+FFFF.
    /// </summary>
    private static string CleanCharacters(string text)
    {
        var cleaned = new StringBuilder(text.Length);
        for (int index = 0; index < text.Length;)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(index), out var rune, out int consumed);
            if (status != System.Buffers.OperationStatus.Done)
            {
                // An unpaired surrogate: not valid text, and it breaks JSON log encoders.
                cleaned.Append(' ');
                index++;
                continue;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                cleaned.Append(' ');
            else
                cleaned.Append(rune.ToString());
            index += consumed;
        }
        return cleaned.ToString();
    }

    /// <summary>Cuts to at most maxLength UTF-16 units, one less when the cut would split a surrogate pair.</summary>
    private static string TruncateUtf16(string value, int maxLength)
    {
        if (maxLength <= 0) return "";
        int length = Math.Min(value.Length, maxLength);
        if (length > 0 && length < value.Length && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }

    /// <summary>
    /// Rejects a missing File (null, or ODC's NullFile(), which has no storage key) before any request.
    /// </summary>
    private static void RequireFile(IOSFile? file)
    {
        if (file == null || string.IsNullOrEmpty(file.Key))
            throw new ArgumentException("File cannot be empty. Pass a File from an Upload widget, an entity attribute or another action.");
    }

    /// <summary>
    /// Validates StorageClass before any request. Empty means "not sent" (Google's default, STANDARD);
    /// otherwise it is upper-cased so 'nearline' works like 'NEARLINE'.
    /// </summary>
    private static string? ToStorageClass(string? value)
    {
        var trimmed = (value ?? "").Trim();
        if (trimmed.Length == 0) return null;
        var upper = trimmed.ToUpperInvariant();
        if (upper is "STANDARD" or "NEARLINE" or "COLDLINE" or "ARCHIVE") return upper;
        throw new ArgumentException($"Invalid StorageClass '{Clean(value)}'. Use STANDARD, NEARLINE, COLDLINE or ARCHIVE, or leave it empty for STANDARD.");
    }

    /// <summary>The object sent by both upload actions. Empty header values are not sent.</summary>
    private static Google.Apis.Storage.v1.Data.Object NewUploadObject(string bucketName, string objectName, string? contentType, IEnumerable<MetadataEntry> metadata,
        string? contentDisposition, string? cacheControl, string? contentEncoding) => new()
    {
        Bucket = bucketName,
        Name = objectName,
        ContentType = string.IsNullOrEmpty(contentType) ? null : contentType,
        ContentDisposition = string.IsNullOrEmpty(contentDisposition) ? null : contentDisposition,
        CacheControl = string.IsNullOrEmpty(cacheControl) ? null : cacheControl,
        ContentEncoding = string.IsNullOrEmpty(contentEncoding) ? null : contentEncoding,
        Metadata = ToMetadataDictionary(metadata)
    };

    private static Exception AlreadyExists(string bucketName, string objectName, Exception inner) =>
        new($"Object '{Clean(objectName)}' already exists in bucket '{Clean(bucketName)}'. OnlyIfNotExists is True, so the existing object was left unchanged.", inner);

    /// <summary>
    /// After a repeated OnlyIfNotExists upload is answered 412: the object now in the bucket, when it is the
    /// one this call stored, else null. If a send stored the object and its response was lost, the repeat
    /// finds the name taken and is refused with 412 - the same answer someone else's object gives - so this
    /// read decides which it is. All three must match: the size, the MD5 (Google reports the Base64 MD5),
    /// and a creation time no earlier than this call (Google's clock can be up to a minute behind). Any
    /// failure of the read counts as "not ours". Same rule as the O11 connector.
    /// </summary>
    private static Google.Apis.Storage.v1.Data.Object? ObjectStoredByThisCall(StorageClient client, string bucketName, string objectName, long size, Func<string> md5Base64, DateTime startedUtc, CancellationToken cancellationToken)
    {
        try
        {
            var existing = WithNetworkRetry(() => client.GetObjectAsync(bucketName, objectName, null, cancellationToken).GetAwaiter().GetResult());
            if (existing.Size != (ulong)size) return null;
            if (!string.Equals(existing.Md5Hash, md5Base64(), StringComparison.Ordinal)) return null;
            return ParseTimestamp(existing.TimeCreatedRaw) >= startedUtc.AddMinutes(-1) ? existing : null;
        }
        catch (Exception e) when (IsTranslatable(e) || e is FileStorageException) { return null; }
    }

    /// <summary>The Base64 MD5 of the bytes: the form Google reports in an object's md5Hash.</summary>
    private static string Md5Base64(byte[] content) => Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(content));

    /// <summary>The Base64 MD5 of an ODC File, read as a stream (only needed in the rare lost-response case).</summary>
    private static string Md5Base64(IFileStorage fileStorage, IOSFile file, CancellationToken cancellationToken)
    {
        using var source = fileStorage.ReadFileAsStreamAsync(file, cancellationToken).GetAwaiter().GetResult();
        return Convert.ToBase64String(System.Security.Cryptography.MD5.HashData(source));
    }

    /// <summary>
    /// A content type travels in an HTTP header, which takes printable ASCII only. A line break would
    /// inject extra headers (or extra signed lines in a signed URL); any other control or non-ASCII
    /// character makes .NET refuse the request, which used to be retried and reported as a network error.
    /// </summary>
    private static void ValidateContentType(string? contentType) => RequireHeaderValue(contentType, "ContentType");

    /// <summary>
    /// A value that travels in an HTTP header (ContentType, ContentDisposition, CacheControl,
    /// ContentEncoding) must be printable ASCII. Same messages as the O11 connector. Empty is allowed.
    /// </summary>
    private static void RequireHeaderValue(string? value, string name)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0)
            throw new ArgumentException($"{name} must not contain line breaks.");
        if (value.Any(ch => ch < ' ' || ch > '~'))
            throw new ArgumentException($"{name} must contain only printable ASCII characters (no control characters, accented letters or other non-ASCII characters).");
    }

    /// <summary>
    /// The name of a downloaded File: the last part of the object name ("reports/2026/q1.pdf" gives
    /// "q1.pdf"), cleaned like an error message (<see cref="CleanCharacters"/>), or "download" when the
    /// name ends with '/' or nothing readable is left. Cut to 200 characters, well inside the 1,500 bytes
    /// ODC allows for a File's metadata.
    /// </summary>
    internal static string FileNameFor(string objectName)
    {
        var name = objectName.TrimEnd('/');
        name = name.Length == 0 || objectName.EndsWith('/') ? "download" : name[(name.LastIndexOf('/') + 1)..];
        name = CleanCharacters(name).Trim();
        // "." and ".." are directory steps, not file names.
        if (name.Length == 0 || name == "." || name == "..") name = "download";
        return TruncateUtf16(name, 200);
    }

    private static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.#} MB" : $"{bytes} bytes";

    /// <summary>ODC storage refused to read or create a File (for example a temporary File that expired).</summary>
    private static Exception FileStorageFailure(FileStorageException e) =>
        new Exception($"ODC file storage failed: {Clean(e.Message)}", e);

    /// <summary>The error of an action stopped by <see cref="ActionDeadline"/>: what it was doing, and what state that leaves.</summary>
    private static Exception ActionTimedOut(string what, string state, Exception inner) =>
        new Exception($"{what} did not finish within {ActionDeadline.TotalSeconds:0} seconds, the most an ODC action can spend on it. {state}", inner);

    /// <summary>
    /// Fetches a bucket within the action deadline. Google.Cloud.Storage.V1 5.0.0's GetBucketAsync ignores
    /// the cancellation token it is given, so waiting for it with the token is what stops the action; the
    /// token is still passed to the SDK for versions that honour it.
    /// </summary>
    private static Google.Apis.Storage.v1.Data.Bucket GetBucketWithin(StorageClient client, string bucketName, CancellationToken cancellationToken) =>
        WithNetworkRetry(() => client.GetBucketAsync(bucketName, null, cancellationToken).WaitAsync(cancellationToken).GetAwaiter().GetResult());

    /// <summary>
    /// Runs one upload or download attempt, retrying transient failures (408, 429, 500, 502, 503, 504,
    /// network and I/O errors) after each of <see cref="TransferRetryDelays"/>. Never retries a too-large
    /// download, a cancellation or timeout (a retry would pass ODC's 95-second limit), or any other 4xx,
    /// and stops retrying once <see cref="TransferRetryWindow"/> has passed.
    /// </summary>
    private static T WithTransferRetry<T>(Func<T> attempt, Action? beforeRetry = null)
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
                // Set only when a repeat really follows (not in a finally), so a 412 can be judged correctly.
                beforeRetry?.Invoke();
                Thread.Sleep(delays[tried]);
            }
        }
    }

    /// <summary>
    /// Runs one idempotent request, repeating it after a network failure (the connection failed: an
    /// HttpRequestException or a network IOException) after each of <see cref="TransferRetryDelays"/>, and
    /// never once <see cref="TransferRetryWindow"/> has passed. Google's SDK repeats these requests by itself
    /// when Google answers 408, 429 or 5xx, but not when the connection fails. When <paramref name="state"/>
    /// is given, it records that an attempt whose outcome is unknown came before the one that answered.
    /// <paramref name="alsoRepeat"/> names further failures to repeat, for the one caller whose library
    /// does not repeat them itself.
    /// </summary>
    private static T WithNetworkRetry<T>(Func<T> request, SendState? state = null, Func<Exception, bool>? alsoRepeat = null)
    {
        var delays = TransferRetryDelays;
        var stopwatch = Stopwatch.StartNew();
        for (int tried = 0; ; tried++)
        {
            try
            {
                return UnwrapSingleAggregate(request);
            }
            catch (Exception e) when (tried < delays.Length && stopwatch.Elapsed < TransferRetryWindow
                && (e is HttpRequestException || IsNetworkIo(e) || alsoRepeat?.Invoke(e) == true))
            {
                if (state != null) state.MayHaveApplied = true;
                Thread.Sleep(delays[tried]);
            }
        }
    }

    /// <summary>One page of an object listing: exactly one request, repeated after a network failure.</summary>
    private static Google.Apis.Storage.v1.Data.Objects ReadObjectsPage(StorageClient client, string bucketName, string? prefix, ListObjectsOptions options, CancellationToken cancellationToken) =>
        WithNetworkRetry(() =>
        {
            var pages = client.ListObjectsAsync(bucketName, prefix, options).AsRawResponses().GetAsyncEnumerator(cancellationToken);
            try
            {
                return pages.MoveNextAsync().AsTask().GetAwaiter().GetResult()
                    ? pages.Current
                    : new Google.Apis.Storage.v1.Data.Objects();
            }
            finally
            {
                pages.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });

    /// <summary>One page of a bucket listing: exactly one request, repeated after a network failure.</summary>
    private static Google.Apis.Storage.v1.Data.Buckets ReadBucketsPage(StorageClient client, string projectId, ListBucketsOptions options, CancellationToken cancellationToken) =>
        WithNetworkRetry(() =>
        {
            var pages = client.ListBucketsAsync(projectId, options).AsRawResponses().GetAsyncEnumerator(cancellationToken);
            try
            {
                return pages.MoveNextAsync().AsTask().GetAwaiter().GetResult()
                    ? pages.Current
                    : new Google.Apis.Storage.v1.Data.Buckets();
            }
            finally
            {
                pages.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });

    /// <summary>
    /// The SDK's synchronous download waits on a task, so a failure (notably the request timeout's
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

    /// <summary>
    /// An I/O failure of the connection, as opposed to the file-system exceptions that also derive from
    /// IOException: a missing or unloadable assembly must surface as itself, not as a network error.
    /// </summary>
    private static bool IsNetworkIo(Exception e) => e is IOException
        && e is not FileNotFoundException
        && e is not FileLoadException
        && e is not DirectoryNotFoundException
        && e is not DriveNotFoundException
        && e is not PathTooLongException;

    /// <summary>
    /// True when Google may have applied the request although it failed: the connection broke, or the
    /// answer was 500, 502, 503 or 504. A 408 or 429 is a refusal: the request was not applied.
    /// </summary>
    private static bool IsAmbiguousFailure(Exception e) =>
        e is HttpRequestException || IsNetworkIo(e)
        || e is Google.GoogleApiException api && api.HttpStatusCode is HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>The 412 of a guarded delete: the object now has another generation, which was not deleted.</summary>
    private static Exception ReplacedDuringDelete(string objectName, string bucketName, Exception e) =>
        new($"Object '{Clean(objectName)}' in bucket '{Clean(bucketName)}' was replaced while it was being deleted: a newer version now exists and was not deleted. Retry the delete if the new version should be removed too.", e);

    private static bool IsRetryableTransferFailure(Exception e)
    {
        if (FindInChain<DownloadTooLargeException>(e) != null) return false;
        if (e is OperationCanceledException) return false; // includes TaskCanceledException
        if (e is Google.GoogleApiException api)
            return api.HttpStatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
        return e is HttpRequestException || IsNetworkIo(e);
    }

    /// <summary>
    /// Translates a 404 from an object request, settling an unclear one (an emulator's or proxy's plain
    /// "Not Found") with <see cref="IsObjectNotFound"/>, so a missing bucket is not reported as a missing object.
    /// </summary>
    private static Exception NotFoundException(StorageClient client, Google.GoogleApiException e, Authentication authentication, string bucketName, string objectName, CancellationToken cancellationToken = default) =>
        TranslateException(e, authentication, bucketName, IsObjectNotFound(client, e, bucketName, cancellationToken) ? objectName : null);

    /// <summary>
    /// Whether an earlier attempt of the request in <see cref="SendWithRetry"/> may have been applied by
    /// Google (<see cref="IsAmbiguousFailure"/>). Only then can a 412 or 404 on the repeat be its own doing.
    /// </summary>
    private sealed class SendState
    {
        public bool MayHaveApplied { get; set; }
    }

    /// <summary>
    /// Sends a request whose SDK retry is off, repeating it on the same transient failures and with the
    /// same delays as transfers when <paramref name="retry"/> is true. <see cref="SendState.MayHaveApplied"/> is
    /// set only when the failed attempt's outcome is unknown, not for a definite rejection such as 408 or 429.
    /// </summary>
    private static void SendWithRetry(SendState state, bool retry, Action send)
    {
        var delays = TransferRetryDelays;
        var stopwatch = Stopwatch.StartNew();
        for (int tried = 0; ; tried++)
        {
            try
            {
                UnwrapSingleAggregate(() => { send(); return 0; });
                return;
            }
            catch (Exception e) when (retry && tried < delays.Length && stopwatch.Elapsed < TransferRetryWindow && IsRetryableTransferFailure(e))
            {
                if (IsAmbiguousFailure(e)) state.MayHaveApplied = true;
                Thread.Sleep(delays[tried]);
            }
        }
    }

    /// <summary>
    /// Decides whether a 404 from an object GET means the object is missing (true) or the bucket is
    /// (false), without relying on Google's English wording alone: "No such object" is an object, any
    /// other message naming the bucket is a bucket, and an ambiguous one (plain "Not Found") is settled
    /// by looking the bucket up: only a 404 on the bucket means the bucket is missing. If that lookup fails
    /// in any other way (e.g. 403: Storage Object Admin cannot read buckets), the object is taken as missing,
    /// as before 1.7.2.
    /// </summary>
    private static bool IsObjectNotFound(StorageClient client, Google.GoogleApiException e, string bucketName, CancellationToken cancellationToken = default)
    {
        string msg = ErrorDetails(e);
        if (msg.IndexOf("No such object", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (msg.IndexOf("bucket", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        try
        {
            GetBucketWithin(client, bucketName, cancellationToken);
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
    private static Exception CopyNotFoundException(StorageClient storageClient, Google.GoogleApiException e, Authentication authentication, string sourceBucketName, string sourceObjectName, string destinationBucketName, long? expectedSourceGeneration = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var probed = WithNetworkRetry(() => storageClient.GetObjectAsync(sourceBucketName, sourceObjectName, null, cancellationToken).GetAwaiter().GetResult());
            // A source that was replaced meanwhile is a change, not a missing bucket (Move only).
            if (expectedSourceGeneration.HasValue && probed.Generation != expectedSourceGeneration)
                return new Exception($"The source object '{Clean(sourceBucketName)}/{Clean(sourceObjectName)}' changed while it was being moved; nothing was moved. Retry the move.", e);
            return FriendlyException(e, authentication, destinationBucketName, null);
        }
        catch (Google.GoogleApiException probe) when (probe.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return NotFoundException(storageClient, probe, authentication, sourceBucketName, sourceObjectName, cancellationToken);
        }
        catch (Exception probe) when (IsTranslatable(probe))
        {
            return new Exception($"The copy from '{Clean(sourceBucketName)}/{Clean(sourceObjectName)}' failed with 404: either that source object or the destination bucket '{Clean(destinationBucketName)}' does not exist, and checking the source failed: {TranslateException(probe, authentication, sourceBucketName, sourceObjectName).Message}", e);
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
        string msg = ErrorDetails(e);
        if (msg.IndexOf("No such object", StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        return msg.IndexOf("bucket", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// True for every exception type the actions translate into an actionable message: a Google API
    /// error, a token-endpoint failure, a subject-token failure, an identity provider failure, an
    /// I/O or download-checksum failure, or the network/timeout failures of a request to Google. Every
    /// cancellation counts: the actions catch their own deadline first, and a look-up made inside a catch
    /// block after the deadline must end in a message, never in a raw OperationCanceledException.
    /// </summary>
    internal static bool IsTranslatable(Exception e) =>
        e is Google.GoogleApiException
        || e is TokenResponseException
        || e is Google.Apis.Auth.OAuth2.SubjectTokenException
        || e is IdentityProviderException
        || IsNetworkIo(e)
        || e is HttpRequestException
        || e is OperationCanceledException; // a request timeout (TaskCanceledException), or a token already cancelled before a look-up

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
                ForgetSuppliedToken(authentication);
                return FriendlyAuthException(token, authentication);
            case Google.Apis.Auth.OAuth2.SubjectTokenException:
                ForgetSuppliedToken(authentication);
                // The SDK wraps every subject-token failure in SubjectTokenException, hiding our
                // IdentityProviderException behind a generic message; unwrap it when present.
                var identityProvider = FindInChain<IdentityProviderException>(e);
                if (identityProvider != null)
                    return new Exception(identityProvider.Message, e);
                return new Exception($"Workload Identity Federation could not obtain the identity provider token: {Clean(InnermostMessage(e))}", e);
            case IdentityProviderException:
                return new Exception(e.Message, e);
            case IOException io when io.Message != null && io.Message.StartsWith("Incorrect hash", StringComparison.Ordinal):
                // In Google.Cloud.Storage.V1 5.0, a download CRC32C mismatch throws an IOException starting with "Incorrect hash:".
                return new Exception($"The downloaded data did not match the object's checksum, so no content was returned. This is usually a transient network error - retry the download. Details: {Clean(e.Message)}", e);
            case IOException:
                return new Exception($"The connection to Google Cloud was interrupted: {Clean(InnermostMessage(e).TrimEnd('.'))}. Retry the request.", e);
            case HttpRequestException:
                return new Exception($"Could not reach Google Cloud: {Clean(InnermostMessage(e).TrimEnd('.'))}. Check the outbound HTTPS access to *.googleapis.com.", e);
            default:
                // Actions take no CancellationToken, so a TaskCanceledException is a request timeout.
                return new Exception($"The request to Google Cloud timed out (no response within {StorageRequestTimeout.TotalSeconds:0} seconds). Retry the action; for a large upload or download, use Object_GetSignedUrl and let the client transfer the file directly.", e);
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
    /// Google's own error text: the message of its JSON error, or, when the body was not JSON (a proxy,
    /// or a plain-text answer), the raw body the SDK keeps, or else the exception's message. Callers
    /// pass it through <see cref="Clean(string?)"/> before it reaches an error message.
    /// </summary>
    internal static string ErrorDetails(Google.GoogleApiException e)
    {
        if (!string.IsNullOrEmpty(e.Error?.Message)) return e.Error.Message;
        if (!string.IsNullOrWhiteSpace(e.Error?.ErrorResponseContent)) return e.Error.ErrorResponseContent.Trim();
        return e.Message ?? "";
    }

    /// <summary>
    /// True when the SDK could not parse a successful (2xx) response: Google.Apis 1.78.0 then throws a
    /// GoogleApiException with status 0, this message prefix and the parser's exception inside. A request
    /// rejected before sending also has status 0, but neither the prefix nor an inner exception.
    /// </summary>
    private static bool IsInvalidJsonResponse(Google.GoogleApiException e) =>
        e.InnerException != null && e.Message.StartsWith("Failed to parse response from server as", StringComparison.Ordinal);

    /// <summary>
    /// Translates a GoogleApiException into an exception with an actionable message for OutSystems
    /// logs, instead of Google's raw API error. The original exception is kept as InnerException.
    /// </summary>
    private static Exception FriendlyException(Google.GoogleApiException e, Authentication authentication, string? bucketName, string? objectName)
    {
        string details = ErrorDetails(e);

        if ((int)e.HttpStatusCode == 0)
        {
            if (IsInvalidJsonResponse(e))
                return new Exception($"Google Cloud Storage answered with a response that is not valid JSON, which usually means a proxy, firewall or sign-in page answered instead of Google. Check the outbound HTTPS access to *.googleapis.com. Details: {Clean(details)}", e);
            return new Exception($"The request was rejected before it was sent to Google Cloud Storage because an input is invalid: {Clean(details)}", e);
        }
        if (e.HttpStatusCode == HttpStatusCode.NotFound && bucketName != null)
        {
            if (objectName != null && !IsBucketNotFound(e))
                return new Exception($"Object '{Clean(objectName)}' was not found in bucket '{Clean(bucketName)}'. Details: {Clean(details)}", e);
            return new Exception($"Bucket '{Clean(bucketName)}' does not exist (names are case-sensitive and must match exactly). Details: {Clean(details)}", e);
        }
        if (e.HttpStatusCode == HttpStatusCode.Forbidden)
            return new Exception($"Access denied for service account '{Clean(IdentityOf(authentication))}'. Grant it the required IAM role in Google Cloud (Storage Object Admin for object operations, Storage Admin for bucket operations). Details: {Clean(details)}", e);
        if (e.HttpStatusCode == HttpStatusCode.Unauthorized)
            return new Exception($"Google rejected the request as unauthenticated. {UnauthenticatedHint(authentication)} Details: {Clean(details)}", e);
        if (e.HttpStatusCode == HttpStatusCode.BadRequest && IsChecksumMismatch(details))
            return new Exception($"Upload rejected by Google Cloud Storage: the data it received did not match the CRC32C checksum computed by the connector, so nothing was stored and any existing object was left unchanged. This is usually a transient network error - retry the upload. Details: {Clean(details)}", e);
        if (e.HttpStatusCode == HttpStatusCode.Conflict)
        {
            if (details.IndexOf("not empty", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Exception($"Bucket '{Clean(bucketName)}' is not empty. Delete all objects in it before deleting the bucket. Details: {Clean(details)}", e);
            return new Exception($"Conflict: {Clean(details)} (for Bucket_Create this usually means the name is already taken - bucket names are global across all of Google Cloud Storage).", e);
        }
        return new Exception($"Google Cloud Storage error ({(int)e.HttpStatusCode} {e.HttpStatusCode}): {Clean(details)}", e);
    }
}
