using AssetLibrary.Core;
using System.Text.Json;

namespace UE5LibraryBrowser;

internal sealed class AssetLibraryCurationStore
{
    private readonly string _root;
    private readonly string _path;
    private readonly HashSet<string> _ignoredModelKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _favoriteModelKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _favoriteAnimationKeys = new(StringComparer.OrdinalIgnoreCase);

    public AssetLibraryCurationStore(string root)
    {
        _root = Path.GetFullPath(root);
        var browserDir = Path.Combine(_root, ".ue5_browser_cache");
        Directory.CreateDirectory(browserDir);
        _path = Path.Combine(browserDir, "curation_marks.jsonl");
        Load();
    }

    public bool IsIgnored(AssetLibraryModel? model)
        => model != null && _ignoredModelKeys.Contains(ModelKey(model));

    public bool IsFavoriteModel(AssetLibraryModel? model)
        => model != null && _favoriteModelKeys.Contains(ModelKey(model));

    public bool IsFavoriteAnimation(AssetLibraryAnimation? animation)
        => animation != null && _favoriteAnimationKeys.Contains(AnimationKey(animation));

    public void SetIgnored(AssetLibraryModel? model, bool ignored)
    {
        if (model == null)
            return;

        SetMark(_ignoredModelKeys, ModelKey(model), ignored);
        Rewrite();
    }

    public void SetFavoriteModel(AssetLibraryModel? model, bool favorite)
    {
        if (model == null)
            return;

        SetMark(_favoriteModelKeys, ModelKey(model), favorite);
        Rewrite();
    }

    public void SetFavoriteAnimation(AssetLibraryAnimation? animation, bool favorite)
    {
        if (animation == null)
            return;

        SetMark(_favoriteAnimationKeys, AnimationKey(animation), favorite);
        Rewrite();
    }

    private void Load()
    {
        if (!File.Exists(_path))
            return;

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var obj = document.RootElement;
                var action = obj.TryGetProperty("action", out var actionProperty) ? actionProperty.GetString() : null;
                var key = obj.TryGetProperty("key", out var keyProperty) ? keyProperty.GetString() : null;
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                switch (action?.ToLowerInvariant())
                {
                    case "ignore_model":
                        _ignoredModelKeys.Add(key);
                        break;
                    case "favorite_model":
                        _favoriteModelKeys.Add(key);
                        break;
                    case "favorite_animation":
                        _favoriteAnimationKeys.Add(key);
                        break;
                }
            }
            catch
            {
                // Broken curation rows should not prevent opening the asset library.
            }
        }
    }

    private void Rewrite()
    {
        using var writer = new StreamWriter(_path, false);
        foreach (var key in _ignoredModelKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            WriteRow(writer, "ignore_model", key);
        foreach (var key in _favoriteModelKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            WriteRow(writer, "favorite_model", key);
        foreach (var key in _favoriteAnimationKeys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            WriteRow(writer, "favorite_animation", key);
    }

    private static void SetMark(HashSet<string> set, string key, bool marked)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (marked)
            set.Add(key);
        else
            set.Remove(key);
    }

    private static void WriteRow(StreamWriter writer, string action, string key)
    {
        writer.WriteLine(JsonSerializer.Serialize(new
        {
            action,
            key,
            markedAtUtc = DateTimeOffset.UtcNow.ToString("O")
        }));
    }

    private string ModelKey(AssetLibraryModel model)
        => AssetLibraryIndexReader.MakeLibraryRelative(_root, model.Output);

    private string AnimationKey(AssetLibraryAnimation animation)
        => AssetLibraryIndexReader.MakeLibraryRelative(_root, animation.Output);
}
