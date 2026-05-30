using OutSystems.ExternalLibraries.SDK;

namespace OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;

[OSStructure(Description = "Represents the complete metadata of a file stored in Google Cloud Storage, retrieved without downloading its content.")]
public struct ObjectMetadata
{
    public ObjectMetadata()
    {
        Name = string.Empty;
        Bucket = string.Empty;
        ContentType = string.Empty;
        ContentEncoding = string.Empty;
        ContentDisposition = string.Empty;
        CacheControl = string.Empty;
        MD5Hash = string.Empty;
        Crc32c = string.Empty;
        ETag = string.Empty;
        StorageClass = string.Empty;
        MediaLink = string.Empty;
        TimeCreated = new DateTime(1900, 1, 1);
        Updated = new DateTime(1900, 1, 1);
    }

    [OSStructureField(Description = "The full path and filename of the object within the bucket.", IsMandatory = true)]
    public string Name { get; set; }

    [OSStructureField(Description = "The name of the bucket that contains this object.")]
    public string Bucket { get; set; }

    [OSStructureField(Description = "The size of the object's content in bytes.")]
    public long Size { get; set; }

    [OSStructureField(Description = "The MIME type of the object (e.g., image/png).")]
    public string ContentType { get; set; }

    [OSStructureField(Description = "The Content-Encoding of the object (e.g., gzip). Usually empty.")]
    public string ContentEncoding { get; set; }

    [OSStructureField(Description = "The Content-Disposition header used when serving the object.")]
    public string ContentDisposition { get; set; }

    [OSStructureField(Description = "The Cache-Control directive applied when the object is served.")]
    public string CacheControl { get; set; }

    [OSStructureField(Description = "The Base64-encoded MD5 hash of the object's content. Not available for composite objects.")]
    public string MD5Hash { get; set; }

    [OSStructureField(Description = "The Base64-encoded CRC32c checksum of the object's content.")]
    public string Crc32c { get; set; }

    [OSStructureField(Description = "The HTTP entity tag (ETag) of the object.")]
    public string ETag { get; set; }

    [OSStructureField(Description = "The generation number that uniquely identifies this version of the content.")]
    public long Generation { get; set; }

    [OSStructureField(Description = "The metageneration number, incremented each time the object's metadata changes.")]
    public long Metageneration { get; set; }

    [OSStructureField(Description = "The storage class of the object (e.g., STANDARD, NEARLINE, COLDLINE, ARCHIVE).")]
    public string StorageClass { get; set; }

    [OSStructureField(Description = "The direct download URL for the object's content (still requires authentication).")]
    public string MediaLink { get; set; }

    [OSStructureField(Description = "The date and time (UTC) when the object was created.")]
    public DateTime TimeCreated { get; set; }

    [OSStructureField(Description = "The date and time (UTC) when the object's content or metadata was last updated.")]
    public DateTime Updated { get; set; }
}
