using System.Text.Json;

namespace UE5LibraryBrowser;

internal sealed class RecentLibraryStore
{
    private const int MaxRecentCount = 12;
    private readonly string _settingsPath;

    public RecentLibraryStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settingsDir = Path.Combine(appData, "UnrealExporter", "UE5LibraryBrowser");
        Directory.CreateDirectory(settingsDir);
        _settingsPath = Path.Combine(settingsDir, "recent_libraries.json");
    }

    public IReadOnlyList<string> Load()
    {
        if (!File.Exists(_settingsPath))
            return [];

        try
        {
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_settingsPath)) ?? [];
            return paths
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(NormalizePath)
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxRecentCount)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return;

        var normalized = NormalizePath(path);
        var paths = Load()
            .Where(x => !string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase))
            .Prepend(normalized)
            .Take(MaxRecentCount)
            .ToList();

        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(paths, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
