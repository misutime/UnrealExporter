namespace UE5LibraryBrowser;

internal sealed class UeLibraryAnimation
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
        && Output.EndsWith(".ueanim", StringComparison.OrdinalIgnoreCase)
        && File.Exists(Output)
        && !IsContainerAnimation
        && !string.Equals(ValidationStatus, "error", StringComparison.OrdinalIgnoreCase);
}
