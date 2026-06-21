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
