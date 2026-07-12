using OutSystems.ExternalLibraries.SDK;

namespace OutSystems.ExternalLibraries.GoogleCloudStorage_Connector.Structures;

[OSStructure(Description = "A folder-style entry returned by Object_List when a Delimiter is set: a common prefix shared by the objects grouped under it.")]
public struct Prefix
{
    public Prefix()
    {
        Value = string.Empty;
    }

    [OSStructureField(Description = "The common prefix (folder path), e.g. 'images/thumbnails/'.", IsMandatory = true)]
    public string Value { get; set; }
}
