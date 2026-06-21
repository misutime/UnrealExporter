using System.Text.Json;

namespace AssetLibrary.Core;

public sealed class AssetLibraryManifest
{
    public int SchemaVersion { get; init; } = 1;
    public string LibraryKind { get; init; } = "AssetLibrary";
    public string LibraryName { get; init; } = "";
    public string SourceTool { get; init; } = "";
    public string SourceGame { get; init; } = "";
    public string CreatedUtc { get; init; } = "";
    public string Index { get; init; } = "library_index.db";
    public AssetLibraryCapabilities Capabilities { get; init; } = new();

    public static AssetLibraryManifest LoadOrDefault(string root, bool hasAnimationTables)
    {
        var path = Path.Combine(root, AssetLibrarySchema.ManifestFileName);
        if (File.Exists(path))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<AssetLibraryManifest>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (manifest != null)
                    return manifest;
            }
            catch (JsonException)
            {
                // Fall through to a synthesized manifest; validation reports the missing/invalid file separately.
            }
        }

        return new AssetLibraryManifest
        {
            LibraryName = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            SourceTool = "Unknown",
            SourceGame = "",
            CreatedUtc = "",
            Capabilities = new AssetLibraryCapabilities
            {
                Models = true,
                Animations = hasAnimationTables,
                AnimationPreviewComposer = hasAnimationTables ? "unreal-ueanim" : null
            }
        };
    }
}

public sealed class AssetLibraryCapabilities
{
    public bool Models { get; init; } = true;
    public bool Animations { get; init; }
    public string? AnimationPreviewComposer { get; init; }

    public bool CanComposeAnimationPreview =>
        Animations && !string.IsNullOrWhiteSpace(AnimationPreviewComposer);
}
