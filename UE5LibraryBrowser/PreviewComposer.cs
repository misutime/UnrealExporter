using AssetLibrary.Core;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace UE5LibraryBrowser;

internal sealed record PreviewResult(bool Success, string OutputPath, string ReportPath, string Message);

internal sealed class PreviewComposer
{
    private const string PreviewCacheVersion = "preview-v4-f3d-stable-skinning";
    private readonly AssetLibraryIndex _index;
    private readonly string _libraryRoot;
    private readonly string _cacheRoot;
    private readonly ViewerSafeGltfCache _viewerSafeCache;

    public PreviewComposer(AssetLibraryIndex index, ViewerSafeGltfCache viewerSafeCache)
    {
        _index = index;
        _libraryRoot = index.Root;
        _cacheRoot = Path.Combine(_libraryRoot, ".asset_browser_cache", "animation_previews");
        _viewerSafeCache = viewerSafeCache;
        Directory.CreateDirectory(_cacheRoot);
    }

    public async Task<PreviewResult> EnsurePreviewAsync(AssetLibraryModel model, AssetLibraryAnimation animation, CancellationToken cancellationToken)
    {
        if (!_index.Capabilities.CanComposeAnimationPreview)
            return new PreviewResult(false, "", "", "当前素材库没有声明动画预览合成器。");

        var composer = _index.Capabilities.AnimationPreviewComposer ?? "";
        if (composer.Contains("AnimeStudio", StringComparison.OrdinalIgnoreCase))
            return await EnsureAnimeStudioPreviewAsync(model, animation, cancellationToken);

        return await EnsureUnrealPreviewAsync(model, animation, cancellationToken);
    }

    private async Task<PreviewResult> EnsureUnrealPreviewAsync(AssetLibraryModel model, AssetLibraryAnimation animation, CancellationToken cancellationToken)
    {
        var modelPath = _viewerSafeCache.GetViewerSafeModelPath(model.Output);
        if (!File.Exists(modelPath))
            return new PreviewResult(false, "", "", "模型文件不存在。");
        if (!File.Exists(animation.Output))
            return new PreviewResult(false, "", "", "动画 .ueanim 文件不存在。");
        if (!animation.IsPreviewable)
            return new PreviewResult(false, "", "", "这个动画不是最高可信可预览候选，或是容器/metadata 动画。");

        var directory = Path.Combine(_cacheRoot, Hash(PreviewCacheVersion + "|unreal|" + modelPath + "|" + animation.Output));
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, $"{SafeName(model.Name)}__{SafeName(animation.Name)}.preview.glb");
        var report = Path.Combine(directory, "preview_validation.db");
        if (File.Exists(output))
            return new PreviewResult(true, output, report, "使用缓存 preview。");

        var repoRoot = ToolLocator.FindRepoRoot();
        if (repoRoot == null)
            return new PreviewResult(false, output, report, "没有找到 UnrealExporter 项目根目录，无法合成 .ueanim preview。");

        var project = Path.Combine(repoRoot, "UnrealExporter", "UnrealExporter.csproj");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("--");
        start.ArgumentList.Add("--preview-ue-animation");
        start.ArgumentList.Add("--model");
        start.ArgumentList.Add(modelPath);
        start.ArgumentList.Add("--animation");
        start.ArgumentList.Add(animation.Output);
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(output);
        start.ArgumentList.Add("--report-db");
        start.ArgumentList.Add(report);

        return await RunPreviewProcessAsync(start, output, report, cancellationToken);
    }

    private async Task<PreviewResult> EnsureAnimeStudioPreviewAsync(AssetLibraryModel model, AssetLibraryAnimation animation, CancellationToken cancellationToken)
    {
        if (!File.Exists(model.Output))
            return new PreviewResult(false, "", "", "模型文件不存在。");
        if (!File.Exists(animation.Output))
            return new PreviewResult(false, "", "", "动画 sidecar 文件不存在。");
        if (!animation.IsPreviewable)
            return new PreviewResult(false, "", "", "这个动画不是可预览候选。");

        var directory = Path.Combine(_cacheRoot, Hash(PreviewCacheVersion + "|animestudio|" + model.Output + "|" + animation.Output));
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, $"{SafeName(model.Name)}__{SafeName(animation.Name)}.preview.gltf");
        var report = Path.Combine(directory, "preview_report.json");
        if (File.Exists(output))
            return new PreviewResult(true, output, report, "使用缓存 preview。");

        var project = ToolLocator.FindAnimeStudioCliProject();
        if (project == null)
            return new PreviewResult(false, output, report, "没有找到 AnimeStudio.CLI 项目，无法合成 preview。");

        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetDirectoryName(project)) ?? _libraryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("--framework");
        start.ArgumentList.Add("net9.0-windows");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add("compose-preview");
        start.ArgumentList.Add("--library-root");
        start.ArgumentList.Add(_libraryRoot);
        start.ArgumentList.Add("--model");
        start.ArgumentList.Add(model.Output);
        start.ArgumentList.Add("--animation");
        start.ArgumentList.Add(animation.Output);
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(output);
        start.ArgumentList.Add("--report");
        start.ArgumentList.Add(report);

        return await RunPreviewProcessAsync(start, output, report, cancellationToken);
    }

    private static async Task<PreviewResult> RunPreviewProcessAsync(ProcessStartInfo start, string output, string report, CancellationToken cancellationToken)
    {
        using var process = Process.Start(start);
        if (process == null)
            return new PreviewResult(false, output, report, "无法启动 dotnet。");

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

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var message = (stdout + Environment.NewLine + stderr).Trim();

        return new PreviewResult(
            process.ExitCode == 0 && File.Exists(output),
            output,
            report,
            string.IsNullOrWhiteSpace(message) ? $"exitCode={process.ExitCode}" : message);
    }

    public static void OpenWithF3d(string path)
    {
        if (!File.Exists(path))
            return;

        var f3d = ToolLocator.FindF3dViewer();
        if (f3d == null)
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }

        var start = new ProcessStartInfo(f3d) { UseShellExecute = false };
        start.ArgumentList.Add("--no-config");
        start.ArgumentList.Add("--blending=none");
        start.ArgumentList.Add("--tone-mapping");
        start.ArgumentList.Add("--hdri-ambient");
        start.ArgumentList.Add("--anti-aliasing=fxaa");
        start.ArgumentList.Add(path);
        Process.Start(start);
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var text = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(text) ? "asset" : text;
    }

    private static string Hash(string value)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
