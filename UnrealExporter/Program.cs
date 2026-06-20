using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Animations;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Textures.BC;
using CUE4Parse_Conversion.UEFormat.Enums;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Localization;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using JSBeautifyLib;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace UnrealExporter;

// TODO: CLI selection for selecting a checkpoint "Checkpoint files found. Select the one you would like to use."
// TODO: if outputType is unspecified, default to fileType

public class UnrealExporter
{
    public const int DefaultMaxDegreeOfParallelism = 12;
    private const string FortniteGameTitle = "FortniteGame";
    private const string FortnitePortingApiBase = "https://api.fortniteporting.app";
    private const string FortniteApiBase = "https://api.fortniteapi.com";
    private static readonly ConcurrentDictionary<string, object> FileLocks = [];
    private static readonly ConcurrentDictionary<string, bool> TextureAlphaCache = new(
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders = { UserAgent = { new ProductInfoHeaderValue("UnrealExporter", "1.0") } },
    };
    private static int totalChangedFiles = 0;
    private static int totalRegexMatches = 0;
    private static int totalExportedFiles = 0;
    private static int totalResumedExportJobs = 0;
    private static bool useCheckpoint = false;
    private static string RootDir = AppContext.BaseDirectory;
    private static readonly object ManifestWriteLock = new();
    private static readonly object CatalogWriteLock = new();
    private static readonly object AutoReferencedWriteLock = new();
    private static readonly object ResumeWriteLock = new();
    private static readonly ConcurrentDictionary<string, ExportEventSqliteWriter> ExportEventWriters = new(
        StringComparer.OrdinalIgnoreCase);

    public static void Main(string[] args)
    {
#if DEBUG
        // During development (dotnet run), BaseDirectory is bin\Debug\net8.0
        // Adjust to project root
        RootDir = Path.GetFullPath(Path.Combine(RootDir, @"..\..\..\.."));
#endif

        // For Oodle to work from outside of project directory
        Directory.SetCurrentDirectory(RootDir);

        if (TryRunPostProcessCommand(args) || TryRunMaterializeAnimationMetadataCommand(args) || TryRunTaskModelQualityCommand(args) || TryRunUEAnimationPreviewCommand(args))
            return;

        double trueStart = Now();

        // Initialize CUE4Parse dependencies
        InitOodle();
        InitZlib();
        InitDetex();

        try
        {
            List<ConfigObj> configs = LoadAllConfigs(args);

            foreach (ConfigObj config in configs)
            {
                double start = Now();
                totalChangedFiles = 0;
                totalRegexMatches = 0;
                totalExportedFiles = 0;
                totalResumedExportJobs = 0;

                EGame selectedVersion = GetGameVersion(config.Version);
                Console.WriteLine(
                    $"Config: {config.ConfigFileName} (object #{config.ConfigObjectIndex + 1})"
                );
                Console.WriteLine($"Game: {config.GameTitle}");
                Console.WriteLine($"Version: {selectedVersion}");
                Console.WriteLine($"Locale: {config.Lang}");
                Console.WriteLine($"Paks: {config.PaksDir}");
                Console.WriteLine($"Output: {config.OutputDir}");
                Console.WriteLine($"AES key: {config.Aes}");
                Console.WriteLine($"Log outputs: {config.LogOutputs}");
                Console.WriteLine($"Keep directory structure: {config.KeepDirectoryStructure}");
                Console.WriteLine($"Create new checkpoint: {config.CreateNewCheckpoint}");

                // Load CUE4Parse and export files
                AbstractFileProvider provider = CreateProvider(config, selectedVersion);
                try
                {
                    if (config.GenerateSourceIndex)
                        UESourceIndexBuilder.Build(provider, config);
                    Export(provider, config, start);
                }
                finally
                {
                    FlushExportEventWriters();
                }
            }

            Console.WriteLine(
                $"UnrealExporter finished in {Elapsed(trueStart, Now(), 1000)} seconds"
            );
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("\nExiting UnrealExporter.");
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine($"ERROR: no config files found.");
        }
    }

    private static bool TryRunPostProcessCommand(string[] args)
    {
        if (args.Length == 0)
            return false;

        if (
            !args[0].Equals("--postprocess-library", StringComparison.OrdinalIgnoreCase)
            && !args[0].Equals("postprocess-library", StringComparison.OrdinalIgnoreCase)
        )
            return false;

        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.WriteLine("ERROR: --postprocess-library requires an exported library root path.");
            Console.WriteLine("Usage: dotnet run --project UnrealExporter -- --postprocess-library <outputDir> [--dedupe-textures]");
            return true;
        }

