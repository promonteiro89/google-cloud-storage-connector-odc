using OutSystems.ExternalLibraries.SDK;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;

namespace OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;

[OSInterface(Description = "Google Cloud Storage connector for ODC.", Name = "GoogleCloudStorage_Connector", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.app_icon.png")]
public interface IGoogleCloudStorage
{
    [OSAction(Description = "Uploads an object to a bucket, optionally with custom key-value metadata.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Object_Upload(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName,
        [OSParameter(Description = "Object Name")] string objectName,
        [OSParameter(Description = "File to upload")] OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.File file,
        [OSParameter(Description = "Optional custom key-value metadata to store with the object (e.g. user id, tenant, document type). Retrievable via Object_GetMetadata. Leave empty for none.")] IEnumerable<MetadataEntry> metadata);

    [OSAction(Description = "Downloads an object from a bucket.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Object_Download(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName,
        [OSParameter(Description = "Object Name")] string objectName,
        [OSParameter(Description = "The downloaded file")] out OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.File file);

    [OSAction(Description = "Lists objects in a bucket, optionally filtered by prefix, with support for pagination (MaxResults/PageToken) and folder-style navigation (Delimiter).", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Object_List(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName,
        [OSParameter(Description = "Prefix filter for hierarchical navigation")] string prefix,
        [OSParameter(Description = "Maximum number of objects to return in this call. 0 (default) returns all objects. When greater than 0, use NextPageToken to fetch the following page.")] int maxResults,
        [OSParameter(Description = "The NextPageToken returned by a previous call. Leave empty to start from the first page.")] string pageToken,
        [OSParameter(Description = "Set to '/' for folder-style navigation: objects in nested subfolders are grouped into PrefixList instead of being returned individually. Leave empty to list all objects recursively.")] string delimiter,
        [OSParameter(Description = "List of GCS objects")] out IEnumerable<OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Object> objects,
        [OSParameter(Description = "Non-empty when more results exist (paged mode only) - pass it as PageToken in the next call. Empty when the listing is complete.")] out string nextPageToken,
        [OSParameter(Description = "The 'folders' found directly under Prefix when Delimiter is set.")] out IEnumerable<Prefix> prefixList);

    [OSAction(Description = "Checks whether an object exists in a bucket.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Object_Exists(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName,
        [OSParameter(Description = "Object Name")] string objectName,
        [OSParameter(Description = "True if the object exists")] out bool exists);

    [OSAction(Description = "Retrieves an object's metadata (size, content type, hashes, generation, storage class, timestamps, and custom key-value metadata) without downloading its content.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Object_GetMetadata(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName,
        [OSParameter(Description = "Object Name")] string objectName,
        [OSParameter(Description = "True if the object was found, False otherwise")] out bool exists,
        [OSParameter(Description = "The object's metadata (only populated when Exists is True)")] out OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.ObjectMetadata metadata,
        [OSParameter(Description = "The object's custom key-value metadata. Empty when the object has none or does not exist.")] out IEnumerable<MetadataEntry> customMetadata);

    [OSAction(Description = "Deletes an object from a bucket.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Object_Delete(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName,
        [OSParameter(Description = "Object Name")] string objectName);

    [OSAction(Description = "Copies an object to another location, within the same bucket or across buckets, without downloading its content. If the destination object exists, it will be overwritten.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Object_Copy(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "The bucket that currently contains the object")] string sourceBucketName,
        [OSParameter(Description = "The full path/name of the source object")] string sourceObjectName,
        [OSParameter(Description = "The bucket to copy the object into (can be the same as the source)")] string destinationBucketName,
        [OSParameter(Description = "The full path/name for the destination object")] string destinationObjectName);

    [OSAction(Description = "Moves an object to another location (copy + delete of the source), within the same bucket or across buckets. Use the same source and destination bucket to rename an object. If the destination exists, it will be overwritten.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Object_Move(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "The bucket that currently contains the object")] string sourceBucketName,
        [OSParameter(Description = "The full path/name of the source object")] string sourceObjectName,
        [OSParameter(Description = "The bucket to move the object into (can be the same as the source)")] string destinationBucketName,
        [OSParameter(Description = "The full path/name for the destination object")] string destinationObjectName);

    [OSAction(Description = "Updates an object's metadata without re-uploading its content. Only the provided fields change: empty text inputs leave the corresponding field untouched, and an empty Metadata list leaves custom metadata untouched. Within Metadata, an entry with an empty Value removes that key.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Object_UpdateMetadata(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName,
        [OSParameter(Description = "The full path/name of the object to update")] string objectName,
        [OSParameter(Description = "New MIME type. Empty = unchanged.")] string contentType,
        [OSParameter(Description = "New content encoding (e.g. 'gzip'). Empty = unchanged.")] string contentEncoding,
        [OSParameter(Description = "New content disposition (e.g. 'attachment; filename=\"report.pdf\"'). Empty = unchanged.")] string contentDisposition,
        [OSParameter(Description = "New cache control (e.g. 'public, max-age=3600'). Empty = unchanged.")] string cacheControl,
        [OSParameter(Description = "Custom metadata changes. Empty list = unchanged. An entry with an empty Value removes that key; others are set/overwritten.")] IEnumerable<MetadataEntry> metadata);

    [OSAction(Description = "Deletes all objects whose names start with the given prefix (a 'folder' and everything under it). The Prefix is mandatory and cannot be empty, as a safety measure against accidentally wiping an entire bucket. Returns the number of objects deleted.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Object_DeleteByPrefix(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName,
        [OSParameter(Description = "All objects whose names start with this prefix are deleted (e.g. 'uploads/2025/'). Cannot be empty.")] string prefix,
        [OSParameter(Description = "Number of objects that were deleted")] out long deletedCount);

    [OSAction(Description = "Generates a temporary signed URL for an object. The Operation controls whether the URL allows download (GET), upload (PUT), or delete (DELETE).", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Object_GetSignedUrl(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName,
        [OSParameter(Description = "Object Name")] string objectName,
        [OSParameter(Description = "Expiration time in minutes (1 to 10080; a Google Cloud V4 signed URL is valid for at most 7 days).")] int expirationMinutes,
        [OSParameter(Description = "The temporary secure URL")] out string url,
        [OSParameter(Description = "The operation the URL will permit: 'Download' (GET), 'Upload' (PUT), or 'Delete' (DELETE). Defaults to 'Download'.")] string operation = "Download",
        [OSParameter(Description = "Optional, for Upload URLs: the exact Content-Type the client will send in the PUT request. It becomes part of the signature, so Google rejects uploads with a different Content-Type. Leave empty to allow any.")] string contentType = "");

    [OSAction(Description = "Lists all buckets in the specified project.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Bucket_List(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "List of GCS buckets")] out IEnumerable<Bucket> buckets);

    [OSAction(Description = "Creates a new bucket in the specified project.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Bucket_Create(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName,
        [OSParameter(Description = "Geographic location (e.g., US, EU)")] string location);

    [OSAction(Description = "Deletes a bucket. The bucket must be empty.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Bucket_Delete(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName);

    [OSAction(Description = "Checks whether a bucket exists and is accessible to the service account, without listing its contents.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Bucket_Exists(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName,
        [OSParameter(Description = "True if the bucket exists and the service account can access it")] out bool exists);
}
