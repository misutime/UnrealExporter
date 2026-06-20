using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace UE5LibraryBrowser;

internal sealed record PreviewResult(bool Success, string OutputPath, string ReportPath, string Message);

internal sealed class PreviewComposer
{
    private readonly string _libraryRoot;
    private readonly string _cacheRoot;

    public PreviewComposer(string libraryRoot)
    {
        _libraryRoot = libraryRoot;
        _cacheRoot = Path.Combine(libraryRoot, ".ue5_browser_cache", "animation_previews");
        Directory.CreateDirectory(_cacheRoot);
    }

    public async Task<PreviewResult> EnsurePreviewAsync(UeLibraryModel model, UeLibraryAnimation animation, CancellationToken cancellationToken)
    {
        if (!File.Exists(model.Output))
            return new PreviewResult(false, "", "", "模型文件不存在。");
        if (!File.Exists(animation.Output))
            return new PreviewResult(false, "", "", "动画 .ueanim 文件不存在。");
        if (!animation.IsPreviewable)
            return new PreviewResult(false, "", "", "这个动画不是最高可信可预览候选，或是容器/metadata 动画。");

        var directory = Path.Combine(_cacheRoot, Hash(model.Output + "|" + animation.Output));
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, $"{SafeName(model.Name)}__{SafeName(animation.Name)}.preview.glb");
        var report = Path.Combine(directory, "preview_validation.json");
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
        start.ArgumentList.Add(model.Output);
        start.ArgumentList.Add("--animation");
        start.ArgumentList.Add(animation.Output);
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(output);
        start.ArgumentList.Add("--report");
        start.ArgumentList.Add(report);

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
