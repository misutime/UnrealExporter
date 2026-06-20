namespace UE5LibraryBrowser;

internal sealed class UeLibraryModel
{
    public string Name { get; init; } = "";
    public string Output { get; init; } = "";
    public string Source { get; init; } = "";
    public string SourceType { get; init; } = "";
    public string ResourceKind { get; init; } = "";
    public string SkeletonPath { get; init; } = "";
    public string SkeletonName { get; init; } = "";
    public string ValidationStatus { get; init; } = "";
    public string Confidence { get; init; } = "";
    public int AnimationCount { get; init; }
    public int UsableAnimationCount { get; init; }
    public int TrustedAnimationCount { get; init; }
    public int CompatibleAnimationCount { get; init; }
    public int ReviewAnimationCount { get; init; }
    public int BoneCount { get; init; }
    public int MaterialCount { get; init; }
    public bool HasSkin { get; init; }

    public string DisplayKind => string.IsNullOrWhiteSpace(ResourceKind) ? SourceType : ResourceKind;
}
