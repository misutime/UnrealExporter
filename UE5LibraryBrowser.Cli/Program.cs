using AssetLibrary.Core;
using System.Text.Json;
using UE5LibraryBrowser;

namespace UE5LibraryBrowser.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0].Equals("build-thumbnails", StringComparison.OrdinalIgnoreCase))
            {
                var concurrency = args.Length >= 3 && int.TryParse(args[2], out var parsedConcurrency)
                    ? parsedConcurrency
                    : 4;
                var limit = args.Length >= 4 && int.TryParse(args[3], out var parsedLimit)
                    ? parsedLimit
                    : 0;
                await BuildThumbnailsAsync(args[1], concurrency, limit).ConfigureAwait(false);
                return 0;
            }

            if (args.Length == 2 && args[0].Equals("validate-library", StringComparison.OrdinalIgnoreCase))
            {
                ValidateLibrary(args[1]);
                return 0;
            }

            PrintUsage();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static async Task BuildThumbnailsAsync(string root, int concurrency, int limit)
    {
        var index = AssetLibraryIndexReader.Load(root);
        concurrency = Math.Clamp(concurrency, 1, 24);
        var viewerSafeCache = new ViewerSafeGltfCache(index.Root);
        using var thumbnails = new ThumbnailService(index.Root, viewerSafeCache, concurrency);

        var models = (limit > 0 ? index.Models.Take(limit) : index.Models).ToArray();
        var nextIndex = -1;
        var completed = 0;
        var cached = 0;
        var failed = 0;
        var startedAt = DateTime.UtcNow;

        Console.WriteLine($"Building asset thumbnails: root={index.Root}");
        Console.WriteLine($"Models={models.Length}, concurrency={concurrency}, renderer={thumbnails.RendererLabel}");

        var workers = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(async () =>
            {
                while (true)
                {
                    var modelIndex = Interlocked.Increment(ref nextIndex);
                    if (modelIndex >= models.Length)
                        break;

                    var model = models[modelIndex];
                    try
                    {
                        var result = await thumbnails.GetThumbnailAsync(model, CancellationToken.None).ConfigureAwait(false);
                        if (result.FromCache)
                            Interlocked.Increment(ref cached);
                        if (!result.Success)
                            Interlocked.Increment(ref failed);
                        result.Image.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        Console.Error.WriteLine($"thumbnail failed: {model.Output} ({ex.Message})");
                    }

                    var done = Interlocked.Increment(ref completed);
                    if (done % 100 == 0 || done == models.Length)
                    {
                        var elapsed = DateTime.UtcNow - startedAt;
                        Console.WriteLine($"thumbnail progress {done}/{models.Length}, cached={Volatile.Read(ref cached)}, failed={Volatile.Read(ref failed)}, elapsed={elapsed:hh\\:mm\\:ss}");
                    }
                }
            }))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        var payload = new
        {
            root = index.Root,
            total = models.Length,
            cached,
            failed,
            elapsedSeconds = (DateTime.UtcNow - startedAt).TotalSeconds
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ValidateLibrary(string root)
    {
        var index = AssetLibraryIndexReader.Load(root);
        var payload = new
        {
            root = index.Root,
            library = index.Manifest.LibraryName,
            sourceTool = index.Manifest.SourceTool,
            capabilities = index.Manifest.Capabilities,
            models = index.Models.Count,
            modelsWithAnimations = index.Models.Count(x => x.AnimationCount > 0),
            animations = index.Models.Sum(x => x.AnimationCount),
            animationUsages = index.AnimationUsages.Count,
            animationGroups = index.AnimationGroups.Count,
            textures = index.Textures.Count,
            materials = index.Materials.Count,
            usableAnimations = index.AnimationsByModel.Values.SelectMany(x => x).Count(x => x.IsUsableCandidate),
            source = "library_index.db"
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void PrintUsage()
    {
        Console.WriteLine("AssetLibraryBrowser.Cli");
        Console.WriteLine("Usage:");
        Console.WriteLine("  build-thumbnails <libraryRoot> [concurrency=4] [limit=0]");
        Console.WriteLine("  validate-library <libraryRoot>");
    }
}
