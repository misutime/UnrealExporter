namespace UE5LibraryBrowser;

internal sealed class UeLibraryAsset
{
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string Output { get; init; } = "";
    public string Source { get; init; } = "";
    public string SourceType { get; init; } = "";
    public string ResourceKind { get; init; } = "";
    public string Format { get; init; } = "";
    public string ValidationStatus { get; init; } = "";
    public string SharedTexture { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public long SizeBytes { get; init; }
    public bool HardLinked { get; init; }
    public string LinkError { get; init; } = "";
    public int TextureSlotCount { get; init; }
    public int ColorCount { get; init; }
    public int ScalarCount { get; init; }
    public int SwitchCount { get; init; }
    public string BlendMode { get; init; } = "";
    public string ShadingModel { get; init; } = "";
}
