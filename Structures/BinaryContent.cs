using OutSystems.ExternalLibraries.SDK;

namespace OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;

[OSStructure(Description = "Content held in memory, with its MIME type. Used by Object_UploadBinary and Object_DownloadBinary (at most 5.5 MB).")]
public struct BinaryContent
{
    public BinaryContent()
    {
        Content = Array.Empty<byte>();
        ContentType = string.Empty;
    }

    [OSStructureField(Description = "The binary content.", IsMandatory = true)]
    public byte[] Content { get; set; }

    [OSStructureField(Description = "The MIME type of the content (e.g., image/png).")]
    public string ContentType { get; set; }
}
