namespace UE5LibraryBrowser;

internal sealed class UeLibraryIndex
{
    public string Root { get; init; } = "";
    public List<UeLibraryModel> Models { get; init; } = [];
    public Dictionary<string, List<UeLibraryAnimation>> AnimationsByModel { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
