using OutSystems.ExternalLibraries.SDK;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;

namespace OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;

[OSInterface(Description = "Google Cloud Storage connector for ODC.", Name = "GoogleCloudStorage_Connector", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.app_icon.png")]
public interface IGoogleCloudStorage
{
    [OSAction(Description = "Uploads an object to a bucket.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Object_Upload(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName,
        [OSParameter(Description = "Object Name")] string objectName,
        [OSParameter(Description = "File to upload")] OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.File file);

    [OSAction(Description = "Downloads an object from a bucket.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Object_Download(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName,
        [OSParameter(Description = "Object Name")] string objectName,
        [OSParameter(Description = "The downloaded file")] out OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.File file);

    [OSAction(Description = "Lists objects in a bucket with an optional prefix filter.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Object_List(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName,
        [OSParameter(Description = "Prefix filter")] string prefix,
        [OSParameter(Description = "List of GCS objects")] out IEnumerable<OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Object> objects);

    [OSAction(Description = "Checks whether an object exists in a bucket.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Object_Exists(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName,
        [OSParameter(Description = "Object Name")] string objectName,
        [OSParameter(Description = "True if the object exists")] out bool exists);

    [OSAction(Description = "Retrieves an object's metadata (size, content type, hashes, generation, storage class, timestamps) without downloading its content.", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Object_GetMetadata(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName,
        [OSParameter(Description = "Object Name")] string objectName,
        [OSParameter(Description = "True if the object was found, False otherwise")] out bool exists,
        [OSParameter(Description = "The object's metadata (only populated when Exists is True)")] out OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.ObjectMetadata metadata);

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

    [OSAction(Description = "Generates a temporary signed URL for an object. The Operation controls whether the URL allows download (GET), upload (PUT), or delete (DELETE).", ReturnDescription = "No return value", IconResourceName = "OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Resources.action_icon.png")]
    void Object_GetSignedUrl(
        [OSParameter(Description = "Authentication credentials")] Authentication authentication,
        [OSParameter(Description = "Bucket Name")] string bucketName,
        [OSParameter(Description = "Object Name")] string objectName,
        [OSParameter(Description = "Expiration time in minutes")] int expirationMinutes,
        [OSParameter(Description = "The temporary secure URL")] out string url,
        [OSParameter(Description = "The operation the URL will permit: 'Download' (GET), 'Upload' (PUT), or 'Delete' (DELETE). Defaults to 'Download'.")] string operation = "Download");

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
}