        // 后处理只读取已导出的 GLB/JSON/PNG，不需要重新挂载 pak。
        var root = args[1];
        var dedupeTextures = args.Any(x => x.Equals("--dedupe-textures", StringComparison.OrdinalIgnoreCase));
        UELibraryPostProcessor.Run(root, dedupeTextures);
        return true;
    }

    private static bool TryRunMaterializeAnimationMetadataCommand(string[] args)
    {
        if (args.Length == 0)
            return false;

        if (
            !args[0].Equals("--materialize-animation-metadata", StringComparison.OrdinalIgnoreCase)
            && !args[0].Equals("materialize-animation-metadata", StringComparison.OrdinalIgnoreCase)
        )
            return false;

        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.WriteLine("ERROR: --materialize-animation-metadata requires an exported library root path.");
            Console.WriteLine("Usage: dotnet run --project UnrealExporter -- --materialize-animation-metadata <outputDir>");
            return true;
        }

        UELibraryPostProcessor.MaterializeAnimationMetadataSidecars(args[1]);
        return true;
    }

    private static bool TryRunUEAnimationPreviewCommand(string[] args)
    {
        if (args.Length == 0)
            return false;

        if (
            !args[0].Equals("--preview-ue-animation", StringComparison.OrdinalIgnoreCase)
            && !args[0].Equals("preview-ue-animation", StringComparison.OrdinalIgnoreCase)
        )
            return false;

        var model = GetCommandOption(args, "--model");
        var animation = GetCommandOption(args, "--animation");
        var output = GetCommandOption(args, "--output");
        var report = GetCommandOption(args, "--report");
        var skipBoneRegex = GetCommandOption(args, "--skip-animation-bone-regex");
        if (string.IsNullOrWhiteSpace(model) ||
            string.IsNullOrWhiteSpace(animation) ||
            string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine("ERROR: --preview-ue-animation requires --model <model.glb> --animation <anim.ueanim> --output <preview.glb> [--report <preview_validation.json>].");
            Environment.ExitCode = 2;
            return true;
        }

        Environment.ExitCode = UEAnimationPreviewBuilder.Run(model, animation, output, report, skipBoneRegex);
        return true;
    }

    private static bool TryRunTaskModelQualityCommand(string[] args)
    {
        if (args.Length == 0)
            return false;

        if (
            !args[0].Equals("--refresh-task-model-quality", StringComparison.OrdinalIgnoreCase)
            && !args[0].Equals("refresh-task-model-quality", StringComparison.OrdinalIgnoreCase)
        )
            return false;

        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.WriteLine("ERROR: --refresh-task-model-quality requires an exported library root path.");
            Console.WriteLine("Usage: dotnet run --project UnrealExporter -- --refresh-task-model-quality <outputDir>");
            return true;
        }

        UELibraryPostProcessor.RefreshTaskModelQualityReport(args[1]);
        return true;
    }

    private static string? GetCommandOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }

        return null;
    }

    public static void InitOodle()
    {
        var oodlePath = Path.Combine(RootDir, OodleHelper.OODLE_NAME_OLD);
        OodleHelper.Initialize(File.Exists(oodlePath) ? oodlePath : null);
    }

    public static void InitZlib()
    {
        ZlibHelper.Initialize();
    }

    public static void InitDetex()
    {
        var detexPath = Path.Combine(RootDir, DetexHelper.DLL_NAME);
        if (!File.Exists(detexPath))
        {
            DetexHelper.LoadDll(detexPath);
        }

        DetexHelper.Initialize(detexPath);
    }

    public static List<ConfigObj>? LoadConfigFile(string path)
    {
        try
        {
            string jsonString = File.ReadAllText(path);
            List<ConfigObj> configObjs =
                JsonConvert.DeserializeObject<List<ConfigObj>>(jsonString) ?? [];
            int index = 0;
            foreach (ConfigObj obj in configObjs)
            {
                obj.ConfigFileName = path.Split(Path.DirectorySeparatorChar).Last();
                obj.ConfigObjectIndex = index;
                index++;
            }
            return configObjs;
        }
        catch (FileNotFoundException)
        {
            Console.WriteLine($"ERROR: {path} not found.");
        }
        catch (JsonException)
        {
            Console.WriteLine($"ERROR: {path} is not a valid JSON format.");
        }
        return null;
    }

    public static List<ConfigObj> LoadConfigsFromSelector(
        string[] args,
        string[] allConfigFilePaths
    )
    {
        if (allConfigFilePaths.Length < 1)
        {
            throw new FileNotFoundException();
        }
        bool[] selectedOptions = new bool[allConfigFilePaths.Length + 1];
        int currentOption = 0;
        List<string> gameTitles = [];
        int longestFileName = 0;

        // Get longest file name for padding purposes
        // Also check items that were passed in args
        for (int i = 0; i < allConfigFilePaths.Length; i++)
        {
            string fileName = allConfigFilePaths[i].Split(Path.DirectorySeparatorChar).Last();
            if (fileName.Length > longestFileName)
            {
                longestFileName = fileName.Length;
            }

            // If "bp" was passed as an arg, set "bp.json" to checked by default
            if (args.Any(arg => arg.Equals(fileName.Split(".").First())))
            {
                selectedOptions[i + 1] = true;
            }
        }

        List<string> paddedFileNames = [];
        foreach (string filePath in allConfigFilePaths)
        {
            string fileName = filePath.Split(Path.DirectorySeparatorChar).Last();
            paddedFileNames.Add(fileName.PadRight(longestFileName + 1, ' '));
        }

        for (int i = 0; i < allConfigFilePaths.Length; i++)
        {
            List<ConfigObj>? configObjsInFile = LoadConfigFile(allConfigFilePaths[i]);

            if (configObjsInFile != null)
            {
                List<string> gameTitlesInFile = [];
                foreach (ConfigObj configObj in configObjsInFile)
                {
                    gameTitlesInFile.Add(configObj.GameTitle);
                }
                gameTitles.Add($"({string.Join(", ", [.. gameTitlesInFile])})");
            }
            else
            {
                gameTitles.Add("");
            }
        }

        while (true)
        {
            Console.Clear(); // Clear the console screen before re-printing options
            Console.WriteLine(
                $"{(allConfigFilePaths.Length > 1 ? "Multiple config files detected. Select the ones" : "Select the config files")} you wish to execute with arrows keys, space to select, enter to confirm, or escape to exit."
            );

            for (int i = 0; i < selectedOptions.Length; i++)
            {
                Console.Write(currentOption == i ? "> " : "  ");

                if (i > 0)
                {
                    Console.Write(selectedOptions[i] ? "[x] " : "[ ] ");
                    Console.WriteLine($"{paddedFileNames[i - 1]} {gameTitles[i - 1]}");
                }
                else if (i == 0 && selectedOptions[0])
                {
                    Console.WriteLine("Unselect all");
                }
                else
                {
                    Console.WriteLine("Select all");
                }
            }

            var key = Console.ReadKey(true);

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    currentOption =
                        (currentOption - 1 + selectedOptions.Length) % selectedOptions.Length;
                    break;
                case ConsoleKey.DownArrow:
                    currentOption = (currentOption + 1) % selectedOptions.Length;
                    break;
                case ConsoleKey.Spacebar:
                    if (currentOption == 0)
                    {
                        for (int i = 0; i < selectedOptions.Length; i++)
                        {
                            selectedOptions[i] = !selectedOptions[i];
                        }
                    }
                    else
                    {
                        selectedOptions[currentOption] = !selectedOptions[currentOption];
                    }
                    break;
                case ConsoleKey.Enter:
                    List<string> result = [];
                    for (int i = 1; i < selectedOptions.Length; i++)
                    {
                        if (selectedOptions[i])
                        {
                            result.Add(
                                allConfigFilePaths[i - 1]
                                    .Split(Path.DirectorySeparatorChar)
                                    .Last()
                                    .Split(".")[0]
                            );
                        }
                    }
                    Console.WriteLine();
                    return LoadAllConfigs([.. result]);
                case ConsoleKey.Escape:
                    throw new OperationCanceledException();
            }
        }
    }

    public static List<ConfigObj> LoadAllConfigs(string[] args)
    {
        List<ConfigObj> allConfigObjs = [];
        string[] allConfigFilePaths = Directory.GetFiles($"{RootDir}\\configs");
        bool isReleaseMode = false;

#if !DEBUG
        isReleaseMode = true;
#endif

        if (args.Length > 0 || isReleaseMode)
        {
            // Show list of config files
            if (args.Any(arg => arg.Equals("--list")) || (isReleaseMode && args.Length < 1))
            {
                args = args.Where(arg => arg != "--list").ToArray();
                return LoadConfigsFromSelector(args, allConfigFilePaths);
            }

            int totalConfigFiles = 0;

            // Load all files
            if (args.Any(arg => arg.Equals("all")))
            {
                foreach (var filePath in allConfigFilePaths)
                {
                    List<ConfigObj>? configObjsInFile = LoadConfigFile(filePath);

                    if (configObjsInFile != null)
                    {
                        foreach (ConfigObj configObj in configObjsInFile)
                        {
                            allConfigObjs.Add(configObj);
                        }
                        totalConfigFiles++;
                        Console.WriteLine(
                            $"{filePath.Split(Path.DirectorySeparatorChar).Last()} ({configObjsInFile.Count} object{(configObjsInFile.Count > 1 ? "s" : "")})"
                        );
                    }
                }
            }
            // Load specified files
            else
            {
                foreach (var arg in args)
                {
                    List<ConfigObj>? configObjsInFile = LoadConfigFile(
                        $"{RootDir}\\configs\\{arg}.json"
                    );

                    if (configObjsInFile != null)
                    {
                        foreach (ConfigObj configObj in configObjsInFile)
                        {
                            allConfigObjs.Add(configObj);
                        }
                        totalConfigFiles++;
                        Console.WriteLine(
                            $"{arg}.json ({configObjsInFile.Count} object{(configObjsInFile.Count > 1 ? "s" : "")})"
                        );
                    }
                }
            }

            Console.WriteLine(
                $"Loaded {totalConfigFiles} config file(s) ({allConfigObjs.Count} total object{(allConfigObjs.Count > 1 ? "s" : "")})"
            );
        }
        // Fallback to default config.json
        else
        {
            Console.WriteLine("No config file(s) specified. Defaulting to config.json...");
            List<ConfigObj>? configObjsInFile = LoadConfigFile($"{RootDir}\\configs\\config.json");

            if (configObjsInFile != null)
            {
                foreach (ConfigObj configObj in configObjsInFile)
                {
                    allConfigObjs.Add(configObj);
                }
                Console.WriteLine(
                    $"Loaded config.json ({allConfigObjs.Count} object{(allConfigObjs.Count > 1 ? "s" : "")})"
                );
            }
        }
        Console.WriteLine();

        return allConfigObjs;
    }

    public static EGame GetGameVersion(string versionString)
    {
        string version;

        // "4.27"
        if (versionString.Contains('.'))
        {
            version = $"UE{versionString.Replace('.', '_')}";
        }
        // "tower of fantasy"
        else if (versionString.Split(" ").Length > 1)
        {
            TextInfo textInfo = new CultureInfo("en-US", false).TextInfo;
            version = textInfo.ToTitleCase(versionString).Replace(" ", "");
        }
        // "TowerOfFantasy"
        else
        {
            version = versionString;
        }

        EGame selectedVersion = (EGame)Enum.Parse(typeof(EGame), $"GAME_{version}");

        return selectedVersion;
    }

    public static AbstractFileProvider CreateProvider(ConfigObj config, EGame selectedVersion)
    {
        // Load CUE4Parse
        // TODO: Ignore mods (all folders within /Content/Paks)
        bool isFortnite = IsFortniteConfig(config);
        var extraDirectories = isFortnite ? GetFortniteExtraDirectories(config) : [];
        var versions = new VersionContainer(selectedVersion);
        if (isFortnite)
        {
            versions["SkeletalMesh.KeepMobileMinLODSettingOnDesktop"] = true;
            versions["StaticMesh.KeepMobileMinLODSettingOnDesktop"] = true;
        }

        var provider = new DefaultFileProvider(
            new DirectoryInfo(config.PaksDir),
            extraDirectories,
            SearchOption.AllDirectories,
            versions,
            StringComparer.OrdinalIgnoreCase
        );

        if (isFortnite)
        {
            provider.SkipReferencedTextures = true;
            provider.ReadNaniteData = config.ReadNaniteData ?? true;

            if (config.LoadOnDemandTocs ?? true)
            {
                provider.OnDemandOptions = new IoStoreOnDemandOptions
                {
                    ChunkHostUri = new Uri(config.OnDemandHostUri ?? "https://download.epicgames.com/", UriKind.Absolute),
                    ChunkCacheDirectory = new DirectoryInfo(
                        config.OnDemandCacheDir ?? Path.Combine(RootDir, ".cache", "fortnite-on-demand")
                    ),
                    Timeout = TimeSpan.FromSeconds(config.OnDemandTimeoutSeconds > 0 ? config.OnDemandTimeoutSeconds : 120),
                    Authorization = string.IsNullOrWhiteSpace(config.EpicAuthToken)
                        ? null
                        : new AuthenticationHeaderValue("Bearer", config.EpicAuthToken),
                };
                provider.OnDemandOptions.ChunkCacheDirectory.Create();
            }
        }

        provider.Initialize();

        SubmitEncryptionKeys(provider, config, isFortnite);

        // Set locale if provided, otherwise English. Some games ship no matching
        // locres culture, and asset export can continue without localization.
        try
        {
            if (config.Lang?.Length > 0)
            {
                ELanguage selectedLang = (ELanguage)Enum.Parse(typeof(ELanguage), config.Lang);
                provider.LoadLocalization(selectedLang);
            }
            else
            {
                provider.LoadLocalization(ELanguage.English);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARN: localization skipped ({ex.Message})");
        }

        // TEMP (need to fix patchProvider for utoc/ucas support). For now it's not guaranteed that the patch paks will be reconciled correctly.
        string pathToMapping = ResolveMappingPath(config, isFortnite);
        if (File.Exists(pathToMapping))
        {
            Console.WriteLine($"Using mapping file: {pathToMapping}");
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(pathToMapping, StringComparer.Ordinal);
        }

        PrintDebugFileMatches(provider, config);

        return provider;
    }

    private static void PrintDebugFileMatches(DefaultFileProvider provider, ConfigObj config)
    {
        if (config.DebugFileContains is not { Count: > 0 })
            goto RegexDebug;

        foreach (var needle in config.DebugFileContains.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            Console.WriteLine($"Debug file search: {needle}");
            foreach (var file in provider.Files.Values
                         .Where(file => file.Path.Contains(needle, StringComparison.OrdinalIgnoreCase))
                         .Take(30))
            {
                Console.WriteLine($"  {file.Path} [{file.GetType().Name}]");
            }
        }

    RegexDebug:
        if (config.DebugFileRegex is not { Count: > 0 })
            return;

        foreach (var pattern in config.DebugFileRegex.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            Console.WriteLine($"Debug file regex: {pattern}");
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            foreach (var file in provider.Files.Values
                         .Where(file => regex.IsMatch(file.Path))
                         .Take(config.DebugFileLimit > 0 ? config.DebugFileLimit : 50))
            {
                Console.WriteLine($"  {file.Path} [{file.GetType().Name}]");
            }
        }
    }

    private static bool IsFortniteConfig(ConfigObj config) =>
        config.FortniteMode
        || config.GameTitle.Equals(FortniteGameTitle, StringComparison.OrdinalIgnoreCase);

    private static DirectoryInfo[] GetFortniteExtraDirectories(ConfigObj config)
    {
        var paths = new List<string>();
        if (config.LoadInstalledBundles ?? true)
        {
            paths.Add(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FortniteGame",
                    "Saved",
                    "PersistentDownloadDir",
                    "GameCustom",
                    "InstalledBundles"
                )
            );
        }

        if (config.ExtraDirectories is { Count: > 0 })
            paths.AddRange(config.ExtraDirectories);

        var directories = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Environment.ExpandEnvironmentVariables(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new DirectoryInfo(path))
            .Where(directory => directory.Exists)
            .ToArray();

        foreach (var directory in directories)
            Console.WriteLine($"Fortnite extra directory: {directory.FullName}");

        return directories;
    }

    private static void SubmitEncryptionKeys(
        DefaultFileProvider provider,
        ConfigObj config,
        bool isFortnite
    )
    {
        var keys = new List<KeyValuePair<FGuid, FAesKey>>();
        string aes =
            config.Aes.Length > 0
                ? config.Aes
                : "0x0000000000000000000000000000000000000000000000000000000000000000";

        if (isFortnite && (config.AutoFetchFortniteKeys ?? true))
        {
            try
            {
                var response = FetchFortniteAes(config.FortniteVersion);
                if (!string.IsNullOrWhiteSpace(response.MainKey))
                {
                    Console.WriteLine($"Fortnite AES version: {response.Version}");
                    aes = response.MainKey;
                }

                foreach (var dynamicKey in response.DynamicKeys)
                {
                    if (string.IsNullOrWhiteSpace(dynamicKey.Guid) || string.IsNullOrWhiteSpace(dynamicKey.Key))
                        continue;

                    keys.Add(
                        new KeyValuePair<FGuid, FAesKey>(
                            new FGuid(dynamicKey.Guid),
                            new FAesKey(dynamicKey.Key)
                        )
                    );
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARN: failed to fetch Fortnite AES keys ({ex.Message})");
            }
        }

        keys.Insert(0, new KeyValuePair<FGuid, FAesKey>(new FGuid(), new FAesKey(aes)));

        if (config.DynamicAesKeys is { Count: > 0 })
        {
            foreach (var dynamicKey in config.DynamicAesKeys)
            {
                if (string.IsNullOrWhiteSpace(dynamicKey.Guid) || string.IsNullOrWhiteSpace(dynamicKey.Key))
                    continue;

                keys.Add(
                    new KeyValuePair<FGuid, FAesKey>(
                        new FGuid(dynamicKey.Guid),
                        new FAesKey(dynamicKey.Key)
                    )
                );
            }
        }

        int mountedCount = provider.SubmitKeys(keys);
        Console.WriteLine($"Mounted encrypted archives: {mountedCount}");

        int plainMountedCount = provider.Mount();
        if (plainMountedCount > 0)
            Console.WriteLine($"Mounted plain archives: {plainMountedCount}");
    }

    private static string ResolveMappingPath(ConfigObj config, bool isFortnite)
    {
        if (!string.IsNullOrWhiteSpace(config.MappingsFile))
            return Environment.ExpandEnvironmentVariables(config.MappingsFile);

        string pathToMapping = Path.Combine(RootDir, "mappings", $"{config.GameTitle}.usmap");
        if (!isFortnite || !(config.AutoFetchFortniteMappings ?? true))
            return pathToMapping;

        try
        {
            var response = FetchFortniteMappings(config.FortniteVersion);
            if (string.IsNullOrWhiteSpace(response.Url))
                return pathToMapping;

            Directory.CreateDirectory(Path.GetDirectoryName(pathToMapping)!);
            if (!File.Exists(pathToMapping) || !MatchesHash(pathToMapping, response.HashMd5, response.Hash))
            {
                Console.WriteLine($"Downloading Fortnite mappings {response.Version}: {response.Url}");
                var bytes = Http.GetByteArrayAsync(response.Url).GetAwaiter().GetResult();
                File.WriteAllBytes(pathToMapping, bytes);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARN: failed to fetch Fortnite mappings ({ex.Message})");
        }

        return pathToMapping;
    }

    private static FortniteAesResponse FetchFortniteAes(string? version)
    {
        return FetchFortniteApiJson<FortniteAesResponse>("aes", version);
    }

    private static FortniteMappingsResponse FetchFortniteMappings(string? version)
    {
        return FetchFortniteApiJson<FortniteMappingsResponse>("mappings", version);
    }

    private static T FetchFortniteApiJson<T>(string endpoint, string? version)
        where T : new()
    {
        Exception? lastError = null;
        foreach (string apiBase in new[] { FortnitePortingApiBase, FortniteApiBase })
        {
            try
            {
                string parameterName = apiBase == FortniteApiBase ? "Version" : "version";
                string url = $"{apiBase}/v1/{endpoint}";
                if (!string.IsNullOrWhiteSpace(version))
                    url += $"?{parameterName}={Uri.EscapeDataString(version)}";

                var json = Http.GetStringAsync(url).GetAwaiter().GetResult();
                return JsonConvert.DeserializeObject<T>(json) ?? new T();
            }
            catch (Exception ex)
            {
                lastError = ex;
                Console.WriteLine($"WARN: failed to fetch Fortnite {endpoint} from {apiBase} ({ex.Message})");
            }
        }

        throw lastError ?? new InvalidOperationException($"Failed to fetch Fortnite {endpoint}");
    }

    private static bool MatchesHash(string path, string? expectedMd5, string? expectedSha1)
    {
        if (!string.IsNullOrWhiteSpace(expectedMd5))
            return MatchesHash(path, expectedMd5, MD5.Create());

        if (!string.IsNullOrWhiteSpace(expectedSha1))
            return MatchesHash(path, expectedSha1, SHA1.Create());

        return true;
    }

    private static bool MatchesHash(string path, string expected, HashAlgorithm algorithm)
    {
        using (algorithm)
        {
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(algorithm.ComputeHash(stream)).ToLowerInvariant();
            return actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Export(AbstractFileProvider provider, ConfigObj config, double start)
    {
        // Load checkpoint if provided
        useCheckpoint = false;
        Dictionary<string, long> loadedCheckpoint = LoadCheckpoint(config);
        ConcurrentDictionary<string, long> newCheckpointDict = [];

        Console.WriteLine($"Scanning {provider.Files.Count} files...{Environment.NewLine}");
        int maxDegreeOfParallelism =
            config.MaxDegreeOfParallelism > 0
                ? config.MaxDegreeOfParallelism
                : DefaultMaxDegreeOfParallelism;
        Console.WriteLine($"Max parallel exports: {maxDegreeOfParallelism}");
        var autoReferencedExportRules = BuildAutoReferencedExportRules(provider, config);
        var resumeExports = ShouldResumeExports(config);
        using var exportResumeStore = resumeExports ? ExportResumeStore.Open(config) : null;
        if (resumeExports)
            Console.WriteLine($"Export resume: loaded {exportResumeStore!.Count} completed job(s).");

        // Loop through all files and export the ones that match any of the config.export paths (converted to regex)
        Parallel.ForEach(
            provider.Files,
            new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
            file =>
            {
                // "Hotta/Content/Resources/UI/Activity/Activity/DT_Activityquest_Balance.uasset"
                // file.Value.Path

                // "Hotta\Content\Resources\UI\Activity\Activity"
                var fileDir = Path.GetDirectoryName(file.Value.Path);

                // "DT_Activityquest_Balance"
                var fileName = Path.GetFileNameWithoutExtension(file.Value.Path);

                // "Hotta\Content\Resources\UI\Activity\Activity\DT_Activityquest_Balance"
                var filePath = fileDir + Path.DirectorySeparatorChar + fileName;

                // "D:\UnrealExporter\output\Hotta\Content\Resources\UI\Activity\Activity"
                var outputDir = config.KeepDirectoryStructure
                    ? Path.GetFullPath(config.OutputDir) + Path.DirectorySeparatorChar + fileDir
                    : Path.GetFullPath(config.OutputDir);

                // "D:\UnrealExporter\output\Hotta\Content\Resources\UI\Activity\Activity\DT_Activityquest_Balance"
                var outputPath = outputDir + Path.DirectorySeparatorChar + fileName;

                var normalizedFilePath = NormalizeAssetPath(file.Value.Path);
                var regexMatches = config.Export
                    .Where(path =>
                    {
                        var separator = path.LastIndexOf(':');
                        return separator > 0
                               && new Regex(
                                   "^" + path[..separator] + "$",
                                   RegexOptions.IgnoreCase
                               ).IsMatch(file.Value.Path);
                    })
                    .ToArray();
                var matchedByConfig = regexMatches.Length > 0;
                bool isExclude = config.Exclude.Any(path =>
                    new Regex("^" + path + "$", RegexOptions.IgnoreCase).IsMatch(file.Value.Path)
                );
                autoReferencedExportRules.TryGetValue(normalizedFilePath, out var autoReferencedRules);
                var exportJobs = BuildExportJobs(
                    regexMatches,
                    matchedByConfig,
                    explicitRulesAreActive: !isExclude,
                    autoReferencedRules);
                var activeExportJobs = isExclude
                    ? exportJobs.Where(x => x.AutoReferencedRule != null).ToList()
                    : exportJobs;

                bool isChanged = true;

                // If checkpoint is specified, skip files whose sizes are the same as in the checkpoint
                if (
                    useCheckpoint
                    && loadedCheckpoint.TryGetValue(file.Value.Path, out long fileSize)
                )
                {
                    isChanged = fileSize != file.Value.Size;
                    if (isChanged)
                        Interlocked.Increment(ref totalChangedFiles);
                }

                if (config.CreateNewCheckpoint)
                    newCheckpointDict.TryAdd(file.Value.Path, file.Value.Size);

                if (activeExportJobs.Count > 0 && isChanged)
                {
                    // "uasset"
                    var fileType = file.Value.Path.SubstringAfterLast('.').ToLower();

                    foreach (var exportJob in activeExportJobs)
                    {
                        var exportedThisJob = false;
                        var jobOutputs = new ConcurrentBag<string>();

                        // "json" etc.
                        var outputType = exportJob.OutputType;
                        var resumeKey = BuildExportResumeKey(file.Value.Path, outputType, exportJob.AutoReferencedRule);
                        if (resumeExports && exportResumeStore!.ShouldSkip(resumeKey, file.Value.Size))
                        {
                            if (config.LogOutputs)
                                Console.WriteLine($"~~ resume skip {file.Value.Path}:{outputType}");
                            Interlocked.Increment(ref totalResumedExportJobs);
                            Interlocked.Increment(ref totalRegexMatches);
                            continue;
                        }

                        try
                        {
                            switch (fileType)
                            {
                                // Referencing CUE4ParseViewModel.cs from Fmodel source code
                                case "uasset":
                                case "umap":
                                {
                                    var allObjects = provider
                                        .LoadPackage(file.Value)
                                        .GetExports()
                                        .ToArray();

                                    if (outputType == "png")
                                    {
                                        foreach (var obj in allObjects)
                                        {
                                            if (!MatchesAutoReferencedTarget(obj, exportJob.AutoReferencedRule))
                                                continue;

                                            // 配置规则导出首个贴图；自动补导按目标对象导出，避免同包多贴图互相遮住。
                                            if (obj is UTexture2D texture)
                                            {
                                                var decodedTexture = texture.Decode(
                                                    ETexturePlatform.DesktopMobile
                                                );

                                                if (decodedTexture != null)
                                                {
                                                    var objectOutputPath = BuildObjectOutputPath(outputPath, outputDir, fileName, obj, exportJob.AutoReferencedRule);
                                                    if (config.LogOutputs)
                                                        Console.WriteLine("=> " + objectOutputPath + ".png");
                                                    if (!Directory.Exists(Path.GetDirectoryName(objectOutputPath)!))
                                                        Directory.CreateDirectory(Path.GetDirectoryName(objectOutputPath)!);

                                                    // Save the bitmap to a file
                                                    try
                                                    {
                                                        using SKBitmap bitmap =
                                                            decodedTexture.ToSkBitmap();
                                                        using (
                                                            SKImage image = SKImage.FromBitmap(bitmap)
                                                        )
                                                        {
                                                            using SKData data = image.Encode(
                                                                SKEncodedImageFormat.Png,
                                                                100
                                                            );
                                                            var pngPath = objectOutputPath + ".png";
                                                            var fileLock = FileLocks.GetOrAdd(
                                                                pngPath,
                                                                _ => new object()
                                                            );
                                                            lock (fileLock)
                                                            {
                                                                using Stream stream = File.Open(
                                                                    pngPath,
                                                                    FileMode.Create,
                                                                    FileAccess.Write,
                                                                    FileShare.Read
                                                                );
                                                                data.SaveTo(stream);
                                                            }
                                                            jobOutputs.Add(pngPath);
                                                        }
                                                    }
                                                    catch (IOException ex)
                                                    {
                                                        Console.WriteLine(
                                                            $"WARN: Skipped locked texture {objectOutputPath}.png ({ex.Message})"
                                                        );
                                                        break;
                                                    }
                                                    AppendExportManifest(config, file.Value.Path, obj, objectOutputPath + ".png", "Texture");
                                                    AppendAssetCatalog(config, BuildTextureCatalogEntry(file.Value.Path, texture, objectOutputPath + ".png"));
                                                    exportedThisJob = true;
                                                    Interlocked.Increment(ref totalExportedFiles);

                                                    break;
                                                }
                                                else
                                                {
                                                    Console.WriteLine(
                                                        $"ERROR: Failed to export {file.Value.Path} (not a valid image bitmap)."
                                                    );
                                                }
                                            }
                                            else
                                            {
                                                // Not necessarily an error
                                                // Console.WriteLine($"ERROR: Failed to export {file.Value.Path} (object is not of type UTexture2D).");
                                            }
                                        }
                                    }
                                    else if (outputType == "json")
                                    {
                                        // Serialize to JSON, then write to file
                                        if (config.LogOutputs)
                                            Console.WriteLine("=> " + outputPath + ".json");
                                        var json = JsonConvert.SerializeObject(
                                            allObjects,
                                            Formatting.Indented
                                        );
                                        if (!Directory.Exists(outputDir))
                                            Directory.CreateDirectory(outputDir);
                                        var jsonPath = outputPath + ".json";
                                        File.WriteAllText(jsonPath, json);
                                        jobOutputs.Add(jsonPath);
                                        AppendExportManifest(config, file.Value.Path, null, jsonPath, "Json");
                                        foreach (var material in allObjects.OfType<UMaterialInterface>())
                                            AppendAssetCatalog(config, BuildMaterialCatalogEntry(file.Value.Path, material, outputPath + ".json"));
                                        exportedThisJob = true;
                                        Interlocked.Increment(ref totalExportedFiles);
                                    }
                                    // Referenced from FModel's ExportData(). uexp is tied to the uasset file.
                                    // https://github.com/4sval/FModel/blob/master/FModel/ViewModels/CUE4ParseViewModel.cs#L928
                                    // Possible refactor to include TryGetValue
                                    // https://github.com/FabianFG/CUE4Parse/blob/b3550db731303a6f383ca2b4f61737ca870deef2/CUE4Parse/FileProvider/AbstractFileProvider.cs#L562
                                    else if (outputType == "uasset")
                                    {
                                        if (provider.TrySavePackage(file.Value, out var assets))
                                        {
                                            Parallel.ForEach(
                                                assets,
                                                kvp =>
                                                {
                                                    if (config.LogOutputs)
                                                        Console.WriteLine(
                                                            "=> "
                                                                + outputPath
                                                                + "."
                                                                + kvp.Key.SubstringAfterLast('.')
                                                        );
                                                    if (!Directory.Exists(outputDir))
                                                        Directory.CreateDirectory(outputDir);
                                                    File.WriteAllBytes(
                                                        outputPath
                                                            + "."
                                                            + kvp.Key.SubstringAfterLast('.'),
                                                        kvp.Value
                                                    );
                                                    var rawPath = outputPath + "." + kvp.Key.SubstringAfterLast('.');
                                                    jobOutputs.Add(rawPath);
                                                    AppendExportManifest(config, file.Value.Path, null, rawPath, "RawPackage");
                                                    exportedThisJob = true;
                                                    Interlocked.Increment(ref totalExportedFiles);
                                                }
                                            );
                                        }
                                    }
                                    else if (outputType is "glb" or "gltf")
                                    {
                                        foreach (var obj in allObjects)
                                        {
                                            if (!MatchesAutoReferencedTarget(obj, exportJob.AutoReferencedRule))
                                                continue;

                                            if (obj is not UStaticMesh && obj is not USkeletalMesh)
                                                continue;

                                            var options = new ExporterOptions
                                            {
                                                LodFormat = ELodFormat.FirstLod,
                                                // CUE4Parse 当前只直接写 GLB；gltf 在导出成功后拆成文本容器和 bin。
                                                MeshFormat = EMeshFormat.Gltf2,
                                                MaterialFormat = EMaterialFormat.AllLayersNoRef,
                                                TextureFormat = ETextureFormat.Png,
                                                CompressionFormat = EFileCompressionFormat.None,
                                                Platform = ETexturePlatform.DesktopMobile,
                                                SocketFormat = ESocketFormat.Bone,
                                                ExportMorphTargets = true,
                                                ExportMaterials = true,
                                                ExportHdrTexturesAsHdr = true,
                                            };

                                            try
                                            {
                                                var exporter = new Exporter(obj, options);
                                                if (
                                                    exporter.TryWriteToDir(
                                                        new DirectoryInfo(
                                                            Path.GetFullPath(config.OutputDir)
                                                        ),
                                                        out var label,
                                                        out var savedFilePath
                                                    )
                                                )
                                                {
                                                    if (outputType == "gltf")
                                                    {
                                                        savedFilePath = ConvertGlbToGltf(savedFilePath, deleteSourceGlb: true);
                                                    }
                                                    else
                                                    {
                                                        SanitizeGlbForPreview(savedFilePath);
                                                    }
                                                    if (config.LogOutputs)
                                                        Console.WriteLine($"=> {savedFilePath}");
                                                    jobOutputs.Add(savedFilePath);
                                                    AppendExportManifest(config, file.Value.Path, obj, savedFilePath, "Model");
                                                    AppendAssetCatalog(config, BuildModelCatalogEntry(file.Value.Path, obj, savedFilePath));
                                                    exportedThisJob = true;
                                                    Interlocked.Increment(ref totalExportedFiles);
                                                    break;
                                                }

                                                string meshFailure = DescribeMeshExportFailure(obj, options);
                                                Console.WriteLine(
                                                    $"ERROR: Failed to export {file.Value.Path} as {outputType.ToUpperInvariant()}{(string.IsNullOrWhiteSpace(label) ? "" : $" ({label})")}{(meshFailure.Length > 0 ? $" [{meshFailure}]" : "")}."
                                                );
                                            }
                                            catch (Exception ex)
                                            {
                                                Console.WriteLine(
                                                    $"WARN: Skipped mesh {file.Value.Path} ({ex.Message})"
                                                );
                                            }
                                        }
                                    }
                                    else if (outputType is "ueanim" or "psa")
                                    {
                                        foreach (var obj in allObjects)
                                        {
                                            if (!MatchesAutoReferencedTarget(obj, exportJob.AutoReferencedRule))
                                                continue;

                                            if (obj is not UAnimSequence && obj is not UAnimMontage && obj is not UAnimComposite)
                                                continue;

                                            var animationAsset = (UAnimationAsset)obj;
                                            var objectOutputPath = BuildObjectOutputPath(outputPath, outputDir, fileName, obj, exportJob.AutoReferencedRule);
                                            AppendAnimationBinding(config, file.Value.Path, animationAsset, null, "discovered", null);
                                            if (NeedsAclNative(animationAsset) && !HasAclNativeExports())
                                            {
                                                const string error = "missingNativeFeature: ACL. CUE4Parse-Natives 没有编入 ACL，无法解压 ACL 压缩动画。请补齐 CUE4Parse-Natives/ACL/external/acl 后重建。";
                                                var diagnosticPath = objectOutputPath + "." + outputType + ".missing-acl.json";
                                                Console.WriteLine($"WARN: Skipped animation {file.Value.Path} ({error})");
                                                WriteAnimationDiagnostic(config, file.Value.Path, animationAsset, diagnosticPath, "blocked", error);
                                                AppendExportManifest(config, file.Value.Path, obj, diagnosticPath, "AnimationMetadata");
                                                AppendAssetCatalog(config, BuildAnimationCatalogEntry(file.Value.Path, obj, objectOutputPath + "." + outputType, outputType, "blocked", error));
                                                AppendAnimationBinding(config, file.Value.Path, animationAsset, null, "blocked", error);
                                                break;
                                            }

                                            var options = new ExporterOptions
                                            {
                                                AnimFormat = outputType == "ueanim" ? EAnimFormat.UEFormat : EAnimFormat.ActorX,
                                                CompressionFormat = EFileCompressionFormat.None,
                                            };

                                            try
                                            {
                                                var exporter = new Exporter(obj, options);
                                                if (
                                                    exporter.TryWriteToDir(
                                                        new DirectoryInfo(Path.GetFullPath(config.OutputDir)),
                                                        out var label,
                                                        out var savedFilePath
                                                    )
                                                )
                                                {
                                                    if (config.LogOutputs)
                                                        Console.WriteLine($"=> {savedFilePath}");
                                                    jobOutputs.Add(savedFilePath);
                                                    AppendExportManifest(config, file.Value.Path, obj, savedFilePath, "Animation");
                                                    AppendAssetCatalog(config, BuildAnimationCatalogEntry(file.Value.Path, obj, savedFilePath, outputType, "ok", null));
                                                    AppendAnimationBinding(config, file.Value.Path, animationAsset, savedFilePath, "ok", null);
                                                    exportedThisJob = true;
                                                    Interlocked.Increment(ref totalExportedFiles);
                                                    break;
                                                }

                                                Console.WriteLine(
                                                    $"ERROR: Failed to export {file.Value.Path} as {outputType}{(string.IsNullOrWhiteSpace(label) ? "" : $" ({label})")}."
                                                );
                                                var error = label ?? "exporter returned false";
                                                var metadataPath = objectOutputPath + "." + outputType + ".metadata.json";
                                                if (TryWriteAnimationMetadataSidecar(config, file.Value.Path, animationAsset, metadataPath, "metadata", error))
                                                {
                                                    AppendExportManifest(config, file.Value.Path, obj, metadataPath, "AnimationMetadata");
                                                    AppendAssetCatalog(config, BuildAnimationCatalogEntry(file.Value.Path, obj, metadataPath, "json", "metadata", error));
                                                    AppendAnimationBinding(config, file.Value.Path, animationAsset, metadataPath, "metadata", error);
                                                }
                                                else
                                                {
                                                    AppendAssetCatalog(config, BuildAnimationCatalogEntry(file.Value.Path, obj, objectOutputPath + "." + outputType, outputType, "error", error));
                                                    AppendAnimationBinding(config, file.Value.Path, animationAsset, null, "error", error);
                                                }
                                            }
                                            catch (Exception ex)
                                            {
                                                Console.WriteLine(
                                                    $"WARN: Skipped animation {file.Value.Path} ({ex.Message})"
                                                );
                                                var metadataPath = objectOutputPath + "." + outputType + ".metadata.json";
                                                if (TryWriteAnimationMetadataSidecar(config, file.Value.Path, animationAsset, metadataPath, "metadata", ex.Message))
                                                {
                                                    AppendExportManifest(config, file.Value.Path, obj, metadataPath, "AnimationMetadata");
                                                    AppendAssetCatalog(config, BuildAnimationCatalogEntry(file.Value.Path, obj, metadataPath, "json", "metadata", ex.Message));
                                                    AppendAnimationBinding(config, file.Value.Path, animationAsset, metadataPath, "metadata", ex.Message);
                                                }
                                                else
                                                {
                                                    AppendAssetCatalog(config, BuildAnimationCatalogEntry(file.Value.Path, obj, objectOutputPath + "." + outputType, outputType, "error", ex.Message));
                                                    AppendAnimationBinding(config, file.Value.Path, animationAsset, null, "error", ex.Message);
                                                }
                                            }
                                        }
                                    }

                                    // else if (outputType == "uexp")
                                    // {
                                    //     if (config.LogOutputs) Console.WriteLine("=> " + outputPath + ".uexp");
                                    //     if (provider.TrySavePackage(file.Value, out var assets))
                                    //     {
                                    //         Parallel.ForEach(assets, kvp =>
                                    //         {
                                    //             lock (new object())
                                    //             {
                                    //                 if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);
                                    //                 File.WriteAllBytes(outputPath + ".uexp", kvp.Value);
                                    //             }
                                    //         });
                                    //     }
                                    //     Interlocked.Increment(ref totalExportedFiles);
                                    // }

                                    break;
                                }
                                case "locres":
                                {
                                    if (
                                        outputType == "json"
                                        && provider.TryCreateReader(file.Value.Path, out var archive)
                                    )
                                    {
                                        if (config.LogOutputs)
                                            Console.WriteLine("=> " + outputPath + ".json");
                                        var locres = new FTextLocalizationResource(archive);
                                        var json = JsonConvert.SerializeObject(
                                            locres,
                                            Formatting.Indented
                                        );
                                        if (!Directory.Exists(outputDir))
                                            Directory.CreateDirectory(outputDir);
                                        var locresJsonPath = outputPath + ".json";
                                        File.WriteAllText(locresJsonPath, json);
                                        jobOutputs.Add(locresJsonPath);
                                        AppendExportManifest(config, file.Value.Path, null, locresJsonPath, "Localization");
                                        exportedThisJob = true;
                                        Interlocked.Increment(ref totalExportedFiles);
                                    }
                                    break;
                                }
                                case "js":
                                {
                                    if (
                                        outputType == fileType
                                        && provider.TrySaveAsset(file.Value.Path, out var data)
                                    )
                                    {
                                        if (config.LogOutputs)
                                            Console.WriteLine("=> " + outputPath + "." + outputType);
                                        using var stream = new MemoryStream(data) { Position = 0 };
                                        using var reader = new StreamReader(stream);
                                        JSBeautifyOptions options = new() { };
                                        JSBeautify beautifier = new(reader.ReadToEnd(), options);
                                        if (!Directory.Exists(outputDir))
                                            Directory.CreateDirectory(outputDir);
                                        var jsPath = outputPath + ".js";
                                        File.WriteAllText(jsPath, beautifier.GetResult());
                                        jobOutputs.Add(jsPath);
                                        AppendExportManifest(config, file.Value.Path, null, jsPath, "Js");
                                        exportedThisJob = true;
                                        Interlocked.Increment(ref totalExportedFiles);
                                    }
                                    break;
                                }
                                case "db":
                                {
                                    if (
                                        outputType == fileType
                                        && provider.TrySaveAsset(file.Value.Path, out var data)
                                    )
                                    {
                                        if (config.LogOutputs)
                                            Console.WriteLine("=> " + outputPath + "." + outputType);
                                        using var stream = new MemoryStream(data) { Position = 0 };
                                        using var reader = new StreamReader(stream);
                                        if (!Directory.Exists(outputDir))
                                            Directory.CreateDirectory(outputDir);
                                        var dbPath = outputPath + ".db";
                                        File.WriteAllBytes(dbPath, data);
                                        jobOutputs.Add(dbPath);
                                        AppendExportManifest(config, file.Value.Path, null, dbPath, "Database");
                                        exportedThisJob = true;
                                        Interlocked.Increment(ref totalExportedFiles);
                                    }
                                    break;
                                }
                            }
                        }
                        catch (AggregateException ae)
                        {
                            Console.WriteLine(ae.Message);
                            // Console.WriteLine($"ERROR: File cannot be opened: {file.Value.Path}. Possible issues include incorrect UE version in config.json, missing mapping file, or this file type is not supported.");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(
                                $"ERROR: Failed to export {file.Value.Path}: {ex.Message}"
                            );
                        }

                        if (resumeExports && exportedThisJob)
                        {
                            exportResumeStore!.MarkExported(
                                resumeKey,
                                file.Value.Path,
                                file.Value.Size,
                                outputType,
                                exportJob.AutoReferencedRule,
                                jobOutputs.ToArray());
                        }

                        if (exportJob.AutoReferencedRule != null)
                        {
                            AppendAutoReferencedExportDiagnostic(
                                config,
                                BuildAutoReferencedExportDiagnostic(
                                    "export",
                                    exportedThisJob ? "exported" : "notExported",
                                    exportJob.AutoReferencedRule.RelationType,
                                    exportJob.AutoReferencedRule.TargetPath,
                                    exportJob.AutoReferencedRule.SourcePath,
                                    exportJob.AutoReferencedRule.OutputType,
                                    exportedThisJob
                                        ? (exportJob.MatchedByConfig ? "coveredByConfig" : null)
                                        : "matchedPackageButNoExportedObject"));
                        }

                        Interlocked.Increment(ref totalRegexMatches);
                    }
                }
            }
        );

        // Create checkpoint
        if (config.CreateNewCheckpoint)
            CreateCheckpoint(newCheckpointDict, config);

        // Log results
        if (config.LogOutputs && totalExportedFiles > 0 && !config.CreateNewCheckpoint)
            Console.WriteLine();
        Console.WriteLine(
            $"Scanned {provider.Files.Count} files{(useCheckpoint ? $" ({totalChangedFiles} changed, {provider.Files.Count - totalChangedFiles} unchanged)" : "")}"
        );
        var incompatibleOrNoOutputJobs = Math.Max(0, totalRegexMatches - totalExportedFiles - totalResumedExportJobs);
        Console.WriteLine(
            $"Regex matched {totalRegexMatches} files {(incompatibleOrNoOutputJobs > 0 ? $"(skipped {incompatibleOrNoOutputJobs} incompatible/no-output job(s))" : "")}"
        );
        if (resumeExports)
            Console.WriteLine($"Resume skipped {totalResumedExportJobs} completed export job(s)");
        Console.WriteLine(
            $"Exported {totalExportedFiles} files in {Elapsed(start, Now(), 1000)} seconds"
        );
        if (config.GenerateLibraryIndexes)
        {
            UELibraryPostProcessor.Run(config.OutputDir, config.UseSharedTextures);
        }
        else if (config.UseSharedTextures)
        {
            UELibraryPostProcessor.DeduplicateTextureFiles(Path.GetFullPath(config.OutputDir));
        }
        Console.WriteLine();
    }

    private static Dictionary<string, AutoReferencedExportRule[]> BuildAutoReferencedExportRules(
        AbstractFileProvider provider,
        ConfigObj config)
    {
        if (!ShouldAutoExportReferencedAssets(config))
            return new Dictionary<string, AutoReferencedExportRule[]>(StringComparer.OrdinalIgnoreCase);

        var dbPath = Path.Combine(Path.GetFullPath(config.OutputDir), "ue_source_index.db");
        if (!File.Exists(dbPath))
        {
            Console.WriteLine($"WARN: auto referenced export skipped because source index is missing: {dbPath}");
            return new Dictionary<string, AutoReferencedExportRule[]>(StringComparer.OrdinalIgnoreCase);
        }

        var autoRuleStart = Now();
        Console.WriteLine("Building auto referenced export rules...");
        var packageFiles = BuildPackageFileLookup(provider);
        var providerFilesByPath = BuildProviderPathLookup(provider);
        Console.WriteLine($"Auto referenced lookup prepared: packages={packageFiles.Count}, providerPaths={providerFilesByPath.Count}, elapsed={Elapsed(autoRuleStart, Now(), 1000)}s");
        var rules = new Dictionary<string, List<AutoReferencedExportRule>>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<object>();
        var unresolved = 0;
        var ambiguous = 0;

        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        var stageStart = Now();
        command.CommandText = """
            SELECT DISTINCT relation_type, target_path
            FROM component_asset_relations
            WHERE target_path IS NOT NULL
              AND target_path != ''
              AND relation_type IN (
                  'StaticMesh', 'SkeletalMesh', 'Material', 'Texture',
                  'Animation', 'AnimClass', 'AnimBlueprintGeneratedClass', 'Skeleton'
              );
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var relationType = reader.GetString(0);
            var targetPath = reader.GetString(1);
            if (relationType.Equals("Skeleton", StringComparison.OrdinalIgnoreCase))
            {
                unresolved += AddSkeletonMeshExportRules(connection, providerFilesByPath, targetPath, rules, diagnostics);
                continue;
            }

            var outputType = InferOutputTypeForReferencedAsset(relationType, targetPath);
            if (outputType == null)
                continue;

            var suffix = BuildPackageFileSuffix(targetPath);
            if (string.IsNullOrWhiteSpace(suffix))
            {
                unresolved++;
                diagnostics.Add(BuildAutoReferencedExportDiagnostic("plan", "unresolved", relationType, targetPath, null, outputType, "emptyPackageSuffix"));
                continue;
            }

            if (!packageFiles.TryGetValue(suffix, out var matches) || matches.Length == 0)
            {
                unresolved++;
                diagnostics.Add(BuildAutoReferencedExportDiagnostic("plan", "unresolved", relationType, targetPath, null, outputType, "sourcePackageNotFound"));
                continue;
            }

            if (matches.Length > 1)
            {
                ambiguous++;
                diagnostics.Add(BuildAutoReferencedExportDiagnostic("plan", "ambiguous", relationType, targetPath, null, outputType, $"matchedPackages={matches.Length}"));
                continue;
            }

            var filePath = matches[0];
            AddAutoReferencedExportRule(rules, diagnostics, relationType, targetPath, filePath, outputType);
        }
        Console.WriteLine($"Auto referenced component relations done: rules={rules.Sum(x => x.Value.Count)}, elapsed={Elapsed(stageStart, Now(), 1000)}s");

        stageStart = Now();
        var sourceRelationResult = AddSourceRelationExportRules(connection, packageFiles, providerFilesByPath, rules, diagnostics);
        unresolved += sourceRelationResult.Unresolved;
        ambiguous += sourceRelationResult.Ambiguous;
        Console.WriteLine($"Auto referenced source relations done: rules={rules.Sum(x => x.Value.Count)}, elapsed={Elapsed(stageStart, Now(), 1000)}s");

        stageStart = Now();
        var materialTextureResult = AddMaterialTextureExportRules(connection, packageFiles, rules, diagnostics);
        unresolved += materialTextureResult.Unresolved;
        ambiguous += materialTextureResult.Ambiguous;
        Console.WriteLine($"Auto referenced material textures done: rules={rules.Sum(x => x.Value.Count)}, elapsed={Elapsed(stageStart, Now(), 1000)}s");

        if (ShouldAutoExportCompatibleAnimations(config))
        {
            stageStart = Now();
            unresolved += AddIndexedAnimationExportRules(connection, packageFiles, rules, diagnostics);
            Console.WriteLine($"Auto referenced compatible animations done: rules={rules.Sum(x => x.Value.Count)}, elapsed={Elapsed(stageStart, Now(), 1000)}s");
        }

        stageStart = Now();
        unresolved += AddAnimationSegmentExportRules(connection, packageFiles, rules, diagnostics);
        Console.WriteLine($"Auto referenced animation segments done: rules={rules.Sum(x => x.Value.Count)}, elapsed={Elapsed(stageStart, Now(), 1000)}s");

        stageStart = Now();
        WriteAutoReferencedExportDiagnostics(config, diagnostics);
        Console.WriteLine($"Auto referenced diagnostics written: rows={diagnostics.Count}, elapsed={Elapsed(stageStart, Now(), 1000)}s");

        Console.WriteLine(
            $"Auto referenced exports: {rules.Sum(x => x.Value.Count)} rule(s)" +
            (unresolved > 0 || ambiguous > 0 ? $" ({unresolved} unresolved, {ambiguous} ambiguous)" : "") +
            $", elapsed={Elapsed(autoRuleStart, Now(), 1000)}s");
        return rules.ToDictionary(
            x => x.Key,
            x => x.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static (int Unresolved, int Ambiguous) AddSourceRelationExportRules(
        SqliteConnection connection,
        Dictionary<string, string[]> packageFiles,
        Dictionary<string, string> providerFilesByPath,
        Dictionary<string, List<AutoReferencedExportRule>> rules,
        List<object> diagnostics)
    {
        if (!TableExists(connection, "source_relations"))
            return (0, 0);

        var unresolved = 0;
        var ambiguous = 0;
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT relation_type, target_path
            FROM source_relations
            WHERE target_path IS NOT NULL
              AND target_path != ''
              AND relation_type IN ('Material', 'Texture', 'Animation', 'Skeleton');
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var relationType = reader.GetString(0);
            var targetPath = reader.GetString(1);
            if (relationType.Equals("Skeleton", StringComparison.OrdinalIgnoreCase))
            {
                unresolved += AddSkeletonMeshExportRules(connection, providerFilesByPath, targetPath, rules, diagnostics);
                continue;
            }

            var outputType = InferOutputTypeForReferencedAsset(relationType, targetPath);
            if (outputType == null)
                continue;

            var result = TryAddPackageBackedAutoRule(packageFiles, rules, diagnostics, relationType, targetPath, outputType);
            unresolved += result.Unresolved;
            ambiguous += result.Ambiguous;
        }

        return (unresolved, ambiguous);
    }

    private static (int Unresolved, int Ambiguous) TryAddPackageBackedAutoRule(
        Dictionary<string, string[]> packageFiles,
        Dictionary<string, List<AutoReferencedExportRule>> rules,
        List<object> diagnostics,
        string relationType,
        string targetPath,
        string outputType)
    {
        var suffix = BuildPackageFileSuffix(targetPath);
        if (string.IsNullOrWhiteSpace(suffix))
        {
            diagnostics.Add(BuildAutoReferencedExportDiagnostic("plan", "unresolved", relationType, targetPath, null, outputType, "emptyPackageSuffix"));
            return (1, 0);
        }

        if (!packageFiles.TryGetValue(suffix, out var matches) || matches.Length == 0)
        {
            diagnostics.Add(BuildAutoReferencedExportDiagnostic("plan", "unresolved", relationType, targetPath, null, outputType, "sourcePackageNotFound"));
            return (1, 0);
        }

        if (matches.Length > 1)
        {
            diagnostics.Add(BuildAutoReferencedExportDiagnostic("plan", "ambiguous", relationType, targetPath, null, outputType, $"matchedPackages={matches.Length}"));
            return (0, 1);
        }

        AddAutoReferencedExportRule(rules, diagnostics, relationType, targetPath, matches[0], outputType);
        return (0, 0);
    }

    private static bool ShouldAutoExportReferencedAssets(ConfigObj config)
    {
        if (config.AutoExportReferencedAssets.HasValue)
            return config.AutoExportReferencedAssets.Value;

        // 素材库导出需要从完整源索引补齐显式引用，避免组合模型、材质贴图或动画只导出一半。
        return config.GenerateLibraryIndexes && config.GenerateSourceIndex;
    }

    private static bool ShouldAutoExportCompatibleAnimations(ConfigObj config)
    {
        // 只靠同 Skeleton 兼容不足以证明动画属于当前模型，默认只补显式引用和动画片段。
        return config.AutoExportCompatibleAnimations == true;
    }

    private static int AddSkeletonMeshExportRules(
        SqliteConnection connection,
        Dictionary<string, string> providerFilesByPath,
        string skeletonPath,
        Dictionary<string, List<AutoReferencedExportRule>> rules,
        List<object> diagnostics)
    {
        var unresolved = 0;
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT source_path, owner_object_path
            FROM skeleton_bones
            WHERE skeleton_path = $skeletonPath
              AND owner_type = 'SkeletalMesh'
              AND source_path IS NOT NULL
              AND source_path != ''
              AND owner_object_path IS NOT NULL
              AND owner_object_path != '';
            """;
        command.Parameters.AddWithValue("$skeletonPath", skeletonPath);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var sourcePath = NormalizeAssetPath(reader.GetString(0));
            var meshObjectPath = reader.GetString(1);
            if (!providerFilesByPath.TryGetValue(sourcePath, out var filePath))
            {
                unresolved++;
                diagnostics.Add(BuildAutoReferencedExportDiagnostic("plan", "unresolved", "Skeleton", skeletonPath, sourcePath, "glb", "skeletonMeshSourceNotFound"));
                continue;
            }

            AddAutoReferencedExportRule(rules, diagnostics, "SkeletonMesh", meshObjectPath, filePath, "glb");
        }

        return unresolved;
    }

    private static (int Unresolved, int Ambiguous) AddMaterialTextureExportRules(
        SqliteConnection connection,
        Dictionary<string, string[]> packageFiles,
        Dictionary<string, List<AutoReferencedExportRule>> rules,
        List<object> diagnostics)
    {
        if (!TableExists(connection, "material_texture_slots"))
            return (0, 0);

        var unresolved = 0;
        var ambiguous = 0;
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT texture_object_path
            FROM material_texture_slots
            WHERE texture_object_path IS NOT NULL
              AND texture_object_path != '';
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var texturePath = reader.GetString(0);
            var suffix = BuildPackageFileSuffix(texturePath);
            if (string.IsNullOrWhiteSpace(suffix))
            {
                unresolved++;
                diagnostics.Add(BuildAutoReferencedExportDiagnostic("plan", "unresolved", "MaterialTextureSlot", texturePath, null, "png", "emptyPackageSuffix"));
                continue;
            }

            if (!packageFiles.TryGetValue(suffix, out var matches) || matches.Length == 0)
            {
                unresolved++;
                diagnostics.Add(BuildAutoReferencedExportDiagnostic("plan", "unresolved", "MaterialTextureSlot", texturePath, null, "png", "sourcePackageNotFound"));
                continue;
            }

            if (matches.Length > 1)
            {
                ambiguous++;
                diagnostics.Add(BuildAutoReferencedExportDiagnostic("plan", "ambiguous", "MaterialTextureSlot", texturePath, null, "png", $"matchedPackages={matches.Length}"));
                continue;
            }

            var filePath = matches[0];
            AddAutoReferencedExportRule(rules, diagnostics, "MaterialTextureSlot", texturePath, filePath, "png");
        }

        return (unresolved, ambiguous);
    }

    private static int AddAnimationSegmentExportRules(
        SqliteConnection connection,
        Dictionary<string, string[]> packageFiles,
        Dictionary<string, List<AutoReferencedExportRule>> rules,
        List<object> diagnostics)
    {
        if (!TableExists(connection, "animation_segments"))
            return 0;

        var unresolved = 0;
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT animation_object_path
            FROM animation_segments
            WHERE animation_object_path IS NOT NULL
              AND animation_object_path != ''
            UNION
            SELECT DISTINCT referenced_animation_path
            FROM animation_segments
            WHERE referenced_animation_path IS NOT NULL
              AND referenced_animation_path != '';
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var filePath = FindProviderFilePathByObjectPath(packageFiles, reader.GetString(0));
            if (filePath == null)
            {
                unresolved++;
                diagnostics.Add(BuildAutoReferencedExportDiagnostic("plan", "unresolved", "AnimationSegment", reader.GetString(0), null, "ueanim", "segmentAnimationSourceNotFound"));
                continue;
            }

            AddAutoReferencedExportRule(rules, diagnostics, "AnimationSegment", reader.GetString(0), filePath, "ueanim");
        }

        return unresolved;
    }

    private static int AddIndexedAnimationExportRules(
        SqliteConnection connection,
        Dictionary<string, string[]> packageFiles,
        Dictionary<string, List<AutoReferencedExportRule>> rules,
        List<object> diagnostics)
    {
        if (!TableExists(connection, "source_objects"))
            return 0;

        var unresolved = 0;
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH model_skeletons AS (
                SELECT DISTINCT skeleton_path
                FROM source_objects
                WHERE skeleton_path IS NOT NULL
                  AND skeleton_path != ''
                  AND (
                      object_type = 'USkeletalMesh'
                      OR export_type = 'SkeletalMesh'
                  )
            )
            SELECT DISTINCT object_path
            FROM source_objects
            WHERE object_path IS NOT NULL
              AND object_path != ''
              AND skeleton_path IN (SELECT skeleton_path FROM model_skeletons)
              AND (
                  object_type IN ('UAnimSequence', 'UAnimMontage', 'UAnimComposite')
                  OR export_type IN ('AnimSequence', 'AnimMontage', 'AnimComposite')
              );
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var animationPath = reader.GetString(0);
            var filePath = FindProviderFilePathByObjectPath(packageFiles, animationPath);
            if (filePath == null)
            {
                unresolved++;
                diagnostics.Add(BuildAutoReferencedExportDiagnostic("plan", "unresolved", "Animation", animationPath, null, "ueanim", "indexedAnimationSourceNotFound"));
                continue;
            }

            AddAutoReferencedExportRule(rules, diagnostics, "Animation", animationPath, filePath, "ueanim");
        }

        return unresolved;
    }

    private static void AddAutoReferencedExportRule(
        Dictionary<string, List<AutoReferencedExportRule>> rules,
        List<object> diagnostics,
        string relationType,
        string targetPath,
        string sourcePath,
        string outputType)
    {
        var rule = new AutoReferencedExportRule(
            sourcePath,
            $"{Regex.Escape(sourcePath)}:{outputType}",
            relationType,
            targetPath,
            outputType);
        var sourceKey = NormalizeAssetPath(sourcePath);
        if (!rules.TryGetValue(sourceKey, out var sourceRules))
        {
            sourceRules = [];
            rules[sourceKey] = sourceRules;
        }

        if (sourceRules.Any(x =>
                x.OutputType.Equals(outputType, StringComparison.OrdinalIgnoreCase) &&
                x.TargetPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase)))
            return;

        sourceRules.Add(rule);
        diagnostics.Add(BuildAutoReferencedExportDiagnostic(
            "plan",
            "planned",
            relationType,
            targetPath,
            sourcePath,
            outputType,
            null));
    }

    private static object BuildAutoReferencedExportDiagnostic(
        string stage,
        string status,
        string relationType,
        string targetPath,
        string? sourcePath,
        string? outputType,
        string? reason)
        => new
        {
            stage,
            status,
            relationType,
            targetPath,
            source = sourcePath,
            outputType,
            reason,
        };

    private static void WriteAutoReferencedExportDiagnostics(ConfigObj config, List<object> diagnostics)
    {
        ReplaceExportEventSqliteRows(config, "auto_referenced_exports", diagnostics.Select(JObject.FromObject));
        var path = Path.Combine(Path.GetFullPath(config.OutputDir), "auto_referenced_exports.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = diagnostics.Select(JsonConvert.SerializeObject).ToArray();
        File.WriteAllLines(path, lines);
    }

    private static void AppendAutoReferencedExportDiagnostic(ConfigObj config, object diagnostic)
    {
        WriteExportEventSqlite(config, "auto_referenced_exports", JObject.FromObject(diagnostic));
        var path = Path.Combine(Path.GetFullPath(config.OutputDir), "auto_referenced_exports.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (AutoReferencedWriteLock)
        {
            File.AppendAllText(path, JsonConvert.SerializeObject(diagnostic) + Environment.NewLine);
        }
    }

    private sealed class AutoReferencedExportRule
    {
        public AutoReferencedExportRule(
            string sourcePath,
            string rule,
            string relationType,
            string targetPath,
            string outputType)
        {
            SourcePath = sourcePath;
            Rule = rule;
            RelationType = relationType;
            TargetPath = targetPath;
            OutputType = outputType;
        }

        public string SourcePath { get; }

        public string Rule { get; }

        public string RelationType { get; }

        public string TargetPath { get; }

        public string OutputType { get; }
    }

    private sealed class ExportJob
    {
        public ExportJob(string outputType, bool matchedByConfig, AutoReferencedExportRule? autoReferencedRule)
        {
            OutputType = outputType;
            MatchedByConfig = matchedByConfig;
            AutoReferencedRule = autoReferencedRule;
        }

        public string OutputType { get; }

        public bool MatchedByConfig { get; }

        public AutoReferencedExportRule? AutoReferencedRule { get; }
    }

    private static List<ExportJob> BuildExportJobs(
        IEnumerable<string> regexMatches,
        bool matchedByConfig,
        bool explicitRulesAreActive,
        AutoReferencedExportRule[]? autoReferencedRules)
    {
        var jobs = new List<ExportJob>();
        foreach (var regexMatch in regexMatches)
        {
            var outputType = regexMatch.SubstringAfterLast(':').ToLowerInvariant();
            if (outputType.Length == 0 || jobs.Any(x => x.AutoReferencedRule == null && x.OutputType.Equals(outputType, StringComparison.OrdinalIgnoreCase)))
                continue;

            jobs.Add(new ExportJob(outputType, matchedByConfig, null));
        }

        foreach (var rule in autoReferencedRules ?? [])
        {
            if (matchedByConfig && explicitRulesAreActive && IsCoveredByExplicitJob(jobs, rule.OutputType))
                continue;

            // 自动补导必须逐对象执行；同一个 uasset 里可能同时塞了多个贴图或动画。
            if (jobs.Any(x =>
                    x.AutoReferencedRule != null &&
                    x.OutputType.Equals(rule.OutputType, StringComparison.OrdinalIgnoreCase) &&
                    x.AutoReferencedRule.TargetPath.Equals(rule.TargetPath, StringComparison.OrdinalIgnoreCase)))
                continue;

            jobs.Add(new ExportJob(rule.OutputType.ToLowerInvariant(), matchedByConfig, rule));
        }

        return jobs;
    }

    private static bool IsCoveredByExplicitJob(List<ExportJob> jobs, string outputType)
    {
        if (jobs.Any(x => x.AutoReferencedRule == null && x.OutputType.Equals(outputType, StringComparison.OrdinalIgnoreCase)))
            return true;

        return IsModelOutput(outputType) &&
               jobs.Any(x => x.AutoReferencedRule == null && IsModelOutput(x.OutputType));
    }

    private static bool IsModelOutput(string outputType)
        => outputType.Equals("glb", StringComparison.OrdinalIgnoreCase) ||
           outputType.Equals("gltf", StringComparison.OrdinalIgnoreCase);

    private static bool MatchesAutoReferencedTarget(UObject obj, AutoReferencedExportRule? rule)
    {
        if (rule == null)
            return true;

        var targetPath = NormalizeObjectPath(rule.TargetPath);
        var objectPath = NormalizeObjectPath(obj.GetPathName());
        if (objectPath.Equals(targetPath, StringComparison.OrdinalIgnoreCase))
            return true;

        var targetName = GetObjectNameFromPath(targetPath);
        return targetName.Length > 0 && obj.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildObjectOutputPath(
        string defaultOutputPath,
        string outputDir,
        string packageFileName,
        UObject obj,
        AutoReferencedExportRule? rule)
    {
        if (rule == null)
            return defaultOutputPath;

        var objectName = SanitizeOutputName(GetObjectNameFromPath(rule.TargetPath));
        if (objectName.Length == 0)
            objectName = SanitizeOutputName(obj.Name);

        if (objectName.Length == 0 || objectName.Equals(packageFileName, StringComparison.OrdinalIgnoreCase))
            return defaultOutputPath;

        return Path.Combine(outputDir, objectName);
    }

    private static string GetObjectNameFromPath(string objectPath)
    {
        var normalized = NormalizeObjectPath(objectPath);
        var dotIndex = normalized.LastIndexOf('.');
        if (dotIndex >= 0 && dotIndex + 1 < normalized.Length)
            return normalized[(dotIndex + 1)..];

        var slashIndex = normalized.LastIndexOf('/');
        return slashIndex >= 0 && slashIndex + 1 < normalized.Length
            ? normalized[(slashIndex + 1)..]
            : normalized;
    }

    private static string SanitizeOutputName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars).Trim();
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $tableName LIMIT 1;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return command.ExecuteScalar() != null;
    }

    private static Dictionary<string, string[]> BuildPackageFileLookup(AbstractFileProvider provider)
    {
        var rows = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in provider.Files.Values)
        {
            if (!file.Path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) &&
                !file.Path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
                continue;

            var withoutExtension = NormalizeAssetPath(Path.ChangeExtension(file.Path, null));
            foreach (var suffix in BuildProviderFileSuffixes(withoutExtension))
            {
                if (!rows.TryGetValue(suffix, out var list))
                {
                    list = [];
                    rows[suffix] = list;
                }

                list.Add(file.Path);
            }
        }

        return rows.ToDictionary(
            x => x.Key,
            x => x.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> BuildProviderPathLookup(AbstractFileProvider provider)
        => provider.Files.Values
            .Where(x => x.IsUePackage)
            .GroupBy(x => NormalizeAssetPath(x.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Path, StringComparer.OrdinalIgnoreCase);

    private static string? FindProviderFilePathByObjectPath(Dictionary<string, string[]> packageFiles, string objectPath)
    {
        var suffix = BuildPackageFileSuffix(objectPath);
        if (string.IsNullOrWhiteSpace(suffix))
            return null;

        return packageFiles.TryGetValue(suffix, out var matches) && matches.Length > 0
            ? matches[0]
            : null;
    }

    private static IEnumerable<string> BuildProviderFileSuffixes(string withoutExtension)
    {
        yield return withoutExtension;

        var contentIndex = withoutExtension.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (contentIndex >= 0)
        {
            yield return withoutExtension[contentIndex..].TrimStart('/');
            // UE 插件资源的虚拟路径通常是 /PluginName/...，pak 内实际路径会带 PluginName/Content/...
            var ownerStart = withoutExtension.LastIndexOf('/', Math.Max(0, contentIndex - 1));
            if (ownerStart >= 0 && ownerStart + 1 < contentIndex)
                yield return withoutExtension[(ownerStart + 1)..];
        }

        if (withoutExtension.StartsWith("Engine/Content/", StringComparison.OrdinalIgnoreCase))
            yield return withoutExtension;
    }

    private static string? InferOutputTypeForReferencedAsset(string relationType, string targetPath)
    {
        if (relationType.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase) ||
            relationType.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase))
            return "glb";

        if (relationType.Equals("Material", StringComparison.OrdinalIgnoreCase))
            return "json";

        if (relationType.Equals("Texture", StringComparison.OrdinalIgnoreCase))
            return "png";

        if (relationType.Equals("Animation", StringComparison.OrdinalIgnoreCase))
            return "ueanim";

        // AnimClass/AnimBlueprintGeneratedClass 是运行时类或蓝图，不是可直接播放动画；保留 JSON 便于后续重建动画蓝图关系。
        if (relationType.Equals("AnimClass", StringComparison.OrdinalIgnoreCase) ||
            relationType.Equals("AnimBlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase))
            return "json";

        return null;
    }

    private static string? BuildPackageFileSuffix(string objectPath)
    {
        var path = NormalizeObjectPath(objectPath);
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase))
            return null;

        var dotIndex = path.LastIndexOf('.');
        if (dotIndex > 0)
            path = path[..dotIndex];

        if (path.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            return "Content/" + path["/Game/".Length..];

        if (path.StartsWith("/Engine/", StringComparison.OrdinalIgnoreCase))
            return "Engine/Content/" + path["/Engine/".Length..];

        var pluginPath = path.TrimStart('/');
        var slashIndex = pluginPath.IndexOf('/');
        // 非 /Game、/Engine 的 mount point 按 UE 插件 Content 路径尝试匹配。
        if (slashIndex > 0 && !pluginPath.Contains("/Content/", StringComparison.OrdinalIgnoreCase))
            return pluginPath[..slashIndex] + "/Content/" + pluginPath[(slashIndex + 1)..];

        return pluginPath;
    }

    private static string NormalizeObjectPath(string path)
        => path.Replace('\\', '/').Trim();

    private static string NormalizeAssetPath(string path)
        => path.Replace('\\', '/').Trim();

    private static bool ShouldResumeExports(ConfigObj config)
        => config.ResumeExports != false;

    private static string BuildExportResumeKey(
        string sourcePath,
        string outputType,
        AutoReferencedExportRule? rule)
    {
        var raw = string.Join(
            "|",
            "v1",
            NormalizeAssetPath(sourcePath).ToLowerInvariant(),
            outputType.ToLowerInvariant(),
            rule == null ? "explicit" : NormalizeObjectPath(rule.TargetPath).ToLowerInvariant());
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static void AppendExportManifest(
        ConfigObj config,
        string sourcePath,
        UObject? obj,
        string outputPath,
        string kind
    )
    {
        var manifestPath = Path.Combine(Path.GetFullPath(config.OutputDir), "export_manifest.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var entry = new
        {
            exportedAt = DateTime.UtcNow.ToString("O"),
            gameTitle = config.GameTitle,
            kind,
            source = sourcePath,
            objectType = obj?.GetType().Name,
            name = obj?.Name,
            objectPath = obj?.GetPathName(),
            output = Path.GetFullPath(outputPath),
        };
        WriteExportEventSqlite(config, "export_manifest", JObject.FromObject(entry));
        lock (ManifestWriteLock)
        {
            File.AppendAllText(manifestPath, JsonConvert.SerializeObject(entry) + Environment.NewLine);
        }
    }

    private static void AppendAssetCatalog(ConfigObj config, object entry)
    {
        WriteExportEventSqlite(config, "asset_catalog", JObject.FromObject(entry));
        var catalogPath = Path.Combine(Path.GetFullPath(config.OutputDir), "asset_catalog.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
        lock (CatalogWriteLock)
        {
            File.AppendAllText(catalogPath, JsonConvert.SerializeObject(entry) + Environment.NewLine);
        }
    }

    private static void WriteExportEventSqlite(ConfigObj config, string tableName, JObject row)
    {
        var writer = ExportEventWriters.GetOrAdd(
            Path.GetFullPath(config.OutputDir),
            outputDir => ExportEventSqliteWriter.Open(outputDir));
        writer.Insert(tableName, row);
    }

    private static void ReplaceExportEventSqliteRows(ConfigObj config, string tableName, IEnumerable<JObject> rows)
    {
        var writer = ExportEventWriters.GetOrAdd(
            Path.GetFullPath(config.OutputDir),
            outputDir => ExportEventSqliteWriter.Open(outputDir));
        writer.Replace(tableName, rows);
    }

    private static void FlushExportEventWriters()
    {
        foreach (var pair in ExportEventWriters.ToArray())
        {
            if (!ExportEventWriters.TryRemove(pair.Key, out var writer))
                continue;

            writer.Dispose();
        }
    }

    private sealed class ExportEventSqliteWriter : IDisposable
    {
        private readonly object _lock = new();
        private readonly SqliteConnection _connection;

        private ExportEventSqliteWriter(SqliteConnection connection)
        {
            _connection = connection;
        }

        public static ExportEventSqliteWriter Open(string outputDir)
        {
            Directory.CreateDirectory(outputDir);
            SQLitePCL.Batteries_V2.Init();
            var dbPath = Path.Combine(outputDir, "export_events.db");
            var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            ExecuteExportEventSql(connection, "PRAGMA busy_timeout = 10000;");
            ExecuteExportEventSql(connection, "PRAGMA journal_mode = WAL;");
            ExecuteExportEventSql(connection, "PRAGMA synchronous = NORMAL;");
            EnsureSchema(connection);
            return new ExportEventSqliteWriter(connection);
        }

        public void Insert(string tableName, JObject row)
        {
            lock (_lock)
            {
                InsertCore(tableName, row);
            }
        }

        public void Replace(string tableName, IEnumerable<JObject> rows)
        {
            lock (_lock)
            {
                using var transaction = _connection.BeginTransaction();
                using (var delete = _connection.CreateCommand())
                {
                    delete.Transaction = transaction;
                    delete.CommandText = $"DELETE FROM {ValidateExportEventTableName(tableName)};";
                    delete.ExecuteNonQuery();
                }

                foreach (var row in rows)
                    InsertCore(tableName, row, transaction);

                transaction.Commit();
            }
        }

        private void InsertCore(string tableName, JObject row, SqliteTransaction? transaction = null)
        {
            switch (ValidateExportEventTableName(tableName))
            {
                case "export_manifest":
                    InsertExportManifest(row, transaction);
                    break;
                case "asset_catalog":
                    InsertAssetCatalog(row, transaction);
                    break;
                case "animation_bindings":
                    InsertAnimationBinding(row, transaction);
                    break;
                case "auto_referenced_exports":
                    InsertAutoReferencedExport(row, transaction);
                    break;
            }
        }

        private void InsertExportManifest(JObject row, SqliteTransaction? transaction)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO export_manifest (
                    exported_at, game_title, kind, source, object_type, name, object_path, output, raw_json
                )
                VALUES (
                    $exportedAt, $gameTitle, $kind, $source, $objectType, $name, $objectPath, $output, $rawJson
                );
                """;
            Add(command, "$exportedAt", (string?)row["exportedAt"]);
            Add(command, "$gameTitle", (string?)row["gameTitle"]);
            Add(command, "$kind", (string?)row["kind"]);
            Add(command, "$source", (string?)row["source"]);
            Add(command, "$objectType", (string?)row["objectType"]);
            Add(command, "$name", (string?)row["name"]);
            Add(command, "$objectPath", (string?)row["objectPath"]);
            Add(command, "$output", (string?)row["output"]);
            Add(command, "$rawJson", row.ToString(Formatting.None));
            command.ExecuteNonQuery();
        }

        private void InsertAssetCatalog(JObject row, SqliteTransaction? transaction)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO asset_catalog (
                    kind, resource_kind, name, source_type, source, object_path, output, format,
                    skeleton_path, skeleton_name, validation_status, status, raw_json
                )
                VALUES (
                    $kind, $resourceKind, $name, $sourceType, $source, $objectPath, $output, $format,
                    $skeletonPath, $skeletonName, $validationStatus, $status, $rawJson
                );
                """;
            Add(command, "$kind", (string?)row["kind"]);
            Add(command, "$resourceKind", (string?)row["resourceKind"]);
            Add(command, "$name", (string?)row["name"]);
            Add(command, "$sourceType", (string?)row["sourceType"]);
            Add(command, "$source", (string?)row["source"]);
            Add(command, "$objectPath", (string?)row["objectPath"]);
            Add(command, "$output", (string?)row["output"]);
            Add(command, "$format", (string?)row["format"]);
            Add(command, "$skeletonPath", (string?)row["skeletonPath"]);
            Add(command, "$skeletonName", (string?)row["skeletonName"]);
            Add(command, "$validationStatus", (string?)row["validationStatus"]);
            Add(command, "$status", (string?)row["status"]);
            Add(command, "$rawJson", row.ToString(Formatting.None));
            command.ExecuteNonQuery();
        }

        private void InsertAnimationBinding(JObject row, SqliteTransaction? transaction)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO animation_bindings (
                    indexed_at, game_title, status, error, source, source_type, name, object_path, output,
                    skeleton_path, skeleton_name, skeleton_guid, duration, frame_count, track_count,
                    notify_count, curve_count, segment_count, section_count, requires_acl, compression, raw_json
                )
                VALUES (
                    $indexedAt, $gameTitle, $status, $error, $source, $sourceType, $name, $objectPath, $output,
                    $skeletonPath, $skeletonName, $skeletonGuid, $duration, $frameCount, $trackCount,
                    $notifyCount, $curveCount, $segmentCount, $sectionCount, $requiresAcl, $compression, $rawJson
                );
                """;
            Add(command, "$indexedAt", (string?)row["indexedAt"]);
            Add(command, "$gameTitle", (string?)row["gameTitle"]);
            Add(command, "$status", (string?)row["status"]);
            Add(command, "$error", (string?)row["error"]);
            Add(command, "$source", (string?)row["source"]);
            Add(command, "$sourceType", (string?)row["sourceType"]);
            Add(command, "$name", (string?)row["name"]);
            Add(command, "$objectPath", (string?)row["objectPath"]);
            Add(command, "$output", (string?)row["output"]);
            Add(command, "$skeletonPath", (string?)row["skeletonPath"]);
            Add(command, "$skeletonName", (string?)row["skeletonName"]);
            Add(command, "$skeletonGuid", (string?)row["skeletonGuid"]);
            Add(command, "$duration", (double?)row["duration"]);
            Add(command, "$frameCount", (int?)row["frameCount"]);
            Add(command, "$trackCount", (int?)row["trackCount"]);
            Add(command, "$notifyCount", (int?)row["notifyCount"] ?? 0);
            Add(command, "$curveCount", (int?)row["curveCount"] ?? 0);
            Add(command, "$segmentCount", row["segments"] is JArray segments ? segments.Count : 0);
            Add(command, "$sectionCount", row["sections"] is JArray sections ? sections.Count : 0);
            Add(command, "$requiresAcl", ((bool?)row["requiresAcl"] ?? false) ? 1 : 0);
            Add(command, "$compression", (string?)row["compression"]);
            Add(command, "$rawJson", row.ToString(Formatting.None));
            command.ExecuteNonQuery();
        }

        private void InsertAutoReferencedExport(JObject row, SqliteTransaction? transaction)
        {
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO auto_referenced_exports (
                    stage, status, relation_type, target_path, source, output_type, reason, raw_json
                )
                VALUES (
                    $stage, $status, $relationType, $targetPath, $source, $outputType, $reason, $rawJson
                );
                """;
            Add(command, "$stage", (string?)row["stage"]);
            Add(command, "$status", (string?)row["status"]);
            Add(command, "$relationType", (string?)row["relationType"]);
            Add(command, "$targetPath", (string?)row["targetPath"]);
            Add(command, "$source", (string?)row["source"]);
            Add(command, "$outputType", (string?)row["outputType"]);
            Add(command, "$reason", (string?)row["reason"]);
            Add(command, "$rawJson", row.ToString(Formatting.None));
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                try
                {
                    ExecuteExportEventSql(_connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                    ExecuteExportEventSql(_connection, "PRAGMA journal_mode = DELETE;");
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
                {
                    Console.WriteLine($"WARN: export event checkpoint skipped because sqlite database is busy/locked ({ex.Message})");
                }

                _connection.Dispose();
            }
        }

        private static void EnsureSchema(SqliteConnection connection)
        {
            ExecuteExportEventSql(connection, """
                CREATE TABLE IF NOT EXISTS export_manifest (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    exported_at TEXT,
                    game_title TEXT,
                    kind TEXT,
                    source TEXT,
                    object_type TEXT,
                    name TEXT,
                    object_path TEXT,
                    output TEXT,
                    raw_json TEXT NOT NULL
                );
                """);
            ExecuteExportEventSql(connection, """
                CREATE TABLE IF NOT EXISTS asset_catalog (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    kind TEXT,
                    resource_kind TEXT,
                    name TEXT,
                    source_type TEXT,
                    source TEXT,
                    object_path TEXT,
                    output TEXT,
                    format TEXT,
                    skeleton_path TEXT,
                    skeleton_name TEXT,
                    validation_status TEXT,
                    status TEXT,
                    raw_json TEXT NOT NULL
                );
                """);
            ExecuteExportEventSql(connection, """
                CREATE TABLE IF NOT EXISTS animation_bindings (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    indexed_at TEXT,
                    game_title TEXT,
                    status TEXT,
                    error TEXT,
                    source TEXT,
                    source_type TEXT,
                    name TEXT,
                    object_path TEXT,
                    output TEXT,
                    skeleton_path TEXT,
                    skeleton_name TEXT,
                    skeleton_guid TEXT,
                    duration REAL,
                    frame_count INTEGER,
                    track_count INTEGER,
                    notify_count INTEGER,
                    curve_count INTEGER,
                    segment_count INTEGER,
                    section_count INTEGER,
                    requires_acl INTEGER,
                    compression TEXT,
                    raw_json TEXT NOT NULL
                );
                """);
            ExecuteExportEventSql(connection, """
                CREATE TABLE IF NOT EXISTS auto_referenced_exports (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    stage TEXT,
                    status TEXT,
                    relation_type TEXT,
                    target_path TEXT,
                    source TEXT,
                    output_type TEXT,
                    reason TEXT,
                    raw_json TEXT NOT NULL
                );
                """);
            ExecuteExportEventSql(connection, "CREATE INDEX IF NOT EXISTS idx_export_events_manifest_output ON export_manifest(output, kind);");
            ExecuteExportEventSql(connection, "CREATE INDEX IF NOT EXISTS idx_export_events_asset_output ON asset_catalog(output, kind);");
            ExecuteExportEventSql(connection, "CREATE INDEX IF NOT EXISTS idx_export_events_asset_object ON asset_catalog(object_path);");
            ExecuteExportEventSql(connection, "CREATE INDEX IF NOT EXISTS idx_export_events_animation_object ON animation_bindings(object_path, status);");
            ExecuteExportEventSql(connection, "CREATE INDEX IF NOT EXISTS idx_export_events_auto_target ON auto_referenced_exports(target_path, relation_type);");
        }

        private static string ValidateExportEventTableName(string tableName)
            => tableName is "export_manifest" or "asset_catalog" or "animation_bindings" or "auto_referenced_exports"
                ? tableName
                : throw new ArgumentOutOfRangeException(nameof(tableName), tableName, "Unknown export event table.");

        private static void Add(SqliteCommand command, string name, object? value)
            => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        private static void ExecuteExportEventSql(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    private sealed class ExportResumeStore : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly Dictionary<string, ExportResumeEntry> _entries;
        private readonly string _gameTitle;

        private ExportResumeStore(SqliteConnection connection, Dictionary<string, ExportResumeEntry> entries, string gameTitle)
        {
            _connection = connection;
            _entries = entries;
            _gameTitle = gameTitle;
        }

        public int Count => _entries.Count;

        public static ExportResumeStore Open(ConfigObj config)
        {
            var dbPath = Path.Combine(Path.GetFullPath(config.OutputDir), "export_resume_state.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            var connection = new SqliteConnection($"Data Source={dbPath}");
            connection.Open();
            ExecuteResumeSql(connection, "PRAGMA busy_timeout = 10000;");
            ExecuteResumeSql(connection, "PRAGMA journal_mode = WAL;");
            ExecuteResumeSql(connection, "PRAGMA synchronous = NORMAL;");
            ExecuteResumeSql(connection, """
                CREATE TABLE IF NOT EXISTS export_jobs (
                    job_key TEXT PRIMARY KEY,
                    completed_at TEXT NOT NULL,
                    game_title TEXT NOT NULL,
                    source TEXT NOT NULL,
                    source_size INTEGER NOT NULL,
                    output_type TEXT NOT NULL,
                    auto_referenced_target TEXT,
                    auto_referenced_relation_type TEXT,
                    outputs_json TEXT NOT NULL,
                    status TEXT NOT NULL
                );
                """);
            ExecuteResumeSql(connection, "CREATE INDEX IF NOT EXISTS idx_export_jobs_source ON export_jobs(source, output_type);");
            ExecuteResumeSql(connection, "CREATE INDEX IF NOT EXISTS idx_export_jobs_status ON export_jobs(status);");

            var entries = new Dictionary<string, ExportResumeEntry>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT job_key, source_size, outputs_json, status
                FROM export_jobs
                WHERE status = 'exported';
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var jobKey = reader.GetString(0);
                string[] outputs;
                try
                {
                    outputs = JsonConvert.DeserializeObject<string[]>(reader.GetString(2)) ?? [];
                }
                catch
                {
                    outputs = [];
                }

                entries[jobKey] = new ExportResumeEntry
                {
                    JobKey = jobKey,
                    SourceSize = reader.GetInt64(1),
                    Outputs = outputs,
                    Status = reader.GetString(3),
                };
            }

            return new ExportResumeStore(connection, entries, config.GameTitle);
        }

        public bool ShouldSkip(string jobKey, long sourceSize)
        {
            lock (ResumeWriteLock)
            {
                if (!_entries.TryGetValue(jobKey, out var entry))
                    return false;

                if (entry.SourceSize != sourceSize || entry.Outputs.Length == 0)
                    return false;

                return entry.Outputs.All(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
            }
        }

        public void MarkExported(
            string jobKey,
            string sourcePath,
            long sourceSize,
            string outputType,
            AutoReferencedExportRule? rule,
            string[] outputs)
        {
            var existingOutputs = outputs
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(File.Exists)
                .ToArray();
            if (existingOutputs.Length == 0)
                return;

            lock (ResumeWriteLock)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO export_jobs (
                        job_key, completed_at, game_title, source, source_size, output_type,
                        auto_referenced_target, auto_referenced_relation_type, outputs_json, status
                    )
                    VALUES (
                        $jobKey, $completedAt, $gameTitle, $source, $sourceSize, $outputType,
                        $autoReferencedTarget, $autoReferencedRelationType, $outputsJson, $status
                    )
                    ON CONFLICT(job_key) DO UPDATE SET
                        completed_at = excluded.completed_at,
                        game_title = excluded.game_title,
                        source = excluded.source,
                        source_size = excluded.source_size,
                        output_type = excluded.output_type,
                        auto_referenced_target = excluded.auto_referenced_target,
                        auto_referenced_relation_type = excluded.auto_referenced_relation_type,
                        outputs_json = excluded.outputs_json,
                        status = excluded.status;
                    """;
                command.Parameters.AddWithValue("$jobKey", jobKey);
                command.Parameters.AddWithValue("$completedAt", DateTime.UtcNow.ToString("O"));
                command.Parameters.AddWithValue("$gameTitle", _gameTitle);
                command.Parameters.AddWithValue("$source", sourcePath);
                command.Parameters.AddWithValue("$sourceSize", sourceSize);
                command.Parameters.AddWithValue("$outputType", outputType);
                command.Parameters.AddWithValue("$autoReferencedTarget", (object?)rule?.TargetPath ?? DBNull.Value);
                command.Parameters.AddWithValue("$autoReferencedRelationType", (object?)rule?.RelationType ?? DBNull.Value);
                command.Parameters.AddWithValue("$outputsJson", JsonConvert.SerializeObject(existingOutputs));
                command.Parameters.AddWithValue("$status", "exported");
                command.ExecuteNonQuery();

                _entries[jobKey] = new ExportResumeEntry
                {
                    JobKey = jobKey,
                    SourceSize = sourceSize,
                    Outputs = existingOutputs,
                    Status = "exported",
                };
            }
        }

        public void Dispose()
        {
            lock (ResumeWriteLock)
            {
                try
                {
                    ExecuteResumeSql(_connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
                {
                    Console.WriteLine($"WARN: export resume checkpoint skipped because sqlite database is busy/locked ({ex.Message})");
                }

                _connection.Dispose();
            }
        }

        private static void ExecuteResumeSql(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    private sealed class ExportResumeEntry
    {
        public string JobKey { get; set; } = "";
        public long SourceSize { get; set; }
        public string[] Outputs { get; set; } = [];
        public string Status { get; set; } = "";
    }

    private static object BuildModelCatalogEntry(string sourcePath, UObject obj, string outputPath)
    {
        var isSkeletal = obj is USkeletalMesh;
        var skeletalMesh = obj as USkeletalMesh;
        var staticMesh = obj as UStaticMesh;
        return new
        {
            kind = "Model",
            resourceKind = InferCatalogResourceKind(sourcePath),
            name = obj.Name,
            sourceType = isSkeletal ? "USkeletalMesh" : "UStaticMesh",
            source = sourcePath,
            objectPath = obj.GetPathName(),
            output = Path.GetFullPath(outputPath),
            format = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant(),
            hasSkeleton = isSkeletal,
            boneCount = skeletalMesh?.ReferenceSkeleton?.FinalRefBoneInfo?.Length ?? 0,
            materialCount = skeletalMesh?.SkeletalMaterials?.Length ?? staticMesh?.Materials?.Length ?? 0,
            materialSlots = BuildModelMaterialSlots(staticMesh, skeletalMesh),
            morphTargetCount = skeletalMesh?.MorphTargets?.Length ?? 0,
            socketCount = CountModelSockets(staticMesh, skeletalMesh),
            socketNames = BuildModelSocketNames(staticMesh, skeletalMesh),
            skeletonPath = GetPackageIndexPath(skeletalMesh?.Skeleton),
            skeletonName = skeletalMesh?.Skeleton?.Name,
            boneNames = skeletalMesh?.ReferenceSkeleton?.FinalRefBoneInfo?.Select(x => x.Name.Text).ToArray(),
        };
    }

    private static object[] BuildModelMaterialSlots(UStaticMesh? staticMesh, USkeletalMesh? skeletalMesh)
    {
        if (skeletalMesh != null)
        {
            return skeletalMesh.SkeletalMaterials
                .Select((slot, index) => new
                {
                    index,
                    slotName = slot.MaterialSlotName.Text,
                    importedSlotName = slot.ImportedMaterialSlotName?.Text,
                    materialName = slot.Material?.Name.Text,
                    materialObjectPath = slot.Material?.GetPathName(),
                })
                .Cast<object>()
                .ToArray();
        }

        if (staticMesh?.StaticMaterials != null)
        {
            return staticMesh.StaticMaterials
                .Select((slot, index) => new
                {
                    index,
                    slotName = slot.MaterialSlotName.Text,
                    importedSlotName = slot.ImportedMaterialSlotName.Text,
                    materialName = slot.MaterialInterface?.Name.Text,
                    materialObjectPath = slot.MaterialInterface?.GetPathName(),
                })
                .Cast<object>()
                .ToArray();
        }

        return [];
    }

    private static int CountModelSockets(UStaticMesh? staticMesh, USkeletalMesh? skeletalMesh)
        => (staticMesh?.Sockets.Length ?? 0) + (skeletalMesh?.Sockets.Length ?? 0) + CountSkeletonSockets(skeletalMesh);

    private static int CountSkeletonSockets(USkeletalMesh? skeletalMesh)
    {
        try
        {
            return skeletalMesh?.Skeleton.Load<USkeleton>()?.Sockets.Length ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string[] BuildModelSocketNames(UStaticMesh? staticMesh, USkeletalMesh? skeletalMesh)
    {
        var names = new List<string>();
        if (staticMesh != null)
            names.AddRange(staticMesh.Sockets.Select(x => x.Load<UStaticMeshSocket>()?.SocketName.Text).Where(x => !string.IsNullOrWhiteSpace(x))!);
        if (skeletalMesh != null)
            names.AddRange(skeletalMesh.Sockets.Select(x => x.Load<USkeletalMeshSocket>()?.SocketName.Text).Where(x => !string.IsNullOrWhiteSpace(x))!);

        try
        {
            var skeleton = skeletalMesh?.Skeleton.Load<USkeleton>();
            if (skeleton != null)
                names.AddRange(skeleton.Sockets.Select(x => x.Load<USkeletalMeshSocket>()?.SocketName.Text).Where(x => !string.IsNullOrWhiteSpace(x))!);
        }
        catch
        {
            // Socket 只是辅助关系，加载失败时保留模型导出主流程。
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()!;
    }

    private static object BuildTextureCatalogEntry(string sourcePath, UTexture2D texture, string outputPath)
    {
        return new
        {
            kind = "Texture",
            resourceKind = "Texture2D",
            name = texture.Name,
            sourceType = "UTexture2D",
            source = sourcePath,
            objectPath = texture.GetPathName(),
            output = Path.GetFullPath(outputPath),
            format = "png",
            width = texture.PlatformData?.SizeX ?? 0,
            height = texture.PlatformData?.SizeY ?? 0,
            pixelFormat = texture.Format.ToString(),
            isNormalMap = texture.IsNormalMap,
        };
    }

    private static object BuildMaterialCatalogEntry(string sourcePath, UMaterialInterface material, string outputPath)
    {
        return new
        {
            kind = "Material",
            resourceKind = "Material",
            name = material.Name,
            sourceType = material.GetType().Name,
            source = sourcePath,
            objectPath = material.GetPathName(),
            output = Path.GetFullPath(outputPath),
            format = "json",
        };
    }

    private static object BuildAnimationCatalogEntry(
        string sourcePath,
        UObject obj,
        string outputPath,
        string outputType,
        string status,
        string? error
    )
    {
        var asset = obj as UAnimationAsset;
        var sequence = obj as UAnimSequence;
        var sequenceBase = obj as UAnimSequenceBase;
        var trackMap = sequence?.GetTrackMap();
        return new
        {
            kind = "Animation",
            resourceKind = "Animation",
            name = obj.Name,
            sourceType = obj.GetType().Name,
            source = sourcePath,
            objectPath = obj.GetPathName(),
            output = Path.GetFullPath(outputPath),
            format = outputType,
            status,
            error,
            skeletonPath = GetPackageIndexPath(asset?.Skeleton),
            skeletonName = asset?.Skeleton.Name,
            skeletonGuid = asset?.SkeletonGuid.ToString(),
            duration = sequenceBase?.SequenceLength,
            frameCount = sequence?.NumFrames,
            trackCount = sequence?.GetNumTracks(),
            trackBoneIndexes = trackMap?.Select(x => x.BoneTreeIndex).ToArray(),
            notifyCount = sequenceBase?.Notifies.Length ?? 0,
            notifies = BuildAnimationNotifyEntries(sequenceBase),
            curveCount = CountAnimationCurves(sequence),
            curves = BuildAnimationCurveEntries(sequence),
            segments = BuildAnimationSegmentEntries(asset),
            sections = BuildAnimationSectionEntries(asset),
            compression = sequence?.CompressedDataStructure?.GetType().Name,
            requiresAcl = NeedsAclNative(asset),
            additiveType = sequence?.AdditiveAnimType.ToString(),
            additiveBasePoseType = sequence?.RefPoseType.ToString(),
            retargetSource = sequence?.RetargetSource.Text,
        };
    }

    private static void AppendAnimationBinding(
        ConfigObj config,
        string sourcePath,
        UAnimationAsset asset,
        string? outputPath,
        string status,
        string? error
    )
    {
        var sequence = asset as UAnimSequence;
        var sequenceBase = asset as UAnimSequenceBase;
        var trackMap = sequence?.GetTrackMap();
        var entry = new
        {
            indexedAt = DateTime.UtcNow.ToString("O"),
            gameTitle = config.GameTitle,
            kind = "AnimationBinding",
            status,
            error,
            source = sourcePath,
            sourceType = asset.GetType().Name,
            name = asset.Name,
            objectPath = asset.GetPathName(),
            output = string.IsNullOrWhiteSpace(outputPath) ? null : Path.GetFullPath(outputPath),
            skeletonPath = GetPackageIndexPath(asset.Skeleton),
            skeletonName = asset.Skeleton.Name,
            skeletonGuid = asset.SkeletonGuid.ToString(),
            duration = sequenceBase?.SequenceLength,
            frameCount = sequence?.NumFrames,
            trackCount = sequence?.GetNumTracks(),
            trackBoneIndexes = trackMap?.Select(x => x.BoneTreeIndex).ToArray(),
            notifyCount = sequenceBase?.Notifies.Length ?? 0,
            notifies = BuildAnimationNotifyEntries(sequenceBase),
            curveCount = CountAnimationCurves(sequence),
            curves = BuildAnimationCurveEntries(sequence),
            segments = BuildAnimationSegmentEntries(asset),
            sections = BuildAnimationSectionEntries(asset),
            compression = sequence?.CompressedDataStructure?.GetType().Name,
            requiresAcl = NeedsAclNative(asset),
            additiveType = sequence?.AdditiveAnimType.ToString(),
            additiveBasePoseType = sequence?.RefPoseType.ToString(),
            retargetSource = sequence?.RetargetSource.Text,
        };

        WriteExportEventSqlite(config, "animation_bindings", JObject.FromObject(entry));
        var path = Path.Combine(Path.GetFullPath(config.OutputDir), "animation_bindings.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (CatalogWriteLock)
        {
            File.AppendAllText(path, JsonConvert.SerializeObject(entry) + Environment.NewLine);
        }
    }

    private static object[] BuildAnimationSegmentEntries(UAnimationAsset? asset)
    {
        if (asset == null)
            return [];

        var result = new List<object>();
        if (asset is UAnimMontage montage)
        {
            var segmentIndex = 0;
            foreach (var slotTrack in montage.SlotAnimTracks)
            {
                foreach (var segment in slotTrack.AnimTrack.AnimSegments)
                    result.Add(BuildAnimationSegmentEntry(segmentIndex++, slotTrack.SlotName.Text, segment, "MontageSlot"));
            }
        }
        else if (asset is UAnimComposite composite)
        {
            for (var segmentIndex = 0; segmentIndex < composite.AnimationTrack.AnimSegments.Length; segmentIndex++)
                result.Add(BuildAnimationSegmentEntry(segmentIndex, null, composite.AnimationTrack.AnimSegments[segmentIndex], "CompositeTrack"));
        }

        return result.ToArray();
    }

    private static object BuildAnimationSegmentEntry(int segmentIndex, string? slotName, FAnimSegment segment, string relationSource)
    {
        var referencedAnimation = segment.AnimReference.Load<UAnimSequenceBase>();
        return new
        {
            segmentIndex,
            slotName,
            relationSource,
            referencedAnimationPath = GetPackageIndexPath(segment.AnimReference),
            referencedAnimationName = referencedAnimation?.Name,
            startPos = segment.StartPos,
            animStartTime = segment.AnimStartTime,
            animEndTime = segment.AnimEndTime,
            playRate = segment.AnimPlayRate,
            loopingCount = segment.LoopingCount,
            length = segment.GetLength(),
        };
    }

    private static object[] BuildAnimationSectionEntries(UAnimationAsset? asset)
    {
        if (asset is not UAnimMontage montage)
            return [];

        return montage.CompositeSections
            .Select((section, sectionIndex) => new
            {
                sectionIndex,
                sectionName = section.SectionName.Text,
                nextSectionName = section.NextSectionName.Text,
                slotIndex = section.SlotIndex,
                segmentIndex = section.SegmentIndex,
                segmentBeginTime = section.SegmentBeginTime,
                linkMethod = section.LinkMethod.ToString(),
                cachedLinkMethod = section.CachedLinkMethod.ToString(),
            })
            .Cast<object>()
            .ToArray();
    }

    private static object[] BuildAnimationNotifyEntries(UAnimSequenceBase? sequence)
    {
        if (sequence == null || sequence.Notifies.Length == 0)
            return [];

        return sequence.Notifies
            .Select((notify, notifyIndex) => new
            {
                notifyIndex,
                notifyName = notify.NotifyName.Text,
                notifyObjectPath = GetPackageIndexPath(notify.Notify),
                notifyStateObjectPath = GetPackageIndexPath(notify.NotifyStateClass),
                linkValue = notify.LinkValue,
                duration = notify.Duration,
                trackIndex = notify.TrackIndex,
                triggerChance = notify.NotifyTriggerChance,
                montageTickType = notify.MontageTickType.ToString(),
                linkMethod = notify.LinkMethod.ToString(),
                segmentIndex = notify.SegmentIndex,
                slotIndex = notify.SlotIndex,
            })
            .Cast<object>()
            .ToArray();
    }

    private static int CountAnimationCurves(UAnimSequence? sequence)
        => sequence?.CompressedCurveData?.FloatCurves?.Length ?? 0;

    private static object[] BuildAnimationCurveEntries(UAnimSequence? sequence)
    {
        var curves = sequence?.CompressedCurveData?.FloatCurves;
        if (curves is not { Length: > 0 })
            return [];

        return curves
            .Select((curve, curveIndex) =>
            {
                var keys = curve.FloatCurve.Keys ?? [];
                return new
                {
                    curveIndex,
                    curveName = curve.CurveName.Text,
                    curveTypeFlags = curve.CurveTypeFlags,
                    keyCount = keys.Length,
                    minTime = keys.Length == 0 ? (float?)null : keys.Min(x => x.Time),
                    maxTime = keys.Length == 0 ? (float?)null : keys.Max(x => x.Time),
                    minValue = keys.Length == 0 ? (float?)null : keys.Min(x => x.Value),
                    maxValue = keys.Length == 0 ? (float?)null : keys.Max(x => x.Value),
                };
            })
            .Cast<object>()
            .ToArray();
    }

    private static void WriteAnimationDiagnostic(
        ConfigObj config,
        string sourcePath,
        UAnimationAsset asset,
        string outputPath,
        string status,
        string error
    )
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var diagnostic = new
        {
            generatedAt = DateTime.UtcNow.ToString("O"),
            gameTitle = config.GameTitle,
            kind = "AnimationDiagnostic",
            status,
            error,
            source = sourcePath,
            sourceType = asset.GetType().Name,
            name = asset.Name,
            objectPath = asset.GetPathName(),
            skeletonPath = GetPackageIndexPath(asset.Skeleton),
            skeletonName = asset.Skeleton.Name,
            skeletonGuid = asset.SkeletonGuid.ToString(),
        };
        File.WriteAllText(outputPath, JsonConvert.SerializeObject(diagnostic, Formatting.Indented));
    }

    private static bool TryWriteAnimationMetadataSidecar(
        ConfigObj config,
        string sourcePath,
        UAnimationAsset asset,
        string outputPath,
        string status,
        string error)
    {
        var sequence = asset as UAnimSequence;
        var sequenceBase = asset as UAnimSequenceBase;
        var trackMap = sequence?.GetTrackMap();
        var curves = BuildAnimationCurveEntries(sequence);
        var notifies = BuildAnimationNotifyEntries(sequenceBase);
        var segments = BuildAnimationSegmentEntries(asset);
        var sections = BuildAnimationSectionEntries(asset);
        var hasUsefulMetadata = curves.Length > 0
            || notifies.Length > 0
            || segments.Length > 0
            || sections.Length > 0
            || sequenceBase?.SequenceLength > 0
            || !string.IsNullOrWhiteSpace(GetPackageIndexPath(asset.Skeleton))
            || !string.IsNullOrWhiteSpace(asset.GetPathName());
        if (!hasUsefulMetadata)
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var sidecar = new
        {
            generatedAt = DateTime.UtcNow.ToString("O"),
            gameTitle = config.GameTitle,
            kind = "AnimationMetadata",
            status,
            error,
            source = sourcePath,
            sourceType = asset.GetType().Name,
            name = asset.Name,
            objectPath = asset.GetPathName(),
            skeletonPath = GetPackageIndexPath(asset.Skeleton),
            skeletonName = asset.Skeleton.Name,
            skeletonGuid = asset.SkeletonGuid.ToString(),
            duration = sequenceBase?.SequenceLength,
            frameCount = sequence?.NumFrames,
            trackCount = sequence?.GetNumTracks(),
            trackBoneIndexes = trackMap?.Select(x => x.BoneTreeIndex).ToArray(),
            notifyCount = sequenceBase?.Notifies.Length ?? 0,
            notifies,
            curveCount = CountAnimationCurves(sequence),
            curves,
            segments,
            sections,
            compression = sequence?.CompressedDataStructure?.GetType().Name,
            requiresAcl = NeedsAclNative(asset),
            additiveType = sequence?.AdditiveAnimType.ToString(),
            additiveBasePoseType = sequence?.RefPoseType.ToString(),
            retargetSource = sequence?.RetargetSource.Text,
            note = "这是导出失败动画的可读元数据/诊断侧车，不是可直接播放的 .ueanim。曲线、通知、容器片段、时长和 Skeleton 等事实仍可用于素材库检索和后续动画支持。",
        };
        File.WriteAllText(outputPath, JsonConvert.SerializeObject(sidecar, Formatting.Indented));
        return true;
    }

    private static bool NeedsAclNative(UAnimationAsset? asset)
    {
        if (asset is UAnimSequence sequence)
            return IsAclCompressed(sequence);

        if (asset is UAnimMontage montage)
            return montage.SlotAnimTracks
                .SelectMany(x => x.AnimTrack.AnimSegments)
                .Any(x => TryLoadAclCompressedSequence(x.AnimReference));

        if (asset is UAnimComposite composite)
            return composite.AnimationTrack.AnimSegments
                .Any(x => TryLoadAclCompressedSequence(x.AnimReference));

        return false;
    }

    private static bool TryLoadAclCompressedSequence(CUE4Parse.UE4.Objects.UObject.FPackageIndex animReference)
    {
        var sequence = animReference.Load<UAnimSequence>();
        return sequence != null && IsAclCompressed(sequence);
    }

    private static bool IsAclCompressed(UAnimSequence sequence)
    {
        var typeName = sequence.CompressedDataStructure?.GetType().Name ?? "";
        return typeName.Contains("ACL", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAclNativeExports()
    {
        return CUE4ParseNatives.IsInitialized
               && NativeLibrary.TryGetExport(CUE4ParseNatives.LibraryHandle, "nAllocate", out _)
               && NativeLibrary.TryGetExport(CUE4ParseNatives.LibraryHandle, "nReadACLData", out _);
    }

    private static string? GetPackageIndexPath(CUE4Parse.UE4.Objects.UObject.FPackageIndex? index)
    {
        if (index == null || index.IsNull)
            return null;

        return index.ResolvedObjectNoCache?.GetPathName() ?? index.Name;
    }

    private static string InferCatalogResourceKind(string sourcePath)
    {
        var text = sourcePath.Replace('\\', '/').ToLowerInvariant();
        if (IsTaskOrPropLikePath(text))
            return "Prop";
        if (text.Contains("/weapon") || text.Contains("/weapons/") || text.Contains("/gadgets/") ||
            text.Contains("/grappling/") || text.Contains("/grapplegun/"))
            return "Weapon";
        if (text.Contains("/environment/") || text.Contains("/scenery/") || text.Contains("/building/") || text.Contains("/plants/"))
            return "Environment";
        if (text.Contains("/vehicle") || text.Contains("/vehicles/"))
            return "Vehicle";
        if (text.Contains("/characters/") || text.Contains("/character/"))
            return "Character";
        return "Unknown";
    }

    private static bool IsTaskOrPropLikePath(string normalizedLowerPath)
        => normalizedLowerPath.Contains("/item/") ||
           normalizedLowerPath.Contains("/items/") ||
           normalizedLowerPath.Contains("/props/") ||
           normalizedLowerPath.Contains("/prop/") ||
           normalizedLowerPath.Contains("/collectable") ||
           normalizedLowerPath.Contains("/collectible") ||
           normalizedLowerPath.Contains("/targets/") ||
           normalizedLowerPath.Contains("/target/") ||
           normalizedLowerPath.Contains("/quest") ||
           normalizedLowerPath.Contains("/mission") ||
           normalizedLowerPath.Contains("/objective") ||
           normalizedLowerPath.Contains("/interact") ||
           normalizedLowerPath.Contains("/pickup") ||
           normalizedLowerPath.Contains("/anomaly/");

    public static string ConvertGlbToGltf(string savedFilePath, bool deleteSourceGlb)
    {
        if (!savedFilePath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            return savedFilePath;

        byte[] data = File.ReadAllBytes(savedFilePath);
        if (data.Length < 28 || System.Text.Encoding.ASCII.GetString(data, 0, 4) != "glTF")
            throw new InvalidDataException($"Not a GLB file: {savedFilePath}");

        uint version = BitConverter.ToUInt32(data, 4);
        if (version != 2)
            throw new InvalidDataException($"Unsupported GLB version {version}: {savedFilePath}");

        int offset = 12;
        string? jsonText = null;
        byte[]? binData = null;
        while (offset + 8 <= data.Length)
        {
            int chunkLength = BitConverter.ToInt32(data, offset);
            uint chunkType = BitConverter.ToUInt32(data, offset + 4);
            int chunkStart = offset + 8;
            if (chunkLength < 0 || chunkStart + chunkLength > data.Length)
                throw new InvalidDataException($"Invalid GLB chunk: {savedFilePath}");

            if (chunkType == 0x4E4F534A)
                jsonText = System.Text.Encoding.UTF8.GetString(data, chunkStart, chunkLength).TrimEnd('\0', ' ', '\r', '\n', '\t');
            else if (chunkType == 0x004E4942)
            {
                binData = new byte[chunkLength];
                Buffer.BlockCopy(data, chunkStart, binData, 0, chunkLength);
            }

            offset = chunkStart + chunkLength;
        }

        if (string.IsNullOrWhiteSpace(jsonText))
            throw new InvalidDataException($"GLB JSON chunk is missing: {savedFilePath}");

        var gltf = JObject.Parse(jsonText);
        string gltfPath = Path.ChangeExtension(savedFilePath, ".gltf");
        if (binData is { Length: > 0 })
        {
            string binName = Path.GetFileNameWithoutExtension(gltfPath) + ".bin";
            File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(gltfPath)!, binName), binData);
            if (gltf["buffers"] is JArray buffers && buffers.First is JObject buffer)
                buffer["uri"] = binName;
        }

        File.WriteAllText(gltfPath, gltf.ToString(Formatting.Indented));
        if (deleteSourceGlb)
            File.Delete(savedFilePath);
        return gltfPath;
    }

    public static void SanitizeGlbForPreview(string savedFilePath)
    {
        if (!savedFilePath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            byte[] data = File.ReadAllBytes(savedFilePath);
            if (data.Length < 28 || System.Text.Encoding.ASCII.GetString(data, 0, 4) != "glTF")
                return;

            int jsonLength = BitConverter.ToInt32(data, 12);
            if (jsonLength <= 0 || data.Length < 20 + jsonLength + 8)
                return;

            string jsonText = System
                .Text.Encoding.UTF8.GetString(data, 20, jsonLength)
                .TrimEnd('\0', ' ', '\r', '\n', '\t');
            JObject gltf = JObject.Parse(jsonText);
            JArray? meshes = gltf["meshes"] as JArray;
            JArray? accessors = gltf["accessors"] as JArray;
            JArray? bufferViews = gltf["bufferViews"] as JArray;

            int binHeaderOffset = 20 + jsonLength;
            int binLength = BitConverter.ToInt32(data, binHeaderOffset);
            int binStart = binHeaderOffset + 8;
            if (binLength < 0 || binStart + binLength > data.Length)
                return;

            byte[] binData = new byte[binLength];
            Buffer.BlockCopy(data, binStart, binData, 0, binLength);
            bool changed = false;

            if (gltf["materials"] is JArray materials)
            {
                foreach (JObject material in materials.Children<JObject>())
                {
                    string? alphaMode = material["alphaMode"]?.Value<string>();
                    string? blendMode = material["extras"]?["blendMode"]?.Value<string>();
                    string? shadingModel = material["extras"]?["shadingModel"]?.Value<string>();
                    bool baseColorTextureHasAlpha = MaterialBaseColorTextureHasMeaningfulAlpha(
                        gltf,
                        material,
                        savedFilePath
                    );
                    if (
                        baseColorTextureHasAlpha
                        && alphaMode?.Equals("BLEND", StringComparison.OrdinalIgnoreCase) != true
                    )
                    {
                        material["alphaMode"] = "BLEND";
                        material.Remove("alphaCutoff");
                        changed = true;
                    }

                    if (
                        blendMode?.Equals("BLEND_Masked", StringComparison.OrdinalIgnoreCase)
                            == true
                        || shadingModel?.Equals("MSM_ClearCoat", StringComparison.OrdinalIgnoreCase)
                            == true
                    )
                    {
                        AddUnlitPreviewExtension(gltf, material);
                        changed = true;
                    }

                    if (material["pbrMetallicRoughness"]?["baseColorFactor"] is JArray baseColorFactor
                        && baseColorFactor.Count >= 4
                        && baseColorFactor[3]?.Value<double>() < 1.0
                        && material["alphaMode"] is null)
                    {
                        material["alphaMode"] = "BLEND";
                        changed = true;
                    }
                }
            }

            if (meshes is not null && accessors is not null && bufferViews is not null)
                changed |= SanitizeGlbVertexColorData(
                    gltf,
                    binData,
                    meshes,
                    accessors,
                    bufferViews
                );

            if (meshes is not null)
                changed |= SanitizeInvalidMorphTargets(gltf, meshes);

            if (changed)
                WriteGlb(savedFilePath, gltf, binData);
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"WARN: Failed to sanitize GLB for preview for {savedFilePath} ({ex.Message})"
            );
        }
    }

    private static string DescribeMeshExportFailure(object obj, ExporterOptions options)
    {
        try
        {
            if (obj is UStaticMesh staticMesh)
            {
                bool converted = staticMesh.TryConvert(
                    null,
                    out var convertedMesh,
                    options.NaniteMeshFormat
                );
                if (!converted)
                {
                    string packageFlags = staticMesh.Owner is null
                        ? "Owner=null"
                        : $"Flags={staticMesh.Owner.Summary.PackageFlags}";
                    int? lods = staticMesh.RenderData?.LODs?.Length;
                    int skipped = staticMesh.RenderData?.LODs?.Count(lod => lod.SkipLod) ?? 0;
                    return $"StaticMesh TryConvert=false, bCooked={staticMesh.bCooked}, RenderData={(staticMesh.RenderData is null ? "null" : "ok")}, Bounds={(staticMesh.RenderData?.Bounds is null ? "null" : "ok")}, LODs={(lods.HasValue ? lods.Value.ToString(CultureInfo.InvariantCulture) : "null")}, skipped={skipped}, {packageFlags}";
                }

                int lodCount = convertedMesh.LODs.Count;
                int skippedLodCount = convertedMesh.LODs.Count(lod => lod.SkipLod);
                return $"StaticMesh LODs={lodCount}, skipped={skippedLodCount}";
            }

            if (obj is USkeletalMesh skeletalMesh)
            {
                bool converted = skeletalMesh.TryConvert(out var convertedMesh);
                if (!converted)
                    return "SkeletalMesh TryConvert=false";

                int lodCount = convertedMesh.LODs.Count;
                int skippedLodCount = convertedMesh.LODs.Count(lod => lod.SkipLod);
                int sourceLodCount = skeletalMesh.LODModels?.Length ?? -1;
                int sourceSkippedLodCount = skeletalMesh.LODModels?.Count(lod => lod.SkipLod) ?? 0;
                string packageFlags = skeletalMesh.Owner is null
                    ? "Owner=null"
                    : $"Flags={skeletalMesh.Owner.Summary.PackageFlags}";
                return $"SkeletalMesh LODs={lodCount}, skipped={skippedLodCount}, sourceLODs={sourceLodCount}, sourceSkipped={sourceSkippedLodCount}, materials={skeletalMesh.SkeletalMaterials.Length}, {packageFlags}";
            }
        }
        catch (Exception ex)
        {
            return $"diagnostic failed: {ex.Message}";
        }

        return string.Empty;
    }

    private static bool SanitizeGlbVertexColorData(
        JObject gltf,
        byte[] binData,
        JArray meshes,
        JArray accessors,
        JArray bufferViews
    )
    {
        bool changed = false;

        foreach (
            JObject primitive in meshes.SelectMany(mesh =>
                mesh["primitives"]?.Children<JObject>() ?? []
            )
        )
        {
            int? colorAccessorIndex = primitive["attributes"]?["COLOR_0"]?.Value<int>();
            if (
                colorAccessorIndex is null
                || colorAccessorIndex < 0
                || colorAccessorIndex >= accessors.Count
            )
                continue;

            JObject accessor = (JObject)accessors[colorAccessorIndex.Value];
            if (
                accessor["componentType"]?.Value<int>() != 5121
                || accessor["type"]?.Value<string>() != "VEC4"
            )
                continue;

            int? bufferViewIndex = accessor["bufferView"]?.Value<int>();
            if (
                bufferViewIndex is null
                || bufferViewIndex < 0
                || bufferViewIndex >= bufferViews.Count
            )
                continue;

            JObject bufferView = (JObject)bufferViews[bufferViewIndex.Value];
            int count = accessor["count"]?.Value<int>() ?? 0;
            int accessorOffset = accessor["byteOffset"]?.Value<int>() ?? 0;
            int bufferViewOffset = bufferView["byteOffset"]?.Value<int>() ?? 0;
            int stride = bufferView["byteStride"]?.Value<int>() ?? 4;
            int start = bufferViewOffset + accessorOffset;

            byte maxColorChannel = 0;
            for (int i = 0; i < count; i++)
            {
                int offset = start + i * stride;
                if (offset + 3 >= binData.Length)
                    break;

                maxColorChannel = Math.Max(
                    maxColorChannel,
                    Math.Max(binData[offset], Math.Max(binData[offset + 1], binData[offset + 2]))
                );
            }

            bool nearlyInvisible = maxColorChannel <= 1;
            for (int i = 0; i < count; i++)
            {
                int offset = start + i * stride;
                if (offset + 3 >= binData.Length)
                    break;

                if (nearlyInvisible)
                {
                    binData[offset] = 255;
                    binData[offset + 1] = 255;
                    binData[offset + 2] = 255;
                    binData[offset + 3] = 255;
                    changed = true;
                }
                else if (binData[offset + 3] == 0)
                {
                    binData[offset + 3] = 255;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private static bool SanitizeInvalidMorphTargets(JObject gltf, JArray meshes)
    {
        bool changed = false;
        foreach (JObject mesh in meshes.Children<JObject>())
        {
            var primitives = mesh["primitives"]?.Children<JObject>().ToArray() ?? [];
            if (primitives.Length == 0)
                continue;

            var targetCounts = primitives
                .Select(x => (x["targets"] as JArray)?.Count ?? 0)
                .Distinct()
                .ToArray();
            bool extrasIsString = mesh["extras"]?.Type == JTokenType.String;
            bool inconsistentTargets = targetCounts.Length > 1;
            if (!extrasIsString && !inconsistentTargets)
                continue;

            foreach (var primitive in primitives)
            {
                if (primitive.Remove("targets"))
                    changed = true;
            }

            if (mesh.Remove("weights"))
                changed = true;
            if (mesh.Remove("extras"))
                changed = true;

            RemoveWeightAnimationChannels(gltf, mesh);
        }

        return changed;
    }

    private static void RemoveWeightAnimationChannels(JObject gltf, JObject mesh)
    {
        if (gltf["animations"] is not JArray animations)
            return;

        foreach (JObject animation in animations.Children<JObject>())
        {
            if (animation["channels"] is not JArray channels)
                continue;

            for (int i = channels.Count - 1; i >= 0; i--)
            {
                if (channels[i]?["target"]?["path"]?.Value<string>() == "weights")
                    channels.RemoveAt(i);
            }
        }
    }

    private static void AddUnlitPreviewExtension(JObject gltf, JObject material)
    {
        JArray extensionsUsed = gltf["extensionsUsed"] as JArray ?? new JArray();
        if (gltf["extensionsUsed"] is null)
            gltf["extensionsUsed"] = extensionsUsed;

        if (!extensionsUsed.Any(extension => extension.Value<string>() == "KHR_materials_unlit"))
            extensionsUsed.Add("KHR_materials_unlit");

        JObject extensions = material["extensions"] as JObject ?? new JObject();
        if (material["extensions"] is null)
            material["extensions"] = extensions;

        extensions["KHR_materials_unlit"] = new JObject();
    }

    private static bool MaterialBaseColorTextureHasMeaningfulAlpha(
        JObject gltf,
        JObject material,
        string savedFilePath
    )
    {
        if (!TryGetMaterialBaseColorImagePath(gltf, material, savedFilePath, out string imagePath))
            return false;

        return TextureAlphaCache.GetOrAdd(imagePath, TextureHasMeaningfulAlpha);
    }

    private static bool TryGetMaterialBaseColorImagePath(
        JObject gltf,
        JObject material,
        string savedFilePath,
        out string imagePath
    )
    {
        imagePath = string.Empty;
        int? textureIndex = material["pbrMetallicRoughness"]?["baseColorTexture"]?["index"]?.Value<int>();
        if (textureIndex is null || gltf["textures"] is not JArray textures || gltf["images"] is not JArray images)
            return false;
        if (textureIndex < 0 || textureIndex >= textures.Count || textures[textureIndex.Value] is not JObject texture)
            return false;

        int? sourceIndex = texture["source"]?.Value<int>();
        if (sourceIndex is null || sourceIndex < 0 || sourceIndex >= images.Count || images[sourceIndex.Value] is not JObject image)
            return false;

        string? uri = image["uri"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(uri) || IsNonFileImageUri(uri))
            return false;

        string? directory = Path.GetDirectoryName(savedFilePath);
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        string nativeUri = Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar);
        imagePath = Path.GetFullPath(Path.Combine(directory, nativeUri));
        return File.Exists(imagePath);
    }

    private static bool IsNonFileImageUri(string uri)
        => uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool TextureHasMeaningfulAlpha(string imagePath)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(imagePath);
            if (bitmap is null)
                return false;

            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).Alpha <= 245)
                        return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static void WriteGlb(string savedFilePath, JObject gltf, byte[] binData)
    {
        byte[] jsonData = System.Text.Encoding.UTF8.GetBytes(gltf.ToString(Formatting.None));
        int paddedJsonLength = (jsonData.Length + 3) & ~3;
        Array.Resize(ref jsonData, paddedJsonLength);
        for (int i = jsonData.Length - 1; i >= 0 && jsonData[i] == 0; i--)
            jsonData[i] = 0x20;

        int paddedBinLength = (binData.Length + 3) & ~3;
        Array.Resize(ref binData, paddedBinLength);

        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("glTF"));
        writer.Write(2);
        writer.Write(12 + 8 + jsonData.Length + 8 + binData.Length);
        writer.Write(jsonData.Length);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("JSON"));
        writer.Write(jsonData);
        writer.Write(binData.Length);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("BIN\0"));
        writer.Write(binData);
        File.WriteAllBytes(savedFilePath, stream.ToArray());
    }

    public static Dictionary<string, long> LoadCheckpoint(ConfigObj config)
    {
        if (config?.UseCheckpointFile?.Length > 0)
        {
            string checkpointPath = $"{RootDir}\\{config.UseCheckpointFile}";
            if (config.UseCheckpointFile.Equals("latest"))
            {
                string[] allCheckpointPaths = Directory.GetFiles($"{RootDir}\\checkpoints");
                var pathsForGameTitle = allCheckpointPaths.Where(path =>
                    path.Contains(config.GameTitle)
                );

                if (!pathsForGameTitle.Any())
                {
                    Console.WriteLine(
                        $"ERROR: could not find any checkpoints for \"{config.GameTitle}\". Ignoring..."
                    );
                    return [];
                }

                var sortedPaths = pathsForGameTitle.OrderBy(path =>
                {
                    string dateTimeFromFileName = path.Split(Path.DirectorySeparatorChar)
                        .Last()
                        .Split(".")
                        .First()
                        .SubstringAfter(config.GameTitle)[1..];
                    string date = dateTimeFromFileName.Split(" ")[0];
                    string time = dateTimeFromFileName.Split(" ")[1].Replace("-", ":");
                    double unixTime = DateTime
                        .Parse($"{date} {time}")
                        .Subtract(new DateTime(1970, 1, 1))
                        .TotalSeconds;
                    return unixTime;
                });

                var latestCheckpointPath = sortedPaths.Last();

                if (File.Exists(latestCheckpointPath))
                {
                    useCheckpoint = true;
                    Console.WriteLine(
                        $"Using checkpoint: latest ({latestCheckpointPath.Split(Path.DirectorySeparatorChar).Last()})"
                    );
                    var fromFile = File.ReadAllText(latestCheckpointPath);
                    var loadedCheckpoint = JsonConvert.DeserializeObject<Dictionary<string, long>>(
                        fromFile
                    );
                    return loadedCheckpoint ?? [];
                }

                return [];
            }
            else if (File.Exists(checkpointPath))
            {
                useCheckpoint = true;
                Console.WriteLine($"Using checkpoint: {config.UseCheckpointFile}");
                var fromFile = File.ReadAllText(checkpointPath);
                var loadedCheckpoint = JsonConvert.DeserializeObject<Dictionary<string, long>>(
                    fromFile
                );
                return loadedCheckpoint ?? [];
            }
            else
            {
                Console.WriteLine(
                    $"ERROR: checkpoint file at location \"{config.UseCheckpointFile}\" does not exist. Ignoring..."
                );
                return [];
            }
        }
        else
        {
            Console.WriteLine($"No checkpoint file selected. Ignoring...");
            return [];
        }
    }

    public static void CreateCheckpoint(
        ConcurrentDictionary<string, long> newCheckpointDict,
        ConfigObj config
    )
    {
        Console.WriteLine();
        var newCheckpointJson = JsonConvert.SerializeObject(newCheckpointDict, Formatting.Indented);
        var dateStamp = DateTime.Now.ToString("MM-dd-yyyy HH-mm");
        string checkpointsDirPath = $"{RootDir}\\checkpoints";
        if (!Directory.Exists(checkpointsDirPath))
        {
            Directory.CreateDirectory(checkpointsDirPath);
        }
        File.WriteAllText($"./checkpoints/{config.GameTitle} {dateStamp}.ckpt", newCheckpointJson);
        Console.WriteLine(
            $"Created checkpoint file: ./checkpoints/{config.GameTitle} {dateStamp}.ckpt"
        );
    }

    public static double Now()
    {
        return DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalMilliseconds;
    }

    public static string Elapsed(double start, double end, int factor = 1)
    {
        return ((end - start) / factor).ToString("0.00");
    }
}

public class ConfigObj
{
    public required string ConfigFileName { get; set; }
    public required int ConfigObjectIndex { get; set; }
    public required string GameTitle { get; set; }
    public required string Version { get; set; }
    public required string PaksDir { get; set; }
    public required string OutputDir { get; set; }
    public required string Aes { get; set; }
    public required bool LogOutputs { get; set; }
    public required bool KeepDirectoryStructure { get; set; }
    public string? Lang { get; set; }
    public int MaxDegreeOfParallelism { get; set; } =
        UnrealExporter.DefaultMaxDegreeOfParallelism;
    public bool FortniteMode { get; set; }
    public string? FortniteVersion { get; set; }
    public string? MappingsFile { get; set; }
    public bool? AutoFetchFortniteKeys { get; set; }
    public bool? AutoFetchFortniteMappings { get; set; }
    public bool? LoadOnDemandTocs { get; set; }
    public bool? LoadInstalledBundles { get; set; }
    public bool? ReadNaniteData { get; set; }
    public string? OnDemandHostUri { get; set; }
    public string? OnDemandCacheDir { get; set; }
    public int OnDemandTimeoutSeconds { get; set; }
    public string? EpicAuthToken { get; set; }
    public List<string>? ExtraDirectories { get; set; }
    public List<DynamicAesKeyConfig>? DynamicAesKeys { get; set; }
    public List<string>? DebugFileContains { get; set; }
    public List<string>? DebugFileRegex { get; set; }
    public int DebugFileLimit { get; set; }
    public bool CreateNewCheckpoint { get; set; }
    public string? UseCheckpointFile { get; set; }
    public bool GenerateLibraryIndexes { get; set; }
    public bool UseSharedTextures { get; set; }
    public bool GenerateSourceIndex { get; set; }
    public bool? AutoExportReferencedAssets { get; set; }
    public bool? AutoExportCompatibleAnimations { get; set; }
    public bool? ResumeExports { get; set; }
    public List<string>? SourceIndexRegex { get; set; }
    public int SourceIndexLimit { get; set; }
    public required List<string> Export { get; set; }
    public required List<string> Exclude { get; set; }
}

public class DynamicAesKeyConfig
{
    public string? Guid { get; set; }
    public string? Key { get; set; }
}

public class FortniteAesResponse
{
    public string? Version { get; set; }
    public string? MainKey { get; set; }
    public List<FortniteDynamicKeyResponse> DynamicKeys { get; set; } = [];
}

public class FortniteDynamicKeyResponse
{
    public string? Name { get; set; }
    public string? Guid { get; set; }
    public string? Key { get; set; }
}

public class FortniteMappingsResponse
{
    public string? Version { get; set; }
    public DateTime? Updated { get; set; }
    public string? Hash { get; set; }
    public string? FileName { get; set; }
    public long Size { get; set; }

    [JsonProperty("hash-md5")]
    public string? HashMd5 { get; set; }

    public string? Url { get; set; }
}
