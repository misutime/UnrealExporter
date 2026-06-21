using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Security.Cryptography;
using System.Text;

namespace UE5LibraryBrowser;

internal sealed class ThumbnailService : IDisposable
{
    private const string ThumbnailCacheVersion = "opengl-v2-alpha-aware";
    private readonly string _cacheRoot;
    private readonly string? _f3dConsole;
    private readonly ViewerSafeGltfCache _viewerSafeCache;
    private readonly SemaphoreSlim _f3dGate;
    private readonly GltfThumbnailRenderPool _renderPool;
    private readonly ConcurrentDictionary<string, Task<ThumbnailResult>> _runningTasks = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public ThumbnailService(string libraryRoot, ViewerSafeGltfCache viewerSafeCache, int maxConcurrency)
    {
        _cacheRoot = Path.Combine(libraryRoot, ".ue5_browser_cache", "thumbnails", ThumbnailCacheVersion);
        Directory.CreateDirectory(_cacheRoot);
        _viewerSafeCache = viewerSafeCache;
        _f3dConsole = ToolLocator.FindF3dConsole();
        _f3dGate = new SemaphoreSlim(Math.Clamp(maxConcurrency, 1, 2));
        _renderPool = new GltfThumbnailRenderPool(Math.Max(1, maxConcurrency));
    }

    public bool HasF3d => !string.IsNullOrWhiteSpace(_f3dConsole) && File.Exists(_f3dConsole);

    public bool HasPersistentRenderer => !_disposed;

    public string RendererLabel => HasF3d ? "OpenGL worker + F3D fallback" : "OpenGL worker";

    public bool IsCached(UeLibraryModel model)
        => File.Exists(GetCachePath(model));

    public async Task<ThumbnailResult> GetThumbnailAsync(UeLibraryModel model, CancellationToken cancellationToken)
    {
        var cachePath = GetCachePath(model);
        if (File.Exists(cachePath))
            return new ThumbnailResult(LoadImageCopy(cachePath), true, true, "");

        var task = _runningTasks.GetOrAdd(cachePath, _ => RenderThumbnailAsync(model, cachePath, cancellationToken));
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            if (task.IsCompleted)
                _runningTasks.TryRemove(KeyValuePair.Create(cachePath, task));
        }
    }

    private async Task<ThumbnailResult> RenderThumbnailAsync(UeLibraryModel model, string cachePath, CancellationToken cancellationToken)
    {
        var modelPath = _viewerSafeCache.GetViewerSafeModelPath(model.Output);
        var persistentError = "";
        if (File.Exists(modelPath))
        {
            var result = await _renderPool.RenderAsync(modelPath, cachePath, cancellationToken).ConfigureAwait(false);
            if (result.Success && File.Exists(cachePath))
                return new ThumbnailResult(LoadImageCopy(cachePath), false, true, "");

            persistentError = result.Error;
        }

        if (HasF3d && File.Exists(modelPath))
        {
            await _f3dGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!File.Exists(cachePath))
                    await RenderWithF3dAsync(modelPath, cachePath, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _f3dGate.Release();
            }

            if (File.Exists(cachePath))
                return new ThumbnailResult(LoadImageCopy(cachePath), false, true, "");
        }

        var reason = !File.Exists(modelPath)
            ? "model file not found"
            : string.IsNullOrWhiteSpace(persistentError)
                ? "OpenGL worker did not produce a thumbnail"
                : "OpenGL worker failed: " + persistentError;
        if (!HasF3d && File.Exists(modelPath))
            reason += "; F3D fallback not found";
        return new ThumbnailResult(BuildPlaceholder(model), false, false, reason);
    }

    private string GetCachePath(UeLibraryModel model)
    {
        var sourceStamp = File.Exists(model.Output) ? File.GetLastWriteTimeUtc(model.Output).Ticks.ToString() : "missing";
        return Path.Combine(_cacheRoot, Hash(ThumbnailCacheVersion + "|" + model.Output + "|" + sourceStamp) + ".png");
    }

    private async Task RenderWithF3dAsync(string modelPath, string outputPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temp = outputPath + ".tmp.png";
        if (File.Exists(temp))
            File.Delete(temp);

        var start = new ProcessStartInfo(_f3dConsole!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--blending=ddp");
        start.ArgumentList.Add("--tone-mapping");
        start.ArgumentList.Add("--hdri-ambient");
        start.ArgumentList.Add("--anti-aliasing=fxaa");
        start.ArgumentList.Add("--resolution");
        start.ArgumentList.Add("256,256");
        start.ArgumentList.Add("--background-color");
        start.ArgumentList.Add("#2f3438");
        start.ArgumentList.Add("--camera-orthographic");
        start.ArgumentList.Add("--camera-azimuth-angle");
        start.ArgumentList.Add("35");
        start.ArgumentList.Add("--camera-elevation-angle");
        start.ArgumentList.Add("25");
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(temp);
        start.ArgumentList.Add(modelPath);

        using var process = Process.Start(start);
        if (process == null)
            return;

        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort cancellation.
            }
        });

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode == 0 && File.Exists(temp))
            File.Move(temp, outputPath, true);
    }

    private static Image LoadImageCopy(string path)
    {
        using var stream = File.OpenRead(path);
        using var loaded = Image.FromStream(stream);
        return new Bitmap(loaded);
    }

    private static Image BuildPlaceholder(UeLibraryModel model)
    {
        var bitmap = new Bitmap(220, 150);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var bg = new LinearGradientBrush(new Rectangle(0, 0, bitmap.Width, bitmap.Height), Color.FromArgb(34, 39, 46), Color.FromArgb(54, 62, 70), 45);
        g.FillRectangle(bg, 0, 0, bitmap.Width, bitmap.Height);
        using var pen = new Pen(Color.FromArgb(98, 115, 130), 2);
        g.DrawRectangle(pen, 18, 18, bitmap.Width - 36, bitmap.Height - 36);
        using var font = new Font("Segoe UI", 10, FontStyle.Bold);
        using var small = new Font("Segoe UI", 8);
        using var brush = new SolidBrush(Color.WhiteSmoke);
        var text = string.IsNullOrWhiteSpace(model.Name) ? "UE5 Model" : model.Name;
        g.DrawString(Trim(text, 24), font, brush, new RectangleF(24, 54, bitmap.Width - 48, 24));
        g.DrawString($"{model.UsableAnimationCount}/{model.AnimationCount} animations", small, brush, new RectangleF(24, 82, bitmap.Width - 48, 22));
        return bitmap;
    }

    private static string Hash(string value)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..Math.Max(0, max - 1)] + "...";

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _renderPool.Dispose();
        _f3dGate.Dispose();
    }
}

internal sealed record ThumbnailResult(Image Image, bool FromCache, bool Success, string Message);
