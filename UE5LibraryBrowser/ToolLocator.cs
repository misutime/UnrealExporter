namespace UE5LibraryBrowser;

internal static class ToolLocator
{
    public static string? FindF3dConsole()
    {
        var candidates = new[]
        {
            @"C:\Program Files\F3D\bin\f3d-console.exe",
            @"C:\Program Files\F3D\bin\f3d.exe"
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? FindF3dViewer()
    {
        var candidates = new[]
        {
            @"C:\Program Files\F3D\bin\f3d.exe",
            @"C:\Program Files\F3D\bin\f3d-console.exe"
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "UnrealExporter", "UnrealExporter.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }

        var cwd = new DirectoryInfo(Environment.CurrentDirectory);
        while (cwd != null)
        {
            if (File.Exists(Path.Combine(cwd.FullName, "UnrealExporter", "UnrealExporter.csproj")))
                return cwd.FullName;
            cwd = cwd.Parent;
        }

        return null;
    }
}
