using Google.Apis.Auth.OAuth2;
using Google.Apis.Storage.v1;
using Google.Cloud.Storage.V1;
using OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;
using System.Net;
using File = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.File;
using Object = OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures.Object;

namespace OutSystems.ExternalLibraries.GoogleCloudStorage_Connector;

public class GoogleCloudStorage : IGoogleCloudStorage
{
    private ServiceAccountCredential GetServiceAccountCredential(Authentication authentication)
    {
        var initializer = new ServiceAccountCredential.Initializer(authentication.ClientEmail)
        {
            Scopes = new[] { StorageService.Scope.CloudPlatform }
        }.FromPrivateKey(authentication.PrivateKey.Replace("\\n", "\n"));

        return new ServiceAccountCredential(initializer);
    }

    private StorageClient GetStorageClient(Authentication authentication)
    {
        var credential = GetServiceAccountCredential(authentication);
        return StorageClient.Create(credential.ToGoogleCredential());
    }

    public void Object_Upload(Authentication authentication, string bucketName, string objectName, File file)
    {
        var storageClient = GetStorageClient(authentication);
        using var stream = new MemoryStream(file.Content);
        storageClient.UploadObject(bucketName, objectName, file.ContentType, stream);
    }

    public void Object_Download(Authentication authentication, string bucketName, string objectName, out File file)
    {
        var storageClient = GetStorageClient(authentication);
        using var stream = new MemoryStream();
        var obj = storageClient.DownloadObject(bucketName, objectName, stream);
        
        file = new File
        {
            Content = stream.ToArray(),
            ContentType = obj.ContentType
        };
    }

    public void Object_List(Authentication authentication, string bucketName, string prefix, out IEnumerable<Object> objects)
    {
        var storageClient = GetStorageClient(authentication);
        var gcsObjects = storageClient.ListObjects(bucketName, prefix);

        objects = gcsObjects.Select(obj => new Object
        {
            Name = obj.Name,
            Size = (long)(obj.Size ?? 0),
            ContentType = obj.ContentType,
            Updated = obj.UpdatedDateTimeOffset?.UtcDateTime ?? new DateTime(1900, 1, 1)
        }).ToList();
    }

    public void Object_Exists(Authentication authentication, string bucketName, string objectName, out bool exists)
    {
        var storageClient = GetStorageClient(authentication);
        try
        {
            storageClient.GetObject(bucketName, objectName);
            exists = true;
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            exists = false;
        }
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
                Size = (long)(obj.Size ?? 0),
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
                TimeCreated = obj.TimeCreatedDateTimeOffset?.UtcDateTime ?? new DateTime(1900, 1, 1),
                Updated = obj.UpdatedDateTimeOffset?.UtcDateTime ?? new DateTime(1900, 1, 1)
            };
        }
        catch (Google.GoogleApiException e) when (e.HttpStatusCode == HttpStatusCode.NotFound)
        {
            exists = false;
        }
    }

    public void Object_Delete(Authentication authentication, string bucketName, string objectName)
    {
        var storageClient = GetStorageClient(authentication);
        storageClient.DeleteObject(bucketName, objectName);
    }

    public void Object_GetSignedUrl(Authentication authentication, string bucketName, string objectName, int expirationMinutes, out string url, string operation = "Download")
    {
        var credential = GetServiceAccountCredential(authentication);
        var urlSigner = UrlSigner.FromCredential(credential);

        var method = (operation ?? "").Trim().ToUpperInvariant() switch
        {
            "DOWNLOAD" => HttpMethod.Get,
            "UPLOAD" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            _ => throw new ArgumentException($"Invalid Operation '{operation}'. Use 'Download', 'Upload', or 'Delete'.")
        };

        url = urlSigner.Sign(
            bucketName,
            objectName,
            TimeSpan.FromMinutes(expirationMinutes),
            method
        );
    }

    public void Bucket_List(Authentication authentication, out IEnumerable<Bucket> buckets)
    {
        var storageClient = GetStorageClient(authentication);
        var gcsBuckets = storageClient.ListBuckets(authentication.ProjectId);

        buckets = gcsBuckets.Select(b => new Bucket
        {
            Name = b.Name,
            Location = b.Location,
            StorageClass = b.StorageClass,
            Created = b.TimeCreatedDateTimeOffset?.UtcDateTime ?? new DateTime(1900, 1, 1)
        }).ToList();
    }

    public void Bucket_Create(Authentication authentication, string bucketName, string location)
    {
        var storageClient = GetStorageClient(authentication);
        storageClient.CreateBucket(
            authentication.ProjectId,
            new Google.Apis.Storage.v1.Data.Bucket
            {
                Name = bucketName,
                Location = location
            }
        );
    }

    public void Bucket_Delete(Authentication authentication, string bucketName)
    {
        var storageClient = GetStorageClient(authentication);
        storageClient.DeleteBucket(bucketName);
    }
}
