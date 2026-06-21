namespace AssetLibrary.Core;

public sealed class AssetLibraryIndex
{
    public string Root { get; init; } = "";
    public AssetLibraryManifest Manifest { get; init; } = new();
    public AssetLibraryCapabilities Capabilities => Manifest.Capabilities;
    public List<AssetLibraryModel> Models { get; init; } = [];
    public Dictionary<string, List<AssetLibraryAnimation>> AnimationsByModel { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AssetLibraryAnimationUsage> AnimationUsages { get; init; } = [];
    public List<AssetLibraryAnimationGroup> AnimationGroups { get; init; } = [];
    public List<AssetLibraryAsset> Textures { get; init; } = [];
    public List<AssetLibraryAsset> Materials { get; init; } = [];
}

public sealed class AssetLibraryModel
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

public sealed class AssetLibraryAnimation
{
    public string Name { get; init; } = "";
    public string Output { get; init; } = "";
    public string Source { get; init; } = "";
    public string Status { get; init; } = "";
    public string RelationSource { get; init; } = "";
    public string UsageEvidence { get; init; } = "";
    public string ConfidenceTier { get; init; } = "";
    public string RelationshipKind { get; init; } = "";
    public string RecommendedUse { get; init; } = "";
    public string EvidenceSummary { get; init; } = "";
    public bool IsExplicitUsage { get; init; }
    public bool IsSkeletonCompatible { get; init; }
    public bool IsDeterministicUsage { get; init; }
    public bool IsCompatibilityCandidate { get; init; }
    public string ValidationStatus { get; init; } = "";
    public string ValidationCategory { get; init; } = "";
    public string ValidationReason { get; init; } = "";
    public double Duration { get; init; }
    public int FrameCount { get; init; }
    public int TrackCount { get; init; }
    public double TrackCoverage { get; init; }
    public bool HierarchyCompatible { get; init; }
    public bool IsContainerAnimation { get; init; }
    public bool IsUsableCandidate { get; init; }

    public bool IsDefaultTrusted =>
        string.Equals(RecommendedUse, "defaultTrusted", StringComparison.OrdinalIgnoreCase);

    public bool IsPreviewable =>
        IsUsableCandidate
        && File.Exists(Output)
        && !IsContainerAnimation
        && !string.Equals(ValidationStatus, "error", StringComparison.OrdinalIgnoreCase);
}

public sealed class AssetLibraryAsset
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

public sealed class AssetLibraryAnimationUsage
{
    public AssetLibraryModel Model { get; init; } = new();
    public AssetLibraryAnimation Animation { get; init; } = new();
}

public sealed class AssetLibraryAnimationGroup
{
    public string Key { get; init; } = "";
    public AssetLibraryAnimation Representative { get; init; } = new();
    public List<AssetLibraryAnimationUsage> Usages { get; init; } = [];

    public string Name => Representative.Name;
    public string Output => Representative.Output;
    public string Source => Representative.Source;
    public int ModelCount => Usages.Select(x => x.Model.Output).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public int DefaultTrustedCount => Usages.Count(x => x.Animation.IsDefaultTrusted);
    public int CompatibleCount => Usages.Count(x => string.Equals(x.Animation.RecommendedUse, "compatibleCandidate", StringComparison.OrdinalIgnoreCase));
    public int PreviewableCount => Usages.Count(x => x.Animation.IsPreviewable);
    public int ReviewCount => Usages.Count(x =>
        string.Equals(x.Animation.RecommendedUse, "manualReview", StringComparison.OrdinalIgnoreCase)
        || string.Equals(x.Animation.RecommendedUse, "compatibleNeedsReview", StringComparison.OrdinalIgnoreCase)
        || string.Equals(x.Animation.RecommendedUse, "notUsable", StringComparison.OrdinalIgnoreCase));
}
