namespace UE5LibraryBrowser;

internal sealed class UeLibraryAnimation
{
    public string Name { get; init; } = "";
    public string Output { get; init; } = "";
    public string Source { get; init; } = "";
    public string Status { get; init; } = "";
    public string RelationSource { get; init; } = "";
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

    public bool IsPreviewable =>
        IsUsableCandidate
        && Output.EndsWith(".ueanim", StringComparison.OrdinalIgnoreCase)
        && File.Exists(Output)
        && !IsContainerAnimation
        && !string.Equals(ValidationStatus, "error", StringComparison.OrdinalIgnoreCase);
}
