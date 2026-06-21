namespace UE5LibraryBrowser;

internal sealed class UeLibraryAnimationUsage
{
    public UeLibraryModel Model { get; init; } = new();
    public UeLibraryAnimation Animation { get; init; } = new();
}

internal sealed class UeLibraryAnimationGroup
{
    public string Key { get; init; } = "";
    public UeLibraryAnimation Representative { get; init; } = new();
    public List<UeLibraryAnimationUsage> Usages { get; init; } = [];

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
