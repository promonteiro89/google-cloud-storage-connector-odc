using OutSystems.ExternalLibraries.SDK;

namespace OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;

[OSStructure(Description = "Represents a file (object) stored in a Google Cloud Storage bucket.")]
public struct Object
{
    public Object()
    {
        Name = string.Empty;
        ContentType = string.Empty;
        Updated = new DateTime(1900, 1, 1);
        MD5Hash = string.Empty;
        Crc32c = string.Empty;
    }

    [OSStructureField(Description = "The full path and filename of the object within the bucket.", IsMandatory = true)]
    public string Name { get; set; }

    [OSStructureField(Description = "The total size of the object in bytes.")]
    public long Size { get; set; }

    [OSStructureField(Description = "The MIME type of the object (e.g., image/png).")]
    public string ContentType { get; set; }

    [OSStructureField(Description = "The date and time (UTC) when the object was last modified.")]
    public DateTime Updated { get; set; }

    [OSStructureField(Description = "The object's generation (version number). Changes every time the content is replaced.")]
    public long Generation { get; set; }

    [OSStructureField(Description = "Base64 MD5 of the content, as Google reports it. Empty for composite objects.")]
    public string MD5Hash { get; set; }

    [OSStructureField(Description = "Base64 CRC32C checksum of the content, as Google reports it.")]
    public string Crc32c { get; set; }
}
