using OutSystems.ExternalLibraries.SDK;

namespace OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;

[OSStructure(Description = "A single custom metadata key-value pair stored with an object.")]
public struct MetadataEntry
{
    public MetadataEntry()
    {
        Key = string.Empty;
        Value = string.Empty;
    }

    [OSStructureField(Description = "The metadata key (e.g. 'tenant', 'documentType').", IsMandatory = true)]
    public string Key { get; set; }

    [OSStructureField(Description = "The metadata value. In Object_UpdateMetadata, an entry with an empty Value removes that key.")]
    public string Value { get; set; }
}
