using System.Diagnostics;
using System.Runtime;

namespace UnrealExporter;

internal static class MemoryPressureGuard
{
    private const long BytesPerGiB = 1024L * 1024L * 1024L;
    private static readonly object WaitLock = new();
    private static DateTime _lastLogUtc = DateTime.MinValue;

    public static void LogConfiguration(ConfigObj config)
    {
        var softLimitBytes = GetSoftLimitBytes(config);
        if (softLimitBytes <= 0)
            return;

        Console.WriteLine(
            $"Memory soft limit: {FormatGiB(softLimitBytes)} GiB private bytes, checkInterval={GetCheckInterval(config)}ms");
    }

    public static void WaitIfNeeded(ConfigObj config, string phase, string? path = null)
    {
        var softLimitBytes = GetSoftLimitBytes(config);
        if (softLimitBytes <= 0)
            return;

        var resumeBelowBytes = Math.Max((long)(softLimitBytes * 0.92), softLimitBytes - 2 * BytesPerGiB);
        if (GetPrivateBytes() < softLimitBytes)
            return;

        lock (WaitLock)
        {
            var waitStart = DateTime.UtcNow;
            while (true)
            {
                var privateBytes = GetPrivateBytes();
                if (privateBytes < resumeBelowBytes)
                    return;

                LogWaitIfDue(config, phase, path, privateBytes, softLimitBytes, waitStart);
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                Thread.Sleep(GetCheckInterval(config));
            }
        }
    }

    private static void LogWaitIfDue(
        ConfigObj config,
        string phase,
        string? path,
        long privateBytes,
        long softLimitBytes,
        DateTime waitStart)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastLogUtc).TotalSeconds < GetLogIntervalSeconds(config))
            return;

        _lastLogUtc = now;
        var elapsed = now - waitStart;
        var suffix = string.IsNullOrWhiteSpace(path) ? "" : $" ({path})";
        Console.WriteLine(
            $"MEMORY throttle: {phase}{suffix}, private={FormatGiB(privateBytes)} GiB, limit={FormatGiB(softLimitBytes)} GiB, waited={elapsed.TotalSeconds:0}s");
    }

    private static long GetSoftLimitBytes(ConfigObj config)
    {
        if (config.MemorySoftLimitGb <= 0)
            return 0;

        return (long)(config.MemorySoftLimitGb * BytesPerGiB);
    }

    private static int GetCheckInterval(ConfigObj config)
        => config.MemoryCheckIntervalMs > 0 ? config.MemoryCheckIntervalMs : 1500;

    private static int GetLogIntervalSeconds(ConfigObj config)
        => config.MemoryWaitLogSeconds > 0 ? config.MemoryWaitLogSeconds : 30;

    private static long GetPrivateBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    private static string FormatGiB(long bytes)
        => (bytes / (double)BytesPerGiB).ToString("0.00");
}
