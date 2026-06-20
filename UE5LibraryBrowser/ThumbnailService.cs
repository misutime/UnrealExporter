using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Security.Cryptography;
using System.Text;

namespace UE5LibraryBrowser;

internal sealed class ThumbnailService
{
    private readonly string _cacheRoot;
    private readonly string? _f3dConsole;
    private readonly SemaphoreSlim _gate = new(2);

    public ThumbnailService(string libraryRoot)
    {
        _cacheRoot = Path.Combine(libraryRoot, ".ue5_browser_cache", "thumbnails");
        Directory.CreateDirectory(_cacheRoot);
        _f3dConsole = ToolLocator.FindF3dConsole();
    }

    public async Task<Image> GetThumbnailAsync(UeLibraryModel model, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(_cacheRoot, Hash(model.Output) + ".png");
        if (File.Exists(cachePath))
            return Image.FromFile(cachePath);

        if (!string.IsNullOrWhiteSpace(_f3dConsole) && File.Exists(model.Output))
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!File.Exists(cachePath))
                    await RenderWithF3dAsync(model.Output, cachePath, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }

            if (File.Exists(cachePath))
                return Image.FromFile(cachePath);
        }

        return BuildPlaceholder(model);
    }

    private async Task RenderWithF3dAsync(string modelPath, string outputPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var start = new ProcessStartInfo(_f3dConsole!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(outputPath);
        start.ArgumentList.Add("--resolution");
        start.ArgumentList.Add("220,150");
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
}
