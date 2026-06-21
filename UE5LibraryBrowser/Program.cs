using System.Text.Json;

namespace UE5LibraryBrowser;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 3 && args[0].Equals("--render-thumbnail", StringComparison.OrdinalIgnoreCase))
        {
            using var renderer = new PersistentGltfThumbnailRenderer();
            renderer.RenderToFile(args[1], args[2]);
            return;
        }

        if (args.Length == 1 && args[0].Equals("--thumbnail-worker", StringComparison.OrdinalIgnoreCase))
        {
            RunThumbnailWorker();
            return;
        }

        if (args.Length == 2 && args[0].Equals("--validate-library", StringComparison.OrdinalIgnoreCase))
        {
            ValidateLibrary(args[1]);
            return;
        }

        if (args.Length is 2 or 3 or 4 && args[0].Equals("--build-thumbnails", StringComparison.OrdinalIgnoreCase))
        {
            var concurrency = args.Length >= 3 && int.TryParse(args[2], out var parsedConcurrency)
                ? parsedConcurrency
                : 4;
            var limit = args.Length == 4 && int.TryParse(args[3], out var parsedLimit)
                ? parsedLimit
                : 0;
            BuildThumbnailsAsync(args[1], concurrency, limit).GetAwaiter().GetResult();
            return;
        }

        if (args.Length == 2 && args[0].Equals("--validate-components", StringComparison.OrdinalIgnoreCase))
        {
            ValidateComponents(args[1]);
            return;
        }

        if (args.Length == 2 && args[0].Equals("--smoke-preview", StringComparison.OrdinalIgnoreCase))
        {
            SmokePreviewAsync(args[1]).GetAwaiter().GetResult();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(args.FirstOrDefault()));
    }

    private static void RunThumbnailWorker()
    {
        using var renderer = new PersistentGltfThumbnailRenderer();
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            ThumbnailWorkerRequest? request = null;
            try
            {
                request = JsonSerializer.Deserialize<ThumbnailWorkerRequest>(line);
                if (request == null || string.IsNullOrWhiteSpace(request.GltfPath) || string.IsNullOrWhiteSpace(request.OutputPath))
                {
                    WriteWorkerResponse(new ThumbnailWorkerResponse { Id = request?.Id ?? "", Success = false, Error = "empty request" });
                    continue;
                }

                renderer.RenderToFile(request.GltfPath, request.OutputPath);
                WriteWorkerResponse(new ThumbnailWorkerResponse { Id = request.Id, Success = File.Exists(request.OutputPath), Error = "" });
            }
            catch (Exception ex)
            {
                WriteWorkerResponse(new ThumbnailWorkerResponse { Id = request?.Id ?? "", Success = false, Error = ex.Message });
            }
        }
    }

    private static void WriteWorkerResponse(ThumbnailWorkerResponse response)
    {
        Console.WriteLine(JsonSerializer.Serialize(response));
        Console.Out.Flush();
    }

    private static void ValidateLibrary(string root)
    {
        var index = UeLibraryIndexReader.Load(root);
        var payload = new
        {
            root = index.Root,
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

    private static async Task BuildThumbnailsAsync(string root, int concurrency, int limit)
    {
        var index = UeLibraryIndexReader.Load(root);
        concurrency = Math.Clamp(concurrency, 1, 24);
        var viewerSafeCache = new ViewerSafeGltfCache(index.Root);
        using var thumbnails = new ThumbnailService(index.Root, viewerSafeCache, concurrency);

        var models = (limit > 0 ? index.Models.Take(limit) : index.Models).ToArray();
        var nextIndex = -1;
        var completed = 0;
        var cached = 0;
        var failed = 0;

        Console.WriteLine($"Building UE thumbnails: root={index.Root}");
        Console.WriteLine($"Models={models.Length}, concurrency={concurrency}, renderer={thumbnails.RendererLabel}");

        var workers = Enumerable.Range(0, concurrency)
            .Select(_ => Task.Run(async () =>
            {
                while (true)
                {
                    var index = Interlocked.Increment(ref nextIndex);
                    if (index >= models.Length)
                        break;

                    var model = models[index];
                    try
                    {
                        var result = await thumbnails.GetThumbnailAsync(model, CancellationToken.None).ConfigureAwait(false);
                        if (result.FromCache)
                            Interlocked.Increment(ref cached);
                        if (!result.Success)
                            Interlocked.Increment(ref failed);
                        result.Image.Dispose();
                    }
                    catch
                    {
                        Interlocked.Increment(ref failed);
                    }

                    var done = Interlocked.Increment(ref completed);
                    if (done % 100 == 0 || done == models.Length)
                    {
                        Console.WriteLine($"thumbnail progress {done}/{models.Length}, cached={Volatile.Read(ref cached)}, failed={Volatile.Read(ref failed)}");
                    }
                }
            }))
            .ToArray();

        await Task.WhenAll(workers).ConfigureAwait(false);
        Console.WriteLine($"thumbnail build finished: total={models.Length}, cached={cached}, failed={failed}");
    }

    private static void ValidateComponents(string root)
    {
        root = Path.GetFullPath(root);
        var summaries = UeLibraryComponentRelationReader.LoadSummaries(root);
        var payload = new
        {
            root,
            componentSourceSummaries = summaries.Count,
            topSources = summaries.Take(5).Select(x => new
            {
                x.SourcePath,
                x.RelationCount,
                x.OwnerCount,
                x.ComponentCount,
                x.ModelReferenceCount,
                x.MaterialReferenceCount,
                x.TextureReferenceCount,
                x.AnimationReferenceCount
            })
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task SmokePreviewAsync(string root)
    {
        var index = UeLibraryIndexReader.Load(root);
        var pair = index.Models
            .Select(model =>
            {
                var key = UeLibraryIndexReader.MakeLibraryRelative(index.Root, model.Output);
                index.AnimationsByModel.TryGetValue(key, out var animations);
                return new
                {
                    Model = model,
                    Animation = animations?.FirstOrDefault(x => x.IsPreviewable)
                };
            })
            .FirstOrDefault(x => x.Animation != null);

        if (pair?.Animation == null)
            throw new InvalidDataException("没有找到可预览的模型动画组合。");

        var viewerSafeCache = new ViewerSafeGltfCache(index.Root);
        var composer = new PreviewComposer(index.Root, viewerSafeCache);
        var result = await composer.EnsurePreviewAsync(pair.Model, pair.Animation, CancellationToken.None);
        var payload = new
        {
            result.Success,
            model = pair.Model.Name,
            animation = pair.Animation.Name,
            result.OutputPath,
            result.ReportPath,
            result.Message
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        if (!result.Success)
            Environment.ExitCode = 1;
    }

    private sealed class ThumbnailWorkerRequest
    {
        public string Id { get; set; } = "";
        public string GltfPath { get; set; } = "";
        public string OutputPath { get; set; } = "";
    }

    private sealed class ThumbnailWorkerResponse
    {
        public string Id { get; set; } = "";
        public bool Success { get; set; }
        public string Error { get; set; } = "";
    }
}
