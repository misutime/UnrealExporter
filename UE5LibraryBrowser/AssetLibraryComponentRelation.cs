namespace UE5LibraryBrowser;

internal sealed class AssetLibraryComponentSummary
{
    public string SourcePath { get; init; } = "";
    public int RelationCount { get; init; }
    public int OwnerCount { get; init; }
    public int ComponentCount { get; init; }
    public int ModelReferenceCount { get; init; }
    public int MaterialReferenceCount { get; init; }
    public int TextureReferenceCount { get; init; }
    public int AnimationReferenceCount { get; init; }
    public int MissingReferenceCount { get; init; }

    public string Name => string.IsNullOrWhiteSpace(SourcePath) ? "(unknown source)" : Path.GetFileNameWithoutExtension(SourcePath);
}

internal sealed class AssetLibraryComponentRelation
{
    public string OwnerObjectPath { get; init; } = "";
    public string OwnerType { get; init; } = "";
    public string ComponentObjectPath { get; init; } = "";
    public string ComponentType { get; init; } = "";
    public string ComponentName { get; init; } = "";
    public string RelationSource { get; init; } = "";
    public string RelationType { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public string TargetName { get; init; } = "";
    public string TargetAssetKind { get; init; } = "";
    public string TargetAssetOutput { get; init; } = "";
    public string MatchStatus { get; init; } = "";
    public string MatchReason { get; init; } = "";
    public string SocketName { get; init; } = "";
}
