using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnrealExporter;

internal static class UELibraryPostProcessor
{
    private static readonly string[] TextureExtensions = [".png", ".hdr"];
    private const int ModelValidationCacheVersion = 2;
    private const int MaxDetailedComponentGroupRelations = 250_000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    public static void Run(string libraryRoot, bool dedupeTextures, bool writeCompatibilityJson = false)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
            throw new ArgumentException("Library root is required.", nameof(libraryRoot));

        var root = Path.GetFullPath(libraryRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Library root not found: {root}");

        Console.WriteLine($"UE Library postprocess root: {root}");
        Console.WriteLine($"Compatibility JSON/JSONL outputs: {(writeCompatibilityJson ? "enabled" : "disabled")}");
        var modelFiles = new[] { "*.glb", "*.gltf" }
            .SelectMany(pattern => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            .Where(x => !IsIgnoredLibraryPath(root, x))
            .Where(IsSupportedGltfModel)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Console.WriteLine($"Scanning {modelFiles.Length} glTF model(s).");
        var materialIndex = RunStage("加载材质索引", () => LoadMaterialIndex(root));
        var reports = new List<ModelValidationEntry>(modelFiles.Length);
        var catalogRows = new List<JObject>(modelFiles.Length + materialIndex.Count);

        RunStage("验证模型并写模型说明", () =>
        {
            var materialIndexSignature = BuildMaterialIndexSignature(root, materialIndex.Values);
            var cacheHits = 0;
            var cacheMisses = 0;
            foreach (var glbPath in modelFiles)
            {
                var report = InspectModel(root, glbPath, materialIndex, materialIndexSignature, out var fromCache);
                if (fromCache)
                    cacheHits++;
                else
                    cacheMisses++;
                reports.Add(report);
                catalogRows.Add(BuildModelCatalogRow(report));
                WriteAssetReadme(root, report);
            }

            Console.WriteLine($"UE model validation cache: hits={cacheHits}, misses={cacheMisses}");
        });

        foreach (var material in materialIndex.Values.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
            catalogRows.Add(BuildMaterialCatalogRow(material));

        JObject? textureDedupeSummary = null;
        var textureLinks = dedupeTextures
            ? RunStage("共享贴图去重/硬链接", () =>
            {
                var result = DeduplicateTextureFilesCore(root, writeCompatibilityJson);
                textureDedupeSummary = result.Summary;
                return result.Links;
            })
            : RunStage("读取已有共享贴图索引", () =>
            {
                textureDedupeSummary = LoadLibraryWorkReport(root, "texture_dedupe");
                return LoadExistingTextureLinks(root);
            });
        foreach (var texture in textureLinks.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
            catalogRows.Add(BuildTextureCatalogRow(texture));

        var mergedCatalogRows = RunStage("写资产目录", () => WriteAssetCatalog(root, catalogRows, writeCompatibilityJson));
        var sourceIndex = RunStage("读取UE源索引", () => LoadSourceIndex(root));
        var materialTextureSlots = RunStage("写材质贴图槽关系", () => WriteMaterialTextureSlotLinks(root, materialIndex, textureLinks, sourceIndex, writeCompatibilityJson));
        RunStage("应用外部材质验证", () => ApplyExternalMaterialValidation(root, reports, mergedCatalogRows, materialTextureSlots));
        var sharedGltfTextureLinks = RunStage("外置GLB/改写glTF共享贴图引用", () => RewriteGltfSharedTextureUris(root, reports, materialTextureSlots, writeCompatibilityJson));
        mergedCatalogRows = RunStage("重写资产目录", () => WriteAssetCatalog(
            root,
            mergedCatalogRows.Concat(reports.Select(BuildModelCatalogRow)).ToList(),
            writeCompatibilityJson));
        var componentAssetRelations = RunStage("写组件素材关系", () => WriteComponentAssetRelations(root, mergedCatalogRows, sourceIndex, writeCompatibilityJson));
        var packageObjectMaps = RunStage("写包对象映射", () => WritePackageObjectMaps(root, sourceIndex, writeCompatibilityJson));
        var animationValidation = RunStage("写动画验证", () => WriteAnimationValidation(root, mergedCatalogRows, sourceIndex, componentAssetRelations, writeCompatibilityJson));
        var modelAnimationRelations = RunStage("写模型动画关系", () => WriteModelAnimationRelations(root, mergedCatalogRows, animationValidation, writeCompatibilityJson));
        var modelCoverage = RunStage("写模型覆盖报告", () => WriteModelCoverage(root, mergedCatalogRows, reports, componentAssetRelations, modelAnimationRelations, sourceIndex, writeCompatibilityJson));
        RunStage("写任务模型质量报告", () => WriteTaskModelQualityReport(root, modelCoverage, writeCompatibilityJson));
        RunStage("写模型验证报告", () => WriteModelValidation(root, reports, writeCompatibilityJson));
        var skeletonGroups = RunStage("写骨骼索引", () => WriteSkeletonIndex(root, reports, mergedCatalogRows, sourceIndex, writeCompatibilityJson));
        var libraryHealth = RunStage("写健康报告", () => WriteLibraryHealth(root, mergedCatalogRows, reports, textureLinks, materialTextureSlots, sharedGltfTextureLinks, componentAssetRelations, packageObjectMaps, skeletonGroups, modelAnimationRelations, modelCoverage, animationValidation, sourceIndex, writeCompatibilityJson));
        var libraryAcceptance = RunStage("写验收报告", () => WriteLibraryAcceptance(root, mergedCatalogRows, reports, textureLinks, materialTextureSlots, componentAssetRelations, skeletonGroups, modelAnimationRelations, modelCoverage, animationValidation, sourceIndex, writeCompatibilityJson));
        RunStage("写SQLite索引", () => WriteLibraryIndexDb(root, mergedCatalogRows, reports, materialIndex.Values, textureLinks, materialTextureSlots, sharedGltfTextureLinks, componentAssetRelations, packageObjectMaps, skeletonGroups, modelAnimationRelations, modelCoverage, animationValidation, sourceIndex, libraryHealth, libraryAcceptance, textureDedupeSummary));
        RunStage("写素材库说明", () => WriteLibraryReadme(root, reports, materialIndex.Values, componentAssetRelations, packageObjectMaps));

        Console.WriteLine($"UE Library postprocess finished: {root}");
    }

    public static void MaterializeAnimationMetadataSidecars(string libraryRoot, bool writeCompatibilityJson = false)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
            throw new ArgumentException("Library root is required.", nameof(libraryRoot));

        var root = Path.GetFullPath(libraryRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Library root not found: {root}");

        var catalogSummary = MaterializeAnimationMetadataRows(root, "asset_catalog", Path.Combine(root, "asset_catalog.jsonl"), true, writeCompatibilityJson);
        var bindingSummary = MaterializeAnimationMetadataRows(root, "animation_bindings", Path.Combine(root, "animation_bindings.jsonl"), false, writeCompatibilityJson);
        Console.WriteLine(JsonConvert.SerializeObject(new
        {
            root,
            assetCatalog = catalogSummary,
            animationBindings = bindingSummary,
            note = writeCompatibilityJson
                ? "已把失败但含曲线、通知或容器片段的 UE 动画写成 .metadata.json 调试侧车，并同步 SQLite；它们仍不会进入默认可播放动画候选。"
                : "已把失败但含曲线、通知或容器片段的 UE 动画标记为 SQLite metadata 行；默认不写 .metadata.json 侧车，它们仍不会进入默认可播放动画候选。"
        }, Formatting.Indented));
    }

    private static AnimationMetadataMaterializeResult MaterializeAnimationMetadataRows(
        string root,
        string tableName,
        string legacyJsonLinesPath,
        bool updateCatalogRow,
        bool writeCompatibilityJson)
    {
        var sqlite = MaterializeAnimationMetadataSqlite(root, tableName, updateCatalogRow, writeCompatibilityJson);
        AnimationMetadataMaterializeSummary? legacy = null;
        if (writeCompatibilityJson && File.Exists(legacyJsonLinesPath))
            legacy = MaterializeAnimationMetadataJsonLines(root, legacyJsonLinesPath, updateCatalogRow);

        if (sqlite != null || legacy != null)
            return new AnimationMetadataMaterializeResult
            {
                Primary = sqlite ?? legacy!,
                CompatibilityJsonl = sqlite != null ? legacy : null,
            };

        return new AnimationMetadataMaterializeResult
        {
            Primary = new AnimationMetadataMaterializeSummary
            {
                Backend = "sqlite",
                Table = tableName,
                Path = Path.Combine(root, "export_events.db"),
                SkippedReason = "exportEventsDbOrLegacyJsonlNotFound",
            },
        };
    }

    private static AnimationMetadataMaterializeSummary? MaterializeAnimationMetadataSqlite(
        string root,
        string tableName,
        bool updateCatalogRow,
        bool writeCompatibilityJson)
    {
        var dbPath = Path.Combine(root, "export_events.db");
        if (!File.Exists(dbPath))
            return null;

        SQLitePCL.Batteries_V2.Init();
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        tableName = ValidateExportEventTableName(tableName);
        if (!TableExists(connection, tableName) || !TableColumnExists(connection, tableName, "raw_json"))
            return new AnimationMetadataMaterializeSummary
            {
                Backend = "sqlite",
                Table = tableName,
                Path = dbPath,
                SkippedReason = "tableNotFound",
            };

        var summary = new AnimationMetadataMaterializeSummary
        {
            Backend = "sqlite",
            Table = tableName,
            Path = dbPath,
        };
        var updates = new List<(long Id, JObject Row)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT id, raw_json FROM {tableName} ORDER BY id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                summary.Rows++;
                JObject row;
                try
                {
                    row = JObject.Parse(reader.GetString(1));
                }
                catch (JsonException)
                {
                    summary.JsonErrors++;
                    continue;
                }

                if (!IsFailedAnimationRow(row) || !HasUsefulAnimationMetadata(row))
                    continue;

                string? metadataPath = null;
                if (writeCompatibilityJson)
                {
                    metadataPath = BuildAnimationMetadataPath(root, row);
                    if (string.IsNullOrWhiteSpace(metadataPath))
                    {
                        summary.MissingOutput++;
                        continue;
                    }

                    WriteAnimationMetadataSidecar(row, metadataPath);
                    summary.JsonSidecarsWritten++;
                }

                if (!TryApplyAnimationMetadataRowUpdate(row, metadataPath, updateCatalogRow))
                {
                    summary.MissingOutput++;
                    continue;
                }

                summary.Materialized++;
                updates.Add((reader.GetInt64(0), row));
            }
        }

        if (updates.Count > 0)
        {
            using var transaction = connection.BeginTransaction();
            foreach (var (id, row) in updates)
                UpdateAnimationMetadataSqliteRow(connection, transaction, tableName, id, row);
            transaction.Commit();
        }

        return summary;
    }

    private static void UpdateAnimationMetadataSqliteRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        long id,
        JObject row)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (string.Equals(tableName, "asset_catalog", StringComparison.OrdinalIgnoreCase))
        {
            command.CommandText = """
                UPDATE asset_catalog
                SET kind = $kind,
                    output = $output,
                    format = $format,
                    status = $status,
                    raw_json = $rawJson
                WHERE id = $id;
                """;
            Add(command, "$kind", (string?)row["kind"]);
            Add(command, "$format", (string?)row["format"]);
        }
        else if (string.Equals(tableName, "animation_bindings", StringComparison.OrdinalIgnoreCase))
        {
            command.CommandText = """
                UPDATE animation_bindings
                SET status = $status,
                    output = $output,
                    raw_json = $rawJson
                WHERE id = $id;
                """;
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(tableName), tableName, "Unsupported animation metadata table.");
        }

        Add(command, "$status", (string?)row["status"]);
        Add(command, "$output", (string?)row["output"]);
        Add(command, "$rawJson", row.ToString(Formatting.None));
        Add(command, "$id", id);
        command.ExecuteNonQuery();
    }

    private static AnimationMetadataMaterializeSummary MaterializeAnimationMetadataJsonLines(
        string root,
        string path,
        bool updateCatalogRow)
    {
        var summary = new AnimationMetadataMaterializeSummary { Path = path };
        if (!File.Exists(path))
        {
            summary.SkippedReason = "fileNotFound";
            return summary;
        }

        var tempPath = path + ".metadata.tmp";
        var backupPath = path + ".metadata.bak";
        using (var writer = new StreamWriter(tempPath, append: false, new UTF8Encoding(false)))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    writer.WriteLine(line);
                    continue;
                }

                summary.Rows++;
                JObject row;
                try
                {
                    row = JObject.Parse(line);
                }
                catch (JsonException)
                {
                    summary.JsonErrors++;
                    writer.WriteLine(line);
                    continue;
                }

                if (!IsFailedAnimationRow(row) || !HasUsefulAnimationMetadata(row))
                {
                    writer.WriteLine(line);
                    continue;
                }

                var metadataPath = BuildAnimationMetadataPath(root, row);
                if (string.IsNullOrWhiteSpace(metadataPath))
                {
                    summary.MissingOutput++;
                    writer.WriteLine(line);
                    continue;
                }

                WriteAnimationMetadataSidecar(row, metadataPath);
                summary.JsonSidecarsWritten++;
                summary.Materialized++;
                TryApplyAnimationMetadataRowUpdate(row, metadataPath, updateCatalogRow);
                writer.WriteLine(row.ToString(Formatting.None));
            }
        }

        ReplaceJsonLinesFile(path, tempPath, backupPath);
        return summary;
    }

    private static bool TryApplyAnimationMetadataRowUpdate(JObject row, string? metadataPath, bool updateCatalogRow)
    {
        row["status"] = "metadata";
        row["format"] = string.IsNullOrWhiteSpace(metadataPath) ? "sqlite" : "json";
        if (!string.IsNullOrWhiteSpace(metadataPath))
            row["output"] = metadataPath;
        else if (string.IsNullOrWhiteSpace((string?)row["output"]))
            return false;

        row["metadataOnly"] = true;
        row["metadataStorage"] = string.IsNullOrWhiteSpace(metadataPath) ? "export_events.db.raw_json" : "jsonSidecar";
        row["note"] = string.IsNullOrWhiteSpace(metadataPath)
            ? "该动画未成功导出为可播放 .ueanim；UE 曲线、通知、Montage/Composite 片段、时长或 Skeleton 等事实保留在 SQLite raw_json，供素材库检索和后续动画支持使用。"
            : "该动画未成功导出为可播放 .ueanim；这里保留 UE 曲线、通知、Montage/Composite 片段、时长或 Skeleton 等事实，供素材库检索和后续动画支持使用。";
        if (updateCatalogRow)
            row["kind"] = "Animation";

        return true;
    }

    private static bool IsFailedAnimationRow(JObject row)
        => string.Equals((string?)row["kind"], "Animation", StringComparison.OrdinalIgnoreCase)
           || string.Equals((string?)row["kind"], "AnimationBinding", StringComparison.OrdinalIgnoreCase);

    private static bool HasUsefulAnimationMetadata(JObject row)
    {
        if (!string.Equals((string?)row["status"], "error", StringComparison.OrdinalIgnoreCase))
            return false;

        return JArrayCount(row["curves"]) > 0
            || JArrayCount(row["notifies"]) > 0
            || JArrayCount(row["segments"]) > 0
            || JArrayCount(row["sections"]) > 0
            || ((int?)row["curveCount"] ?? 0) > 0
            || ((int?)row["notifyCount"] ?? 0) > 0
            || ((double?)row["duration"] ?? 0) > 0
            || !string.IsNullOrWhiteSpace((string?)row["skeletonPath"])
            || !string.IsNullOrWhiteSpace((string?)row["objectPath"]);
    }

    private static int JArrayCount(JToken? token)
        => token is JArray array ? array.Count : 0;

    private static string BuildAnimationMetadataPath(string root, JObject row)
    {
        var output = (string?)row["output"];
        if (!string.IsNullOrWhiteSpace(output))
        {
            var path = output!;
            if (!Path.IsPathRooted(path))
                path = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            return Path.GetFullPath(path) + ".metadata.json";
        }

        var source = ((string?)row["source"])?.Replace('\\', '/');
        var name = (string?)row["name"];
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(name))
            return "";

        var withoutExtension = Path.ChangeExtension(source, null) ?? source;
        var safeRelative = withoutExtension
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace(':', '_')
            .TrimStart(Path.DirectorySeparatorChar);
        return Path.Combine(root, safeRelative + ".ueanim.metadata.json");
    }

    private static void WriteAnimationMetadataSidecar(JObject row, string metadataPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
        var metadata = new JObject
        {
            ["generatedAt"] = DateTime.UtcNow.ToString("O"),
            ["kind"] = "AnimationMetadata",
            ["status"] = "metadata",
            ["sourceStatus"] = row["status"]?.DeepClone(),
            ["error"] = row["error"]?.DeepClone(),
            ["source"] = row["source"]?.DeepClone(),
            ["sourceType"] = row["sourceType"]?.DeepClone(),
            ["name"] = row["name"]?.DeepClone(),
            ["objectPath"] = row["objectPath"]?.DeepClone(),
            ["skeletonPath"] = row["skeletonPath"]?.DeepClone(),
            ["skeletonName"] = row["skeletonName"]?.DeepClone(),
            ["skeletonGuid"] = row["skeletonGuid"]?.DeepClone(),
            ["duration"] = row["duration"]?.DeepClone(),
            ["frameCount"] = row["frameCount"]?.DeepClone(),
            ["trackCount"] = row["trackCount"]?.DeepClone(),
            ["trackBoneIndexes"] = row["trackBoneIndexes"]?.DeepClone(),
            ["notifyCount"] = row["notifyCount"]?.DeepClone(),
            ["notifies"] = row["notifies"]?.DeepClone() ?? new JArray(),
            ["curveCount"] = row["curveCount"]?.DeepClone(),
            ["curves"] = row["curves"]?.DeepClone() ?? new JArray(),
            ["segments"] = row["segments"]?.DeepClone() ?? new JArray(),
            ["sections"] = row["sections"]?.DeepClone() ?? new JArray(),
            ["compression"] = row["compression"]?.DeepClone(),
            ["requiresAcl"] = row["requiresAcl"]?.DeepClone(),
            ["additiveType"] = row["additiveType"]?.DeepClone(),
            ["additiveBasePoseType"] = row["additiveBasePoseType"]?.DeepClone(),
            ["retargetSource"] = row["retargetSource"]?.DeepClone(),
            ["note"] = "这是导出失败动画的可读元数据/诊断侧车，不是可直接播放的 .ueanim。曲线、通知、容器片段、时长和 Skeleton 等事实仍可用于素材库检索和后续动画支持。"
        };
        File.WriteAllText(metadataPath, metadata.ToString(Formatting.Indented));
    }

    private static void ReplaceJsonLinesFile(string path, string tempPath, string backupPath)
    {
        if (File.Exists(backupPath))
            File.Delete(backupPath);
        File.Move(path, backupPath);
        File.Move(tempPath, path);
        File.Delete(backupPath);
    }

    public static void RefreshTaskModelQualityReport(string libraryRoot)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
            throw new ArgumentException("Library root is required.", nameof(libraryRoot));

        var root = Path.GetFullPath(libraryRoot);
        var rows = LoadModelCoverageRowsForTaskQuality(root);
        WriteTaskModelQualityReport(root, rows, writeCompatibilityJson: false);
        Console.WriteLine($"UE task model quality report refreshed: {root}");
    }

    private static ModelCoverageRow[] LoadModelCoverageRowsForTaskQuality(string root)
    {
        var dbPath = Path.Combine(root, "library_index.db");
        if (!File.Exists(dbPath))
            throw new FileNotFoundException("library_index.db is required to refresh task model quality.", dbPath);

        SQLitePCL.Batteries_V2.Init();
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        if (!TableExists(connection, "model_coverage"))
            throw new FileNotFoundException("library_index.db.model_coverage is required to refresh task model quality.", dbPath);
        if (!TableExists(connection, "model_coverage_task_signals"))
            throw new FileNotFoundException("library_index.db.model_coverage_task_signals is required to refresh task model quality.", dbPath);
        if (!TableExists(connection, "model_coverage_review_reasons"))
            throw new FileNotFoundException("library_index.db.model_coverage_review_reasons is required to refresh task model quality.", dbPath);

        var rows = new List<ModelCoverageRow>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mc.name, mc.output, mc.source, mc.object_path, mc.resource_kind, mc.source_type, mc.validation_status,
                   is_static, has_skin, has_skeleton_path, material_count, texture_count,
                   component_reference_count, source_index_object_count, animation_candidate_count,
                   COALESCE((
                       SELECT group_concat(signal, char(31))
                       FROM (
                           SELECT signal
                           FROM model_coverage_task_signals signal_rows
                           WHERE signal_rows.model_coverage_id = mc.id
                           ORDER BY signal_index
                       )
                   ), '')
            FROM model_coverage mc
            ORDER BY mc.output COLLATE NOCASE;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var source = GetString(reader, 2);
            rows.Add(new ModelCoverageRow
            {
                Name = GetString(reader, 0) ?? "",
                Output = GetString(reader, 1) ?? "",
                Source = source ?? "",
                ObjectPath = GetString(reader, 3),
                ResourceKind = GetString(reader, 4) ?? "Unknown",
                SourceType = GetString(reader, 5) ?? "",
                ValidationStatus = GetString(reader, 6) ?? "unknown",
                IsStatic = GetRequiredBool(reader, 7),
                HasSkin = GetRequiredBool(reader, 8),
                HasSkeletonPath = GetRequiredBool(reader, 9),
                MaterialCount = GetRequiredInt32(reader, 10),
                TextureCount = GetRequiredInt32(reader, 11),
                ComponentReferenceCount = GetRequiredInt32(reader, 12),
                SourceIndexObjectCount = GetRequiredInt32(reader, 13),
                AnimationCandidateCount = GetRequiredInt32(reader, 14),
                TaskSignals = SplitSqliteList(GetString(reader, 15)),
            });
        }

        return rows.ToArray();
    }

    private static T RunStage<T>(string name, Func<T> action)
    {
        var sw = Stopwatch.StartNew();
        Console.WriteLine($"[postprocess] {name}...");
        var result = action();
        Console.WriteLine($"[postprocess] {name} done ({sw.Elapsed:mm\\:ss\\.fff})");
        return result;
    }

    private static void RunStage(string name, Action action)
    {
        RunStage(name, () =>
        {
            action();
            return true;
        });
    }

    private static bool IsLikelyMaterialJson(string path)
    {
        try
        {
            using var reader = File.OpenText(path);
            using var jsonReader = new JsonTextReader(reader);
            if (!jsonReader.Read() || jsonReader.TokenType != JsonToken.StartObject)
                return false;

            while (jsonReader.Read())
            {
                if (jsonReader.TokenType == JsonToken.EndObject)
                    return false;
                if (jsonReader.TokenType != JsonToken.PropertyName)
                    continue;

                var propertyName = (string?)jsonReader.Value;
                if (!jsonReader.Read())
                    return false;

                // 素材库根报告可能很大；这里只看顶层字段，避免为了排除报告 JSON 而整文件解析。
                if (string.Equals(propertyName, "Parameters", StringComparison.OrdinalIgnoreCase))
                    return jsonReader.TokenType == JsonToken.StartObject;

                jsonReader.Skip();
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSupportedGltfModel(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".glb", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".gltf", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnoredLibraryPath(string root, string path)
    {
        var relative = MakeRelative(root, path).Replace('\\', '/');
        if (relative.StartsWith(".as_browser_cache/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith(".animestudio_browser/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("Textures/_Shared/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("Textures/.Shared/", StringComparison.OrdinalIgnoreCase)
            || relative.EndsWith(".ue_model_validation_cache.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return relative.Split('/').Any(part => part.StartsWith(".", StringComparison.Ordinal));
    }

    private static Dictionary<string, MaterialInfo> LoadMaterialIndex(string root)
    {
        var materialJsonFiles = FindMaterialJsonFilesFromAssetCatalog(root);
        var source = "asset catalog";
        var cached = LoadMaterialIndexFromWorkDb(root, materialJsonFiles.Length > 0 ? materialJsonFiles : null);
        if (cached.Count > 0)
        {
            Console.WriteLine($"Material sidecars loaded from library_work.db: {cached.Count}");
            return cached;
        }

        if (materialJsonFiles.Length == 0)
        {
            materialJsonFiles = FindMaterialJsonFilesByScan(root);
            source = "fallback JSON scan";
        }

        var result = LoadMaterialIndexFromJsonFiles(root, materialJsonFiles);
        SaveMaterialIndexToWorkDb(root, result.Values);
        Console.WriteLine($"Material sidecars loaded from {source}: candidates={materialJsonFiles.Length}, parsed={result.Count}");
        return result;
    }

    private static string[] FindMaterialJsonFilesFromAssetCatalog(string root)
    {
        var catalogPath = Path.Combine(root, "asset_catalog.jsonl");
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ReadExportEventRows(root, "asset_catalog", catalogPath))
        {
            if (!string.Equals((string?)row["kind"], "Material", StringComparison.OrdinalIgnoreCase))
                continue;

            var output = (string?)row["output"] ?? (string?)row["source"];
            if (string.IsNullOrWhiteSpace(output))
                continue;

            var path = Path.IsPathRooted(output)
                ? Path.GetFullPath(output)
                : Path.GetFullPath(Path.Combine(root, output.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(path) ||
                !Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                IsIgnoredLibraryPath(root, path))
            {
                continue;
            }

            files.Add(path);
        }

        return files.ToArray();
    }

    private static string[] FindMaterialJsonFilesByScan(string root)
        => Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(x => !IsIgnoredLibraryPath(root, x))
            .Where(IsLikelyMaterialJson)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static Dictionary<string, MaterialInfo> LoadMaterialIndexFromJsonFiles(string root, string[] materialJsonFiles)
    {
        var result = new Dictionary<string, MaterialInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in materialJsonFiles)
        {
            try
            {
                var info = BuildMaterialInfo(root, path, JObject.Parse(File.ReadAllText(path)));
                if (!result.ContainsKey(info.Name))
                    result[info.Name] = info;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARN: material JSON skipped {path} ({ex.Message})");
            }
        }

        return result;
    }

    private static MaterialInfo BuildMaterialInfo(string root, string path, JObject obj)
    {
        var textures = obj["Textures"] as JObject;
        var parameters = obj["Parameters"] as JObject;
        var colors = parameters?["Colors"] as JObject;
        var scalars = parameters?["Scalars"] as JObject;
        var switches = parameters?["Switches"] as JObject;
        var fileInfo = new FileInfo(path);
        return new MaterialInfo
        {
            Name = Path.GetFileNameWithoutExtension(path),
            Path = path,
            RelativePath = MakeRelative(root, path),
            SizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
            LastWriteUtcTicks = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : 0,
            TextureSlotCount = textures?.Properties().Count() ?? 0,
            ColorCount = colors?.Properties().Count() ?? 0,
            ScalarCount = scalars?.Properties().Count() ?? 0,
            SwitchCount = switches?.Properties().Count() ?? 0,
            BlendMode = parameters?["BlendMode"]?.ToString(),
            ShadingModel = parameters?["ShadingModel"]?.ToString(),
            RawJson = obj,
        };
    }

    private static Dictionary<string, MaterialInfo> LoadMaterialIndexFromWorkDb(string root, string[]? expectedFiles)
    {
        using var connection = OpenLibraryWorkDbReadOnly(root);
        if (connection == null ||
            !TableExists(connection, "material_sidecars") ||
            CountTableRows(connection, "material_sidecars") == 0)
        {
            return [];
        }

        var result = new Dictionary<string, MaterialInfo>(StringComparer.OrdinalIgnoreCase);
        var cachedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expectedRelativePaths = expectedFiles?
            .Select(path => MakeRelative(root, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, relative_path, size_bytes, last_write_utc_ticks, texture_slot_count,
                   color_count, scalar_count, switch_count, blend_mode, shading_model, raw_json
            FROM material_sidecars
            ORDER BY relative_path;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var relativePath = GetString(reader, 1) ?? "";
            if (string.IsNullOrWhiteSpace(relativePath))
                continue;
            relativePath = relativePath.Replace('\\', '/');
            cachedRelativePaths.Add(relativePath);

            var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var fileInfo = new FileInfo(path);
            var sizeBytes = reader.GetInt64(2);
            var lastWriteUtcTicks = reader.GetInt64(3);
            if (!fileInfo.Exists ||
                fileInfo.Length != sizeBytes ||
                fileInfo.LastWriteTimeUtc.Ticks != lastWriteUtcTicks)
            {
                return [];
            }

            var name = GetString(reader, 0) ?? Path.GetFileNameWithoutExtension(path);
            if (result.ContainsKey(name))
                continue;

            result[name] = new MaterialInfo
            {
                Name = name,
                Path = path,
                RelativePath = relativePath,
                SizeBytes = sizeBytes,
                LastWriteUtcTicks = lastWriteUtcTicks,
                TextureSlotCount = reader.GetInt32(4),
                ColorCount = reader.GetInt32(5),
                ScalarCount = reader.GetInt32(6),
                SwitchCount = reader.GetInt32(7),
                BlendMode = GetString(reader, 8),
                ShadingModel = GetString(reader, 9),
                RawJson = JObject.Parse(GetString(reader, 10) ?? "{}"),
            };
        }

        if (expectedRelativePaths != null && !cachedRelativePaths.SetEquals(expectedRelativePaths))
            return [];

        return result;
    }

    private static void SaveMaterialIndexToWorkDb(string root, IEnumerable<MaterialInfo> materials)
    {
        var connection = OpenLibraryWorkDb(root, "material_sidecars");
        try
        {
            using var transaction = connection.BeginTransaction();
            foreach (var material in materials.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
                InsertMaterialSidecar(connection, transaction, material);

            transaction.Commit();
        }
        finally
        {
            FinalizeWorkDb(connection);
        }
    }

    private static ModelValidationEntry InspectModel(
        string root,
        string glbPath,
        Dictionary<string, MaterialInfo> materialIndex,
        string materialIndexSignature,
        out bool fromCache)
    {
        if (TryLoadModelValidationCache(root, glbPath, materialIndexSignature, out var cached))
        {
            fromCache = true;
            return cached;
        }

        fromCache = false;
        var notes = new List<string>();
        JObject gltf;
        byte[] binData;
        try
        {
            (gltf, binData) = ReadGltfModel(glbPath);
        }
        catch (Exception ex)
        {
            var errorReport = new ModelValidationEntry
            {
                Status = "error",
                Path = glbPath,
                RelativePath = MakeRelative(root, glbPath),
                Name = Path.GetFileNameWithoutExtension(glbPath),
                Notes = [$"glTF parse failed: {ex.Message}"],
            };
            WriteModelValidationCache(root, glbPath, materialIndexSignature, errorReport);
            return errorReport;
        }

        var nodes = ArrayOf(gltf, "nodes");
        var meshes = ArrayOf(gltf, "meshes");
        var materials = ArrayOf(gltf, "materials");
        var images = ArrayOf(gltf, "images");
        var skins = ArrayOf(gltf, "skins");
        var accessors = ArrayOf(gltf, "accessors");
        var animations = ArrayOf(gltf, "animations");
        var bufferViews = ArrayOf(gltf, "bufferViews");

        ValidateMeshes(meshes, materials.Length, accessors.Length, notes);
        ValidateSkins(skins, nodes.Length, accessors, notes);
        ValidateImages(images, bufferViews.Length, notes);
        if (meshes.Length == 0)
            notes.Add("No mesh was written.");
        if (materials.Length == 0)
            notes.Add("No material was written.");
        if (images.Length == 0)
            notes.Add("No embedded or referenced image was written.");

        var materialNames = materials
            .Select(x => ((string?)x["name"]) ?? "Material")
            .ToArray();
        var matchedMaterials = materialNames
            .Where(materialIndex.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var missingSidecars = materialNames
            .Where(x => !materialIndex.ContainsKey(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(64)
            .ToArray();
        if (missingSidecars.Length > 0)
            notes.Add($"Missing sidecar material JSON for {missingSidecars.Length} material(s).");

        var boneNames = ExtractSkinBoneNames(nodes, skins);
        var skeletonHash = boneNames.Length > 0 ? ComputeHash(string.Join("\n", boneNames)) : null;
        var bbox = EstimateBoundingBox(accessors, bufferViews, binData);
        if (bbox == null)
            notes.Add("POSITION accessor bounds were not available.");

        var report = new ModelValidationEntry
        {
            Status = notes.Count == 0 ? "ok" : "warning",
            Path = glbPath,
            RelativePath = MakeRelative(root, glbPath),
            Name = Path.GetFileNameWithoutExtension(glbPath),
            ResourceKind = InferResourceKind(glbPath),
            NodeCount = nodes.Length,
            MeshCount = meshes.Length,
            SkinCount = skins.Length,
            MaterialCount = materials.Length,
            ImageCount = images.Length,
            AnimationCount = animations.Length,
            EmbeddedImageCount = images.Count(x => x["bufferView"] != null),
            BoneCount = boneNames.Length,
            BoneNames = boneNames,
            SkeletonHash = skeletonHash,
            MaterialNames = materialNames,
            MatchedMaterialSidecars = matchedMaterials,
            MissingMaterialSidecars = missingSidecars,
            BBox = bbox,
            Notes = notes.ToArray(),
        };
        WriteModelValidationCache(root, glbPath, materialIndexSignature, report);
        return report;
    }

    private static string BuildMaterialIndexSignature(string root, IEnumerable<MaterialInfo> materials)
    {
        var parts = materials
            .OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(x =>
            {
                var info = new FileInfo(x.Path);
                return $"{x.RelativePath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            });
        return ComputeHash(string.Join('\n', parts));
    }

    private static bool TryLoadModelValidationCache(
        string root,
        string glbPath,
        string materialIndexSignature,
        out ModelValidationEntry report)
    {
        report = null!;
        if (TryLoadModelValidationCacheSqlite(root, glbPath, materialIndexSignature, out report))
            return true;

        var path = BuildModelValidationCachePath(glbPath);
        if (!File.Exists(path))
            return false;

        try
        {
            var modelInfo = new FileInfo(glbPath);
            var json = JObject.Parse(File.ReadAllText(path));
            if ((int?)json["cacheVersion"] != ModelValidationCacheVersion ||
                !string.Equals((string?)json["materialIndexSignature"], materialIndexSignature, StringComparison.Ordinal) ||
                (long?)json["modelSizeBytes"] != modelInfo.Length ||
                (long?)json["modelLastWriteUtcTicks"] != modelInfo.LastWriteTimeUtc.Ticks)
            {
                return false;
            }

            var data = json["report"] as JObject;
            if (data == null)
                return false;

            report = ReadModelValidationCacheReport(root, glbPath, data);
            try
            {
                WriteModelValidationCacheSqlite(root, glbPath, materialIndexSignature, report);
                TryDeleteLegacyModelValidationCache(glbPath);
            }
            catch
            {
                // 旧缓存已经命中，迁移失败只影响后续速度，不能让本次命中失效。
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteModelValidationCache(
        string root,
        string glbPath,
        string materialIndexSignature,
        ModelValidationEntry report)
    {
        try
        {
            WriteModelValidationCacheSqlite(root, glbPath, materialIndexSignature, report);
            TryDeleteLegacyModelValidationCache(glbPath);
        }
        catch
        {
            // 缓存只用于加速，写失败不能影响素材库后处理。
        }
    }

    private static bool TryLoadModelValidationCacheSqlite(
        string root,
        string glbPath,
        string materialIndexSignature,
        out ModelValidationEntry report)
    {
        report = null!;
        try
        {
            using var connection = OpenLibraryWorkDbReadOnly(root);
            if (connection == null ||
                !TableExists(connection, "model_validation_cache") ||
                !TableColumnExists(connection, "model_validation_cache", "report_json"))
            {
                return false;
            }

            var modelInfo = new FileInfo(glbPath);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT report_json
                FROM model_validation_cache
                WHERE relative_path = $relativePath
                  AND cache_version = $cacheVersion
                  AND model_size_bytes = $modelSizeBytes
                  AND model_last_write_utc_ticks = $modelLastWriteUtcTicks
                  AND material_index_signature = $materialIndexSignature
                LIMIT 1;
                """;
            Add(command, "$relativePath", NormalizeCatalogOutput(root, glbPath));
            Add(command, "$cacheVersion", ModelValidationCacheVersion);
            Add(command, "$modelSizeBytes", modelInfo.Length);
            Add(command, "$modelLastWriteUtcTicks", modelInfo.LastWriteTimeUtc.Ticks);
            Add(command, "$materialIndexSignature", materialIndexSignature);
            var rawJson = command.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(rawJson))
                return false;

            report = ReadModelValidationCacheReport(root, glbPath, JObject.Parse(rawJson));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteModelValidationCacheSqlite(
        string root,
        string glbPath,
        string materialIndexSignature,
        ModelValidationEntry report)
    {
        var connection = OpenLibraryWorkDb(root);
        try
        {
            var modelInfo = new FileInfo(glbPath);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO model_validation_cache (
                    relative_path, cache_version, model_size_bytes, model_last_write_utc_ticks,
                    material_index_signature, report_json, updated_at
                )
                VALUES (
                    $relativePath, $cacheVersion, $modelSizeBytes, $modelLastWriteUtcTicks,
                    $materialIndexSignature, $reportJson, $updatedAt
                )
                ON CONFLICT(relative_path) DO UPDATE SET
                    cache_version = excluded.cache_version,
                    model_size_bytes = excluded.model_size_bytes,
                    model_last_write_utc_ticks = excluded.model_last_write_utc_ticks,
                    material_index_signature = excluded.material_index_signature,
                    report_json = excluded.report_json,
                    updated_at = excluded.updated_at;
                """;
            Add(command, "$relativePath", NormalizeCatalogOutput(root, glbPath));
            Add(command, "$cacheVersion", ModelValidationCacheVersion);
            Add(command, "$modelSizeBytes", modelInfo.Length);
            Add(command, "$modelLastWriteUtcTicks", modelInfo.LastWriteTimeUtc.Ticks);
            Add(command, "$materialIndexSignature", materialIndexSignature);
            Add(command, "$reportJson", BuildModelValidationCacheReport(report).ToString(Formatting.None));
            Add(command, "$updatedAt", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        finally
        {
            FinalizeWorkDb(connection);
        }
    }

    private static void TryDeleteLegacyModelValidationCache(string glbPath)
    {
        try
        {
            var cachePath = BuildModelValidationCachePath(glbPath);
            if (File.Exists(cachePath))
                File.Delete(cachePath);
        }
        catch
        {
            // 缓存删不掉只会影响下一次扫描速度，不影响当前报告。
        }
    }

    private static string BuildModelValidationCachePath(string glbPath)
        => glbPath + ".ue_model_validation_cache.json";

    private static JObject BuildModelValidationCacheReport(ModelValidationEntry report)
    {
        return JObject.FromObject(new
        {
            report.Status,
            report.RelativePath,
            report.Name,
            report.ResourceKind,
            report.NodeCount,
            report.MeshCount,
            report.SkinCount,
            report.MaterialCount,
            report.ImageCount,
            report.EmbeddedImageCount,
            report.AnimationCount,
            report.BoneCount,
            report.BoneNames,
            report.SkeletonHash,
            report.MaterialNames,
            report.MatchedMaterialSidecars,
            report.MissingMaterialSidecars,
            report.ExternalMaterialNames,
            report.ExternalMaterialTextureCount,
            report.BBox,
            report.Notes,
        });
    }

    private static ModelValidationEntry ReadModelValidationCacheReport(string root, string glbPath, JObject data)
    {
        return new ModelValidationEntry
        {
            Status = (string?)data["Status"] ?? (string?)data["status"] ?? "unknown",
            Path = glbPath,
            RelativePath = (string?)data["RelativePath"] ?? MakeRelative(root, glbPath),
            Name = (string?)data["Name"] ?? Path.GetFileNameWithoutExtension(glbPath),
            ResourceKind = (string?)data["ResourceKind"] ?? "Unknown",
            NodeCount = (int?)data["NodeCount"] ?? 0,
            MeshCount = (int?)data["MeshCount"] ?? 0,
            SkinCount = (int?)data["SkinCount"] ?? 0,
            MaterialCount = (int?)data["MaterialCount"] ?? 0,
            ImageCount = (int?)data["ImageCount"] ?? 0,
            EmbeddedImageCount = (int?)data["EmbeddedImageCount"] ?? 0,
            AnimationCount = (int?)data["AnimationCount"] ?? 0,
            BoneCount = (int?)data["BoneCount"] ?? 0,
            BoneNames = ReadStringArray(data, "BoneNames"),
            SkeletonHash = (string?)data["SkeletonHash"],
            MaterialNames = ReadStringArray(data, "MaterialNames"),
            MatchedMaterialSidecars = ReadStringArray(data, "MatchedMaterialSidecars"),
            MissingMaterialSidecars = ReadStringArray(data, "MissingMaterialSidecars"),
            ExternalMaterialNames = ReadStringArray(data, "ExternalMaterialNames"),
            ExternalMaterialTextureCount = (int?)data["ExternalMaterialTextureCount"] ?? 0,
            BBox = data["BBox"]?.DeepClone(),
            Notes = ReadStringArray(data, "Notes"),
        };
    }

    private static string[] ReadStringArray(JObject data, string propertyName)
        => data[propertyName] is JArray array
            ? array.Select(x => (string?)x).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray()
            : [];

    private static (JObject Gltf, byte[] BinData) ReadGlb(string path)
    {
        using var fs = File.OpenRead(path);
        using var reader = new BinaryReader(fs);
        var magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (magic != "glTF")
            throw new InvalidDataException("Not a GLB file.");
        var version = reader.ReadUInt32();
        if (version != 2)
            throw new InvalidDataException($"Unsupported GLB version {version}.");
        _ = reader.ReadUInt32();
        var jsonLength = reader.ReadInt32();
        var jsonType = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (jsonType != "JSON")
            throw new InvalidDataException("GLB JSON chunk is missing.");
        var jsonText = Encoding.UTF8.GetString(reader.ReadBytes(jsonLength)).TrimEnd('\0', ' ', '\r', '\n', '\t');
        var gltf = JObject.Parse(jsonText);
        if (fs.Position >= fs.Length)
            return (gltf, []);

        var binLength = reader.ReadInt32();
        _ = reader.ReadBytes(4);
        var binData = reader.ReadBytes(binLength);
        return (gltf, binData);
    }

    private static void WriteGlb(string path, JObject gltf, byte[] binData)
    {
        var jsonData = Encoding.UTF8.GetBytes(gltf.ToString(Formatting.None));
        Array.Resize(ref jsonData, Align4(jsonData.Length));
        for (var i = jsonData.Length - 1; i >= 0 && jsonData[i] == 0; i--)
            jsonData[i] = 0x20;

        Array.Resize(ref binData, Align4(binData.Length));
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("glTF"));
        writer.Write(2);
        writer.Write(12 + 8 + jsonData.Length + 8 + binData.Length);
        writer.Write(jsonData.Length);
        writer.Write(Encoding.ASCII.GetBytes("JSON"));
        writer.Write(jsonData);
        writer.Write(binData.Length);
        writer.Write(Encoding.ASCII.GetBytes("BIN\0"));
        writer.Write(binData);
        File.WriteAllBytes(path, stream.ToArray());
    }

    private static int Align4(int value)
        => (value + 3) & ~3;

    private static (JObject Gltf, byte[] BinData) ReadGltfModel(string path)
    {
        if (path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            return ReadGlb(path);

        var gltf = JObject.Parse(File.ReadAllText(path));
        byte[] binData = [];
        if (gltf["buffers"] is JArray buffers && buffers.First is JObject buffer)
        {
            var uri = buffer["uri"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(uri) && !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var binPath = Path.Combine(Path.GetDirectoryName(path)!, Uri.UnescapeDataString(uri));
                if (File.Exists(binPath))
                    binData = File.ReadAllBytes(binPath);
            }
        }

        return (gltf, binData);
    }

    private static JObject[] ArrayOf(JObject obj, string name)
        => obj[name]?.OfType<JObject>().ToArray() ?? [];

    private static void ValidateMeshes(JObject[] meshes, int materialCount, int accessorCount, List<string> notes)
    {
        for (var meshIndex = 0; meshIndex < meshes.Length; meshIndex++)
        {
            var primitives = meshes[meshIndex]["primitives"]?.OfType<JObject>().ToArray() ?? [];
            if (primitives.Length == 0)
                notes.Add($"mesh[{meshIndex}] has no primitives.");

            for (var primitiveIndex = 0; primitiveIndex < primitives.Length; primitiveIndex++)
            {
                var primitive = primitives[primitiveIndex];
                var attributes = primitive["attributes"] as JObject;
                CheckAccessor(attributes, "POSITION", accessorCount, notes, $"mesh[{meshIndex}].primitive[{primitiveIndex}].POSITION");
                CheckAccessor(attributes, "NORMAL", accessorCount, notes, $"mesh[{meshIndex}].primitive[{primitiveIndex}].NORMAL", required: false);
                CheckAccessor(attributes, "TEXCOORD_0", accessorCount, notes, $"mesh[{meshIndex}].primitive[{primitiveIndex}].TEXCOORD_0", required: false);
                CheckAccessor(attributes, "JOINTS_0", accessorCount, notes, $"mesh[{meshIndex}].primitive[{primitiveIndex}].JOINTS_0", required: false);
                CheckAccessor(attributes, "WEIGHTS_0", accessorCount, notes, $"mesh[{meshIndex}].primitive[{primitiveIndex}].WEIGHTS_0", required: false);

                var material = (int?)primitive["material"];
                if (material != null && (material < 0 || material >= materialCount))
                    notes.Add($"mesh[{meshIndex}].primitive[{primitiveIndex}] points to invalid material {material}.");
            }
        }
    }

    private static void CheckAccessor(
        JObject? attributes,
        string name,
        int accessorCount,
        List<string> notes,
        string label,
        bool required = true)
    {
        var value = (int?)attributes?[name];
        if (value == null)
        {
            if (required)
                notes.Add($"{label} is missing.");
            return;
        }

        if (value < 0 || value >= accessorCount)
            notes.Add($"{label} points to invalid accessor {value}.");
    }

    private static void ValidateSkins(JObject[] skins, int nodeCount, JObject[] accessors, List<string> notes)
    {
        for (var i = 0; i < skins.Length; i++)
        {
            var joints = skins[i]["joints"]?.Select(x => (int?)x).ToArray() ?? [];
            if (joints.Length == 0)
                notes.Add($"skin[{i}] has no joints.");
            foreach (var joint in joints)
            {
                if (joint == null || joint < 0 || joint >= nodeCount)
                    notes.Add($"skin[{i}] has invalid joint node {joint}.");
            }

            var inverseBindMatrices = (int?)skins[i]["inverseBindMatrices"];
            if (inverseBindMatrices == null || inverseBindMatrices < 0 || inverseBindMatrices >= accessors.Length)
                notes.Add($"skin[{i}] has invalid inverseBindMatrices accessor {inverseBindMatrices}.");
        }
    }

    private static void ValidateImages(JObject[] images, int bufferViewCount, List<string> notes)
    {
        for (var i = 0; i < images.Length; i++)
        {
            var uri = (string?)images[i]["uri"];
            var bufferView = (int?)images[i]["bufferView"];
            if (!string.IsNullOrWhiteSpace(uri))
                continue;
            if (bufferView == null || bufferView < 0 || bufferView >= bufferViewCount)
                notes.Add($"image[{i}] has neither valid uri nor valid bufferView.");
        }
    }

    private static string[] ExtractSkinBoneNames(JObject[] nodes, JObject[] skins)
    {
        var names = new List<string>();
        foreach (var skin in skins)
        {
            foreach (var joint in skin["joints"]?.Select(x => (int?)x) ?? [])
            {
                if (joint == null || joint < 0 || joint >= nodes.Length)
                    continue;
                names.Add(((string?)nodes[joint.Value]["name"]) ?? $"node_{joint.Value}");
            }
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static object? EstimateBoundingBox(JObject[] accessors, JObject[] bufferViews, byte[] binData)
    {
        var positionAccessors = accessors
            .Where(x => ((string?)x["type"]) == "VEC3" && (int?)x["componentType"] == 5126)
            .Where(x => x["min"] is JArray && x["max"] is JArray)
            .ToArray();
        if (positionAccessors.Length == 0)
            return null;

        var min = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var max = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        foreach (var accessor in positionAccessors)
        {
            var aMin = accessor["min"]!.Select(x => (double)x).ToArray();
            var aMax = accessor["max"]!.Select(x => (double)x).ToArray();
            for (var i = 0; i < 3; i++)
            {
                min[i] = Math.Min(min[i], aMin[i]);
                max[i] = Math.Max(max[i], aMax[i]);
            }
        }

        return new
        {
            min,
            max,
            size = new[] { max[0] - min[0], max[1] - min[1], max[2] - min[2] },
        };
    }

    private static JObject BuildModelCatalogRow(ModelValidationEntry report)
    {
        return JObject.FromObject(new
        {
            kind = "Model",
            resourceKind = report.ResourceKind,
            name = report.Name,
            sourceType = report.SkinCount > 0 ? "SkeletalOrSkinnedMeshGltf" : "StaticMeshGltf",
            source = report.RelativePath,
            output = report.RelativePath,
            format = Path.GetExtension(report.RelativePath).TrimStart('.').ToLowerInvariant(),
            meshCount = report.MeshCount,
            materialCount = report.MaterialCount,
            textureCount = report.ImageCount,
            embeddedImageCount = report.EmbeddedImageCount,
            boneCount = report.BoneCount,
            skinCount = report.SkinCount,
            animationCount = report.AnimationCount,
            skeletonHash = report.SkeletonHash,
            materialNames = report.MaterialNames,
            materialSidecars = report.MatchedMaterialSidecars,
            externalMaterialNames = report.ExternalMaterialNames,
            externalMaterialTextureCount = report.ExternalMaterialTextureCount,
            validationStatus = report.Status,
            notes = report.Notes,
            bbox = report.BBox,
        });
    }

    private static JObject BuildMaterialCatalogRow(MaterialInfo material)
    {
        return JObject.FromObject(new
        {
            kind = "Material",
            resourceKind = "Material",
            name = material.Name,
            sourceType = "UMaterialInterfaceSidecar",
            source = material.RelativePath,
            output = material.RelativePath,
            textureSlotCount = material.TextureSlotCount,
            colorCount = material.ColorCount,
            scalarCount = material.ScalarCount,
            switchCount = material.SwitchCount,
            blendMode = material.BlendMode,
            shadingModel = material.ShadingModel,
        });
    }

    private static JObject BuildTextureCatalogRow(TextureLinkInfo texture)
    {
        return JObject.FromObject(new
        {
            kind = "Texture",
            resourceKind = texture.Extension.Equals(".hdr", StringComparison.OrdinalIgnoreCase) ? "HDRTexture" : "Texture2D",
            name = Path.GetFileNameWithoutExtension(texture.RelativePath),
            sourceType = "ExportedTextureFile",
            source = texture.RelativePath,
            output = texture.RelativePath,
            format = texture.Extension.TrimStart('.'),
            sha256 = texture.Hash,
            sizeBytes = texture.SizeBytes,
            sharedOutput = texture.SharedRelativePath,
            hardLinked = texture.HardLinked,
            linkError = texture.LinkError,
        });
    }

    private static List<JObject> WriteAssetCatalog(string root, List<JObject> rows, bool writeCompatibilityJson)
    {
        var path = Path.Combine(root, "asset_catalog.jsonl");
        var mergedRows = MergeExistingCatalogRows(root, path, rows);
        if (!writeCompatibilityJson)
        {
            DeleteIfExists(path);
            return mergedRows;
        }

        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        foreach (var row in mergedRows)
            writer.WriteLine(row.ToString(Formatting.None));
        return mergedRows;
    }

    private static List<JObject> MergeExistingCatalogRows(string root, string catalogPath, List<JObject> generatedRows)
    {
        var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in ReadExportEventRows(root, "asset_catalog", catalogPath))
        {
            try
            {
                if (IsCatalogCacheRow(root, row))
                    continue;

                result[BuildCatalogKey(root, row)] = row;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARN: old catalog row skipped ({ex.Message})");
            }
        }

        foreach (var generated in generatedRows)
        {
            var key = BuildCatalogKey(root, generated);
            if (!result.TryGetValue(key, out var existing))
            {
                result[key] = generated;
                continue;
            }

            var merged = (JObject)existing.DeepClone();
            merged.Merge(generated, new JsonMergeSettings
            {
                MergeArrayHandling = MergeArrayHandling.Replace,
                MergeNullValueHandling = MergeNullValueHandling.Ignore,
            });

            // 导出主链路写入的 UE 源关系更可信，验证重建只补结构检查结果。
            PreserveExistingField(existing, merged, "source");
            PreserveExistingField(existing, merged, "sourceType");
            PreserveExistingField(existing, merged, "objectPath");
            PreserveExistingField(existing, merged, "skeletonPath");
            PreserveExistingField(existing, merged, "skeletonName");
            result[key] = merged;
        }

        return result.Values
            .Where(x => !IsCatalogCacheRow(root, x))
            .OrderBy(x => (string?)x["kind"], StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => (string?)x["output"] ?? (string?)x["source"], StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsCatalogCacheRow(string root, JObject row)
    {
        var path = ((string?)row["output"] ?? (string?)row["source"] ?? "").Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (Path.IsPathRooted(path))
            path = MakeRelative(root, Path.GetFullPath(path)).Replace('\\', '/');

        return path.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.StartsWith(".", StringComparison.Ordinal));
    }

    private static void PreserveExistingField(JObject existing, JObject merged, string name)
    {
        if (existing.TryGetValue(name, out var value) && value.Type != JTokenType.Null)
            merged[name] = value.DeepClone();
    }

    private static string BuildCatalogKey(string root, JObject row)
    {
        var kind = ((string?)row["kind"] ?? "Asset").ToLowerInvariant();
        var output = ((string?)row["output"] ?? (string?)row["source"] ?? (string?)row["name"] ?? "").Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(output))
        {
            var fullPath = Path.IsPathRooted(output) ? Path.GetFullPath(output) : Path.GetFullPath(Path.Combine(root, output));
            output = MakeRelative(root, fullPath).Replace('\\', '/').ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(output))
            return $"{kind}|{output}";

        var objectPath = ((string?)row["objectPath"] ?? "").ToLowerInvariant();
        return $"{kind}|{objectPath}";
    }

    private static SourceIndexSnapshot LoadSourceIndex(string root)
    {
        var dbPath = Path.Combine(root, "ue_source_index.db");
        var snapshot = new SourceIndexSnapshot
        {
            Path = dbPath,
            Available = File.Exists(dbPath),
            UnsupportedAutoReferencedAnimationPaths = LoadUnsupportedAutoReferencedAnimationPaths(root),
        };
        if (!snapshot.Available)
            return snapshot;

        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            snapshot.BonesByOwner = LoadBonesByOwner(connection);
            snapshot.BonesBySkeleton = snapshot.BonesByOwner.Values
                .SelectMany(x => x)
                .Where(x => !string.IsNullOrWhiteSpace(x.SkeletonPath))
                .GroupBy(x => x.SkeletonPath!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
            snapshot.TracksByAnimation = LoadTracksByAnimation(connection);
            snapshot.SegmentsByAnimation = LoadSegmentsByAnimation(connection);
            snapshot.MaterialTextureSlots = LoadSourceMaterialTextureSlots(connection);
            snapshot.ComponentAssetRelationCount = CountTableRows(connection, "component_asset_relations");
            snapshot.ComponentAssetRelations = [];
            snapshot.AssetRegistryDependencies = LoadAssetRegistryDependencies(connection);
            snapshot.AnimBlueprintAnimationRefs = LoadAnimBlueprintAnimationRefs(connection);
            snapshot.UnsupportedAnimationObjectPaths = LoadUnsupportedAnimationObjectPaths(connection);
            snapshot.PackageObjectMapCount = CountTableRows(connection, "package_object_maps");
            snapshot.PackageObjectMaps = [];
        }
        catch (Exception ex)
        {
            snapshot.Available = false;
            snapshot.Error = ex.Message;
            Console.WriteLine($"WARN: ue_source_index.db skipped ({ex.Message})");
        }

        return snapshot;
    }

    private static Dictionary<string, SourceBone[]> LoadBonesByOwner(SqliteConnection connection)
    {
        var result = new Dictionary<string, List<SourceBone>>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_path, owner_object_path, owner_type, skeleton_path, bone_index, bone_name, parent_index
            FROM skeleton_bones
            ORDER BY owner_object_path, bone_index;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var ownerObjectPath = GetString(reader, 1);
            if (string.IsNullOrWhiteSpace(ownerObjectPath))
                continue;

            if (!result.TryGetValue(ownerObjectPath, out var bones))
            {
                bones = [];
                result[ownerObjectPath] = bones;
            }

            bones.Add(new SourceBone
            {
                SourcePath = GetString(reader, 0),
                OwnerObjectPath = ownerObjectPath,
                OwnerType = GetString(reader, 2) ?? "",
                SkeletonPath = GetString(reader, 3),
                BoneIndex = reader.GetInt32(4),
                BoneName = GetString(reader, 5) ?? "",
                ParentIndex = reader.GetInt32(6),
            });
        }

        return result.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, SourceAnimationTrack[]> LoadTracksByAnimation(SqliteConnection connection)
    {
        var result = new Dictionary<string, List<SourceAnimationTrack>>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_path, animation_object_path, skeleton_path, track_index, bone_index, bone_name
            FROM animation_tracks
            ORDER BY animation_object_path, track_index;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var animationObjectPath = GetString(reader, 1);
            if (string.IsNullOrWhiteSpace(animationObjectPath))
                continue;

            if (!result.TryGetValue(animationObjectPath, out var tracks))
            {
                tracks = [];
                result[animationObjectPath] = tracks;
            }

            tracks.Add(new SourceAnimationTrack
            {
                SourcePath = GetString(reader, 0),
                AnimationObjectPath = animationObjectPath,
                SkeletonPath = GetString(reader, 2),
                TrackIndex = reader.GetInt32(3),
                BoneIndex = reader.GetInt32(4),
                BoneName = GetString(reader, 5),
            });
        }

        return result.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, SourceAnimationSegment[]> LoadSegmentsByAnimation(SqliteConnection connection)
    {
        if (!TableExists(connection, "animation_segments"))
            return new Dictionary<string, SourceAnimationSegment[]>(StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, List<SourceAnimationSegment>>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_path, animation_object_path, skeleton_path, segment_index, slot_name,
                   referenced_animation_path, referenced_animation_name,
                   start_pos, anim_start_time, anim_end_time, play_rate, looping_count, length, relation_source
            FROM animation_segments
            ORDER BY animation_object_path, segment_index;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var animationObjectPath = GetString(reader, 1);
            if (string.IsNullOrWhiteSpace(animationObjectPath))
                continue;

            var normalizedPath = NormalizePackageObjectPath(animationObjectPath);
            if (!result.TryGetValue(normalizedPath, out var segments))
            {
                segments = [];
                result[normalizedPath] = segments;
            }

            segments.Add(new SourceAnimationSegment
            {
                SourcePath = GetString(reader, 0),
                AnimationObjectPath = animationObjectPath,
                SkeletonPath = GetString(reader, 2),
                SegmentIndex = reader.GetInt32(3),
                SlotName = GetString(reader, 4),
                ReferencedAnimationPath = GetString(reader, 5),
                ReferencedAnimationName = GetString(reader, 6),
                StartPos = GetNullableDouble(reader, 7),
                AnimStartTime = GetNullableDouble(reader, 8),
                AnimEndTime = GetNullableDouble(reader, 9),
                PlayRate = GetNullableDouble(reader, 10),
                LoopingCount = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                Length = GetNullableDouble(reader, 12),
                RelationSource = GetString(reader, 13) ?? "",
            });
        }

        return result.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static SourceMaterialTextureSlot[] LoadSourceMaterialTextureSlots(SqliteConnection connection)
    {
        var hasTextureClassColumns = TableColumnExists(connection, "material_texture_slots", "texture_class_name");
        using var command = connection.CreateCommand();
        command.CommandText = hasTextureClassColumns
            ? """
                SELECT source_path, material_object_path, material_name, slot_name,
                       texture_path, texture_name, texture_object_path,
                       texture_class_name, texture_class_path, relation_source
                FROM material_texture_slots
                ORDER BY material_object_path, slot_name, texture_object_path, relation_source;
                """
            : """
                SELECT source_path, material_object_path, material_name, slot_name,
                       texture_path, texture_name, texture_object_path, relation_source
                FROM material_texture_slots
                ORDER BY material_object_path, slot_name, texture_object_path, relation_source;
                """;
        using var reader = command.ExecuteReader();
        var result = new List<SourceMaterialTextureSlot>();
        while (reader.Read())
        {
            result.Add(new SourceMaterialTextureSlot
            {
                SourcePath = GetString(reader, 0),
                MaterialObjectPath = GetString(reader, 1),
                MaterialName = GetString(reader, 2),
                SlotName = GetString(reader, 3),
                TexturePath = GetString(reader, 4),
                TextureName = GetString(reader, 5),
                TextureObjectPath = GetString(reader, 6),
                TextureClassName = hasTextureClassColumns ? GetString(reader, 7) : null,
                TextureClassPath = hasTextureClassColumns ? GetString(reader, 8) : null,
                RelationSource = GetString(reader, hasTextureClassColumns ? 9 : 7) ?? "",
            });
        }

        return result.ToArray();
    }

    private static SourceComponentAssetRelation[] LoadSourceComponentAssetRelations(SqliteConnection connection)
    {
        if (!TableExists(connection, "component_asset_relations"))
            return [];

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_path, owner_object_path, owner_type,
                   component_object_path, component_type, component_name, component_variable_name,
                   relation_source, relation_type, target_path, target_name,
                   socket_name, parent_component_path,
                   location_x, location_y, location_z,
                   rotation_pitch, rotation_yaw, rotation_roll,
                   scale_x, scale_y, scale_z
            FROM component_asset_relations;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<SourceComponentAssetRelation>();
        while (reader.Read())
            result.Add(ReadSourceComponentAssetRelation(reader));

        return result.ToArray();
    }

    private static IEnumerable<SourceComponentAssetRelation> EnumerateSourceComponentAssetRelations(SourceIndexSnapshot sourceIndex)
    {
        if (sourceIndex.ComponentAssetRelations.Length > 0)
        {
            foreach (var relation in sourceIndex.ComponentAssetRelations)
                yield return relation;
            yield break;
        }

        if (!sourceIndex.Available || string.IsNullOrWhiteSpace(sourceIndex.Path) || !File.Exists(sourceIndex.Path))
            yield break;

        using var connection = new SqliteConnection($"Data Source={sourceIndex.Path};Mode=ReadOnly");
        connection.Open();
        if (!TableExists(connection, "component_asset_relations"))
            yield break;

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_path, owner_object_path, owner_type,
                   component_object_path, component_type, component_name, component_variable_name,
                   relation_source, relation_type, target_path, target_name,
                   socket_name, parent_component_path,
                   location_x, location_y, location_z,
                   rotation_pitch, rotation_yaw, rotation_roll,
                   scale_x, scale_y, scale_z
            FROM component_asset_relations;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
            yield return ReadSourceComponentAssetRelation(reader);
    }

    private static SourceComponentAssetRelation ReadSourceComponentAssetRelation(SqliteDataReader reader)
        => new()
        {
            SourcePath = GetString(reader, 0),
            OwnerObjectPath = GetString(reader, 1),
            OwnerType = GetString(reader, 2),
            ComponentObjectPath = GetString(reader, 3),
            ComponentType = GetString(reader, 4),
            ComponentName = GetString(reader, 5),
            ComponentVariableName = GetString(reader, 6),
            RelationSource = GetString(reader, 7) ?? "",
            RelationType = GetString(reader, 8) ?? "",
            TargetPath = GetString(reader, 9),
            TargetName = GetString(reader, 10),
            SocketName = GetString(reader, 11),
            ParentComponentPath = GetString(reader, 12),
            LocationX = GetNullableDouble(reader, 13),
            LocationY = GetNullableDouble(reader, 14),
            LocationZ = GetNullableDouble(reader, 15),
            RotationPitch = GetNullableDouble(reader, 16),
            RotationYaw = GetNullableDouble(reader, 17),
            RotationRoll = GetNullableDouble(reader, 18),
            ScaleX = GetNullableDouble(reader, 19),
            ScaleY = GetNullableDouble(reader, 20),
            ScaleZ = GetNullableDouble(reader, 21),
        };

    private static SourceAssetRegistryDependency[] LoadAssetRegistryDependencies(SqliteConnection connection)
    {
        if (!TableExists(connection, "asset_registry_dependencies"))
            return [];

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT registry_path, source_package, source_asset_name, source_asset_class, source_identifier,
                   dependency_package, dependency_asset_name, dependency_asset_class, dependency_identifier,
                   dependency_category, dependency_kind, dependency_flags, dependency_flags_text,
                   is_hard_package, is_soft_package, is_name_dependency, is_manage_dependency,
                   relation_source, raw_json
            FROM asset_registry_dependencies
            ORDER BY source_package, dependency_package, dependency_category, dependency_kind;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<SourceAssetRegistryDependency>();
        while (reader.Read())
        {
            result.Add(new SourceAssetRegistryDependency
            {
                RegistryPath = GetString(reader, 0),
                SourcePackage = NormalizePackageName(GetString(reader, 1)),
                SourceAssetName = GetString(reader, 2),
                SourceAssetClass = GetString(reader, 3),
                SourceIdentifier = GetString(reader, 4),
                DependencyPackage = NormalizePackageName(GetString(reader, 5)),
                DependencyAssetName = GetString(reader, 6),
                DependencyAssetClass = GetString(reader, 7),
                DependencyIdentifier = GetString(reader, 8),
                DependencyCategory = GetString(reader, 9) ?? "",
                DependencyKind = GetString(reader, 10) ?? "",
                DependencyFlags = reader.IsDBNull(11) ? null : reader.GetInt32(11),
                DependencyFlagsText = GetString(reader, 12),
                IsHardPackage = !reader.IsDBNull(13) && reader.GetInt32(13) != 0,
                IsSoftPackage = !reader.IsDBNull(14) && reader.GetInt32(14) != 0,
                IsNameDependency = !reader.IsDBNull(15) && reader.GetInt32(15) != 0,
                IsManageDependency = !reader.IsDBNull(16) && reader.GetInt32(16) != 0,
                RelationSource = GetString(reader, 17) ?? "",
                RawJson = GetString(reader, 18) ?? "{}",
            });
        }

        return result.ToArray();
    }

    private static SourceAnimBlueprintAnimationRef[] LoadAnimBlueprintAnimationRefs(SqliteConnection connection)
    {
        if (!TableExists(connection, "anim_blueprint_animation_refs"))
            return [];

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_path, anim_blueprint_object_path, generated_class_object_path,
                   referenced_animation_path, referenced_animation_name, referenced_animation_type,
                   property_path, node_type, skeleton_path, relation_source, confidence_hint, raw_json
            FROM anim_blueprint_animation_refs
            ORDER BY anim_blueprint_object_path, referenced_animation_path, property_path;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<SourceAnimBlueprintAnimationRef>();
        while (reader.Read())
        {
            result.Add(new SourceAnimBlueprintAnimationRef
            {
                SourcePath = GetString(reader, 0),
                AnimBlueprintObjectPath = NormalizePackageObjectPath(GetString(reader, 1)),
                GeneratedClassObjectPath = NormalizePackageObjectPath(GetString(reader, 2)),
                ReferencedAnimationPath = NormalizePackageObjectPath(GetString(reader, 3)),
                ReferencedAnimationName = GetString(reader, 4),
                ReferencedAnimationType = GetString(reader, 5),
                PropertyPath = GetString(reader, 6),
                NodeType = GetString(reader, 7),
                SkeletonPath = GetString(reader, 8),
                RelationSource = GetString(reader, 9) ?? "",
                ConfidenceHint = GetString(reader, 10) ?? "",
                RawJson = GetString(reader, 11) ?? "{}",
            });
        }

        return result.ToArray();
    }

    private static HashSet<string> LoadUnsupportedAnimationObjectPaths(SqliteConnection connection)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TableExists(connection, "source_objects"))
            return result;

        var hasObjectType = TableColumnExists(connection, "source_objects", "object_type");
        var hasExportType = TableColumnExists(connection, "source_objects", "export_type");
        if (!hasObjectType && !hasExportType)
            return result;

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT object_path, object_type, export_type, name
            FROM source_objects
            WHERE object_path IS NOT NULL
              AND (
                    object_type LIKE '%BlendSpace%' OR export_type LIKE '%BlendSpace%'
                 OR object_type LIKE '%AimOffset%' OR export_type LIKE '%AimOffset%'
                 OR object_type LIKE '%AnimBlueprint%' OR export_type LIKE '%AnimBlueprint%'
              );
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var objectPath = GetString(reader, 0);
            if (string.IsNullOrWhiteSpace(objectPath))
                continue;

            var typeText = $"{GetString(reader, 1)} {GetString(reader, 2)} {GetString(reader, 3)} {objectPath}";
            if (IsUnsupportedAnimationTypeText(typeText))
                result.Add(objectPath);
        }

        return result;
    }

    private static HashSet<string> LoadUnsupportedAutoReferencedAnimationPaths(string root)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(root, "auto_referenced_exports.jsonl");
        if (!File.Exists(path))
            return result;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var row = JObject.Parse(line);
                var stage = (string?)row["stage"];
                var status = (string?)row["status"];
                var relationType = (string?)row["relationType"];
                var outputType = (string?)row["outputType"];
                var reason = (string?)row["reason"];
                var targetPath = (string?)row["targetPath"];
                if (string.Equals(stage, "export", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(status, "notExported", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(relationType, "Animation", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(outputType, "ueanim", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(reason, "matchedPackageButNoExportedObject", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(targetPath))
                {
                    // 包存在但没有 UAnimSequence/Montage/Composite 可写对象，说明这是容器/编辑器动画资产。
                    // 它应作为关系元数据保留，不能继续报成“缺少已导出素材”。
                    result.Add(targetPath!);
                }
            }
            catch (JsonException)
            {
                // 旧日志里如果混入截断行，跳过该行即可；源索引仍是主要证据。
            }
        }

        return result;
    }

    private static SourcePackageObjectMap[] LoadSourcePackageObjectMaps(SqliteConnection connection)
    {
        if (!TableExists(connection, "package_object_maps"))
            return [];

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_path, package_name, map_type, map_index,
                   object_name, object_path, class_name, class_path,
                   outer_path, super_path, template_path, target_package,
                   is_asset, is_optional, object_flags, serial_size, public_export_hash, raw_json
            FROM package_object_maps
            ORDER BY source_path, map_type, map_index;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<SourcePackageObjectMap>();
        while (reader.Read())
        {
            result.Add(new SourcePackageObjectMap
            {
                SourcePath = GetString(reader, 0),
                PackageName = GetString(reader, 1),
                MapType = GetString(reader, 2) ?? "",
                MapIndex = reader.GetInt32(3),
                ObjectName = GetString(reader, 4),
                ObjectPath = GetString(reader, 5),
                ClassName = GetString(reader, 6),
                ClassPath = GetString(reader, 7),
                OuterPath = GetString(reader, 8),
                SuperPath = GetString(reader, 9),
                TemplatePath = GetString(reader, 10),
                TargetPackage = GetString(reader, 11),
                IsAsset = GetNullableBool(reader, 12),
                IsOptional = GetNullableBool(reader, 13),
                ObjectFlags = GetString(reader, 14),
                SerialSize = reader.IsDBNull(15) ? null : reader.GetInt64(15),
                PublicExportHash = GetString(reader, 16),
                RawJson = GetString(reader, 17) ?? "{}",
            });
        }

        return result.ToArray();
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        Add(command, "$name", tableName);
        return command.ExecuteScalar() != null;
    }

    private static bool TableColumnExists(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(GetString(reader, 1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static int CountTableRows(SqliteConnection connection, string tableName)
    {
        if (!TableExists(connection, tableName))
            return 0;

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static MaterialTextureSlotLink[] WriteMaterialTextureSlotLinks(
        string root,
        Dictionary<string, MaterialInfo> materialIndex,
        List<TextureLinkInfo> textureLinks,
        SourceIndexSnapshot sourceIndex,
        bool writeCompatibilityJson)
    {
        var links = BuildMaterialTextureSlotLinks(materialIndex, textureLinks, sourceIndex);
        var path = Path.Combine(root, "material_texture_slots.jsonl");
        if (!writeCompatibilityJson)
            DeleteIfExists(path);

        using var workConnection = writeCompatibilityJson ? null : OpenLibraryWorkDb(root, "material_texture_slots");
        using var workTransaction = workConnection?.BeginTransaction();
        using (var writer = writeCompatibilityJson ? new StreamWriter(path, false, Encoding.UTF8) : null)
        {
            foreach (var link in links.OrderBy(x => x.MaterialName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.SlotName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.TextureObjectPath, StringComparer.OrdinalIgnoreCase))
            {
                if (writeCompatibilityJson)
                {
                    writer!.WriteLine(JsonConvert.SerializeObject(new
                    {
                        kind = "MaterialTextureSlot",
                        link.MaterialName,
                        link.MaterialPath,
                        link.MaterialObjectPath,
                        link.SlotName,
                        link.TextureName,
                        link.TextureObjectPath,
                        link.TexturePath,
                        link.TextureClassName,
                        link.TextureClassPath,
                        link.MissingCategory,
                        link.ExportedTexture,
                        link.SharedTexture,
                        link.Sha256,
                        link.HardLinked,
                        link.MatchStatus,
                        link.MatchReason,
                        link.RelationSource,
                    }));
                }
                else
                {
                    InsertMaterialTextureSlot(workConnection!, workTransaction!, link);
                }
            }
        }

        workTransaction?.Commit();
        FinalizeWorkDb(workConnection);
        return links;
    }

    private static MaterialTextureSlotLink[] BuildMaterialTextureSlotLinks(
        Dictionary<string, MaterialInfo> materialIndex,
        List<TextureLinkInfo> textureLinks,
        SourceIndexSnapshot sourceIndex)
    {
        if (!sourceIndex.Available || sourceIndex.MaterialTextureSlots.Length == 0)
            return [];

        var textureObjects = sourceIndex.PackageObjectMaps
            .Where(x => !string.IsNullOrWhiteSpace(x.ObjectPath))
            .GroupBy(x => x.ObjectPath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                x => x.Key,
                x => x.OrderBy(y => string.Equals(y.MapType, "Export", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(y => y.IsAsset == true ? 0 : 1)
                    .First(),
                StringComparer.OrdinalIgnoreCase);

        // 材质槽数量可能非常大，不能每个槽都线性扫描全部贴图。
        var textureLookup = BuildTextureLinkLookup(textureLinks);
        var result = new List<MaterialTextureSlotLink>();
        foreach (var slot in sourceIndex.MaterialTextureSlots)
        {
            var material = FindMaterialInfo(materialIndex, slot.MaterialName);
            var textureLink = FindTextureLink(textureLookup, slot);
            var textureInfo = FindTextureObjectInfo(textureObjects, slot);
            var textureClassName = slot.TextureClassName ?? textureInfo?.ClassName;
            var textureClassPath = slot.TextureClassPath ?? textureInfo?.ClassPath;
            var missingCategory = textureLink == null ? ClassifyMissingTextureSlot(slot, textureInfo, textureClassName, textureClassPath) : null;
            result.Add(new MaterialTextureSlotLink
            {
                MaterialName = slot.MaterialName ?? "",
                MaterialPath = material?.RelativePath,
                MaterialObjectPath = slot.MaterialObjectPath,
                SlotName = slot.SlotName ?? "",
                TextureName = slot.TextureName,
                TextureObjectPath = slot.TextureObjectPath,
                TexturePath = slot.TexturePath,
                TextureClassName = textureClassName,
                TextureClassPath = textureClassPath,
                MissingCategory = missingCategory,
                ExportedTexture = textureLink?.RelativePath,
                SharedTexture = textureLink?.SharedRelativePath,
                Sha256 = textureLink?.Hash,
                HardLinked = textureLink?.HardLinked,
                MatchStatus = textureLink == null ? BuildMissingTextureStatus(missingCategory) : "matched",
                MatchReason = textureLink == null
                    ? BuildMissingTextureReason(missingCategory, textureClassName)
                    : "通过 UE texture object path / texture name 匹配到已导出贴图，并关联共享贴图。",
                RelationSource = slot.RelationSource,
            });
        }

        return result.ToArray();
    }

    private static void ApplyExternalMaterialValidation(
        string root,
        List<ModelValidationEntry> reports,
        List<JObject> catalogRows,
        MaterialTextureSlotLink[] materialTextureSlots)
    {
        var modelRowsByOutput = catalogRows
            .Where(x => string.Equals((string?)x["kind"], "Model", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace((string?)x["output"]))
            .GroupBy(x => NormalizeCatalogOutput(root, (string)x["output"]!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var slotsByMaterial = materialTextureSlots
            .Where(x => !string.IsNullOrWhiteSpace(x.MaterialName))
            .GroupBy(x => x.MaterialName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var report in reports)
        {
            if (!modelRowsByOutput.TryGetValue(NormalizeCatalogOutput(root, report.RelativePath), out var modelRow))
                continue;

            var materialNames = BuildModelMaterialCandidates(report, modelRow);
            var matchedMaterials = materialNames
                .Where(name => slotsByMaterial.TryGetValue(name, out var slots) &&
                               slots.Any(slot => string.Equals(slot.MatchStatus, "matched", StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var matchedTextureCount = matchedMaterials
                .SelectMany(name => slotsByMaterial[name])
                .Count(slot => string.Equals(slot.MatchStatus, "matched", StringComparison.OrdinalIgnoreCase));

            report.ExternalMaterialNames = matchedMaterials;
            report.ExternalMaterialTextureCount = matchedTextureCount;
            var hasExternalMaterial = matchedTextureCount > 0 || report.MatchedMaterialSidecars.Length > 0;
            if (!hasExternalMaterial)
                continue;

            report.Notes = report.Notes
                .Where(note => !string.Equals(note, "No embedded or referenced image was written.", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var stillMissingSidecars = report.MissingMaterialSidecars
                .Where(name => !matchedMaterials.Contains(name, StringComparer.OrdinalIgnoreCase) &&
                               !(IsGenericGltfMaterialName(name) && matchedMaterials.Length > 0))
                .ToArray();
            if (stillMissingSidecars.Length != report.MissingMaterialSidecars.Length)
            {
                report.MissingMaterialSidecars = stillMissingSidecars;
                report.Notes = report.Notes
                    .Where(note => !note.StartsWith("Missing sidecar material JSON for ", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (stillMissingSidecars.Length > 0)
                    report.Notes = report.Notes.Concat([$"Missing sidecar material JSON for {stillMissingSidecars.Length} material(s)."]).ToArray();
            }

            report.Status = report.Notes.Length == 0 ? "ok" : report.Status;
        }
    }

    private static string NormalizeCatalogOutput(string root, string path)
    {
        var text = path.Replace('\\', '/');
        if (Path.IsPathRooted(text))
            text = MakeRelative(root, text).Replace('\\', '/');
        return text.TrimStart('/').ToLowerInvariant();
    }

    private static string[] BuildModelMaterialCandidates(ModelValidationEntry report, JObject modelRow)
    {
        var result = new List<string>();
        result.AddRange(report.MaterialNames);

        foreach (var slot in (JArray?)modelRow["materialSlots"] ?? [])
        {
            AddIfNotEmpty(result, (string?)slot["materialName"]);
            AddIfNotEmpty(result, (string?)slot["slotName"]);
            AddIfNotEmpty(result, (string?)slot["importedSlotName"]);
        }

        var sourcePath = ((string?)modelRow["source"] ?? report.RelativePath).Replace('\\', '/');
        var modelBaseName = Path.GetFileNameWithoutExtension(sourcePath);
        foreach (var materialName in report.MissingMaterialSidecars)
        {
            if (string.Equals(materialName, "White", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(materialName, "Material", StringComparison.OrdinalIgnoreCase))
            {
                AddIfNotEmpty(result, "MI_" + modelBaseName.Replace("SK_", "").Replace("SM_", ""));
            }
        }

        return result
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddIfNotEmpty(List<string> result, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value, "None", StringComparison.OrdinalIgnoreCase))
            result.Add(value);
    }

    private static bool IsGenericGltfMaterialName(string name)
        => string.Equals(name, "White", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "Material", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(name, "DefaultMaterial", StringComparison.OrdinalIgnoreCase);

    private static SourcePackageObjectMap? FindTextureObjectInfo(
        Dictionary<string, SourcePackageObjectMap> textureObjects,
        SourceMaterialTextureSlot slot)
    {
        foreach (var path in new[] { slot.TextureObjectPath, slot.TexturePath })
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (textureObjects.TryGetValue(path, out var exact))
                return exact;

            var packagePath = NormalizePackageObjectPath(path);
            if (!string.IsNullOrWhiteSpace(packagePath) && textureObjects.TryGetValue(packagePath, out var packageMatch))
                return packageMatch;
        }

        return null;
    }

    private static string NormalizePackageName(string? packageName)
        => string.IsNullOrWhiteSpace(packageName) ? "" : packageName.Replace('\\', '/').Trim();

    private static string NormalizePackageObjectPath(string? objectPath)
    {
        if (string.IsNullOrWhiteSpace(objectPath))
            return "";

        var path = objectPath.Replace('\\', '/').Trim();
        var dot = path.LastIndexOf('.');
        if (dot <= 0)
            return path;

        var package = path[..dot];
        var name = path[(dot + 1)..];
        return string.Equals(package.Split('/').LastOrDefault(), name, StringComparison.OrdinalIgnoreCase)
            ? path
            : package + "." + package.Split('/').LastOrDefault();
    }

    private static string ClassifyMissingTextureSlot(
        SourceMaterialTextureSlot slot,
        SourcePackageObjectMap? textureInfo,
        string? textureClassName,
        string? textureClassPath)
    {
        var classText = $"{textureClassName} {textureClassPath}".ToLowerInvariant();
        var objectPath = (slot.TextureObjectPath ?? slot.TexturePath ?? "").Replace('\\', '/');

        if (classText.Contains("rendertarget"))
            return "runtimeRenderTarget";
        if (classText.Contains("mediatexture"))
            return "mediaTexture";
        if (classText.Contains("volumetexture") || classText.Contains("texturecube") || classText.Contains("texture2darray"))
            return "unsupportedTextureType";
        if (classText.Contains("curve") || classText.Contains("atlas"))
            return "materialDataTexture";
        if (objectPath.StartsWith("/Script/", StringComparison.OrdinalIgnoreCase))
            return "engineScriptObject";
        if (IsEnginePluginTexturePath(objectPath))
            return "enginePluginTexture";
        if (textureInfo == null && string.IsNullOrWhiteSpace(textureClassName))
            return "unresolvedTexturePackage";

        return "exportedTextureMissing";
    }

    private static bool IsEnginePluginTexturePath(string objectPath)
    {
        if (string.IsNullOrWhiteSpace(objectPath))
            return false;

        return objectPath.StartsWith("/SpeedTreeImporter/", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildMissingTextureStatus(string? missingCategory)
    {
        return missingCategory switch
        {
            "runtimeRenderTarget" or "mediaTexture" or "unsupportedTextureType" or "materialDataTexture" or "engineScriptObject" or "enginePluginTexture" => "nonExportableTexture",
            "unresolvedTexturePackage" => "unresolvedTexturePackage",
            _ => "missingExportedTexture",
        };
    }

    private static string BuildMissingTextureReason(string? missingCategory, string? textureClassName)
    {
        return missingCategory switch
        {
            "runtimeRenderTarget" => "源索引记录的是运行时 RenderTarget，当前不能按普通 PNG 贴图导出。",
            "mediaTexture" => "源索引记录的是 MediaTexture，内容来自视频/媒体播放源，不按普通 PNG 贴图验收。",
            "unsupportedTextureType" => $"源索引记录的是 {textureClassName ?? "特殊贴图"}，当前贴图导出链路只稳定支持 Texture2D。",
            "materialDataTexture" => $"源索引记录的是 {textureClassName ?? "材质数据资源"}，更像材质参数/曲线数据，暂不按普通贴图验收。",
            "engineScriptObject" => "材质槽指向 UE 脚本默认对象，不是可直接导出的贴图资产。",
            "enginePluginTexture" => "材质槽指向 UE 引擎/导入器插件内置贴图，不是当前游戏素材库的 PNG 缺失。",
            "unresolvedTexturePackage" => "源索引记录了材质贴图槽，但没有在 UE 包 Import/Export 记录中定位到对应贴图对象。",
            _ => "源索引记录了普通材质贴图槽，但当前导出目录中没有找到对应 PNG/HDR。",
        };
    }

    private static ComponentAssetRelationLink[] WriteComponentAssetRelations(
        string root,
        List<JObject> catalogRows,
        SourceIndexSnapshot sourceIndex,
        bool writeCompatibilityJson)
    {
        var totalRelations = sourceIndex.ComponentAssetRelationCount > 0
            ? sourceIndex.ComponentAssetRelationCount
            : sourceIndex.ComponentAssetRelations.Length;
        if (!sourceIndex.Available || totalRelations == 0)
        {
            WriteEmptyComponentRelations(root, writeCompatibilityJson);
            return [];
        }

        var exportedAssets = catalogRows
            .Where(x => !string.IsNullOrWhiteSpace((string?)x["output"]) || !string.IsNullOrWhiteSpace((string?)x["source"]))
            .ToArray();

        var assetLookup = BuildExportedAssetLookup(root, exportedAssets);
        var packageObjectsByPath = sourceIndex.PackageObjectMaps
            .Where(x => !string.IsNullOrWhiteSpace(x.ObjectPath))
            .GroupBy(x => x.ObjectPath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var retainedLinks = new List<ComponentAssetRelationLink>();
        var path = Path.Combine(root, "component_asset_relations.jsonl");
        if (!writeCompatibilityJson)
            DeleteIfExists(path);

        using var workConnection = writeCompatibilityJson ? null : OpenLibraryWorkDb(root, "component_asset_relations");
        using var workTransaction = workConnection?.BeginTransaction();
        using (var writer = writeCompatibilityJson ? new StreamWriter(path, false, Encoding.UTF8) : null)
        {
            foreach (var relation in EnumerateSourceComponentAssetRelations(sourceIndex))
            {
                var link = BuildComponentAssetRelationLink(relation, assetLookup, packageObjectsByPath, sourceIndex);
                if (writeCompatibilityJson)
                    writer!.WriteLine(SerializeComponentAssetRelationLink(link));
                else
                    InsertComponentAssetRelation(workConnection!, workTransaction!, link);

                if (ShouldRetainComponentAssetRelation(link))
                    retainedLinks.Add(link);
            }
        }

        workTransaction?.Commit();
        FinalizeWorkDb(workConnection);
        var links = retainedLinks.ToArray();
        Console.WriteLine($"Component relations streamed: total={totalRelations}, retainedInMemory={links.Length}.");
        WriteComponentGroups(root, links, writeCompatibilityJson);
        return links;
    }

    private static void WriteEmptyComponentRelations(string root, bool writeCompatibilityJson)
    {
        if (writeCompatibilityJson)
            File.WriteAllText(Path.Combine(root, "component_asset_relations.jsonl"), "", Encoding.UTF8);
        else
        {
            DeleteIfExists(Path.Combine(root, "component_asset_relations.jsonl"));
            using var workConnection = OpenLibraryWorkDb(root, "component_asset_relations");
            FinalizeWorkDb(workConnection);
        }

        if (writeCompatibilityJson)
        {
            File.WriteAllText(
                Path.Combine(root, "component_groups.json"),
                JsonConvert.SerializeObject(new
                {
                    generatedAt = DateTime.UtcNow.ToString("O"),
                    rule = "组合关系来自 UE 蓝图/组件/默认对象里的显式 PPtr 和组件模板，不按名称猜测绑定。",
                    groupCount = 0,
                    groups = Array.Empty<object>(),
                }, Formatting.Indented),
                Encoding.UTF8);
        }
        else
        {
            DeleteIfExists(Path.Combine(root, "component_groups.json"));
        }
    }

    private static SqliteConnection OpenLibraryWorkDb(string root, params string[] resetTables)
    {
        SQLitePCL.Batteries_V2.Init();
        var dbPath = GetLibraryWorkDbPath(root);
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout = 10000;");
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "PRAGMA synchronous = NORMAL;");
        EnsureLibraryWorkDbSchema(connection);
        foreach (var tableName in resetTables)
        {
            Execute(connection, $"DELETE FROM {ValidateLibraryWorkTableName(tableName)};");
            Execute(connection, $"DELETE FROM sqlite_sequence WHERE name = '{ValidateLibraryWorkTableName(tableName)}';");
        }

        return connection;
    }

    private static void EnsureLibraryWorkDbSchema(SqliteConnection connection)
    {
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS texture_links (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source TEXT NOT NULL,
                shared TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                extension TEXT NOT NULL,
                hard_linked INTEGER NOT NULL,
                link_error TEXT
            );
            """);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS material_sidecars (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                last_write_utc_ticks INTEGER NOT NULL,
                texture_slot_count INTEGER NOT NULL,
                color_count INTEGER NOT NULL,
                scalar_count INTEGER NOT NULL,
                switch_count INTEGER NOT NULL,
                blend_mode TEXT,
                shading_model TEXT,
                raw_json TEXT NOT NULL
            );
            """);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS material_texture_slots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                material_name TEXT NOT NULL,
                material_path TEXT,
                material_object_path TEXT,
                slot_name TEXT NOT NULL,
                texture_name TEXT,
                texture_object_path TEXT,
                texture_path TEXT,
                texture_class_name TEXT,
                texture_class_path TEXT,
                missing_category TEXT,
                exported_texture TEXT,
                shared_texture TEXT,
                sha256 TEXT,
                hard_linked INTEGER,
                match_status TEXT NOT NULL,
                match_reason TEXT,
                relation_source TEXT
            );
            """);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS shared_gltf_texture_links (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                model TEXT NOT NULL,
                material_name TEXT,
                semantic TEXT,
                slot_name TEXT,
                texture_name TEXT,
                image_index INTEGER,
                shared_texture TEXT,
                sha256 TEXT,
                uri TEXT,
                removed_buffer_view INTEGER NOT NULL,
                status TEXT NOT NULL,
                reason TEXT
            );
            """);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS component_asset_relations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                owner_object_path TEXT,
                owner_type TEXT,
                component_object_path TEXT,
                component_type TEXT,
                component_name TEXT,
                component_variable_name TEXT,
                relation_source TEXT NOT NULL,
                relation_type TEXT NOT NULL,
                target_path TEXT,
                target_name TEXT,
                target_asset_name TEXT,
                target_asset_kind TEXT,
                target_asset_output TEXT,
                match_status TEXT NOT NULL,
                match_reason TEXT,
                socket_name TEXT,
                parent_component_path TEXT,
                transform_json TEXT,
                source_path TEXT
            );
            """);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS model_validation_cache (
                relative_path TEXT PRIMARY KEY,
                cache_version INTEGER NOT NULL,
                model_size_bytes INTEGER NOT NULL,
                model_last_write_utc_ticks INTEGER NOT NULL,
                material_index_signature TEXT NOT NULL,
                report_json TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """);
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS library_reports (
                name TEXT PRIMARY KEY,
                status TEXT,
                generated_at TEXT,
                raw_json TEXT NOT NULL
            );
            """);
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_material_sidecars_name ON material_sidecars(name);");
        Execute(connection, "CREATE UNIQUE INDEX IF NOT EXISTS idx_material_sidecars_path ON material_sidecars(relative_path);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_model_validation_cache_signature ON model_validation_cache(cache_version, material_index_signature);");
        Execute(connection, "CREATE INDEX IF NOT EXISTS idx_library_work_reports_status ON library_reports(name, status);");
    }

    private static string ValidateLibraryWorkTableName(string tableName)
        => tableName is "texture_links" or "material_sidecars" or "material_texture_slots" or "shared_gltf_texture_links" or "component_asset_relations" or "model_validation_cache" or "library_reports"
            ? tableName
            : throw new ArgumentOutOfRangeException(nameof(tableName), tableName, "Unknown library work table.");

    private static void FinalizeWorkDb(SqliteConnection? connection)
    {
        if (connection == null)
            return;

        try
        {
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            Execute(connection, "PRAGMA journal_mode = DELETE;");
        }
        finally
        {
            connection.Dispose();
        }
    }

    private static string GetLibraryWorkDbPath(string root)
        => Path.Combine(root, "library_work.db");

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    private static SourcePackageObjectMap[] WritePackageObjectMaps(string root, SourceIndexSnapshot sourceIndex, bool writeCompatibilityJson)
    {
        var rows = sourceIndex.Available ? sourceIndex.PackageObjectMaps : [];
        var path = Path.Combine(root, "package_object_maps.jsonl");
        if (!writeCompatibilityJson)
        {
            DeleteIfExists(path);
            return rows;
        }

        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        if (rows.Length == 0 && sourceIndex.PackageObjectMapCount > 0)
        {
            writer.WriteLine(JsonConvert.SerializeObject(new
            {
                kind = "PackageObjectMapSummary",
                sourceIndex = MakeRelative(root, sourceIndex.Path),
                sourcePackageObjectMapCount = sourceIndex.PackageObjectMapCount,
                note = "完整 UE ImportMap/ExportMap 保留在 ue_source_index.db。Library 索引不复制全量 package_object_maps，避免大库后处理和 Browser 读取被数百万行源映射拖慢。"
            }));
            return rows;
        }

        foreach (var row in rows.OrderBy(x => x.SourcePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.MapType, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.MapIndex))
        {
            writer.WriteLine(JsonConvert.SerializeObject(new
            {
                kind = "PackageObjectMap",
                sourcePath = row.SourcePath,
                packageName = row.PackageName,
                mapType = row.MapType,
                mapIndex = row.MapIndex,
                objectName = row.ObjectName,
                objectPath = row.ObjectPath,
                className = row.ClassName,
                classPath = row.ClassPath,
                outerPath = row.OuterPath,
                superPath = row.SuperPath,
                templatePath = row.TemplatePath,
                targetPackage = row.TargetPackage,
                isAsset = row.IsAsset,
                isOptional = row.IsOptional,
                objectFlags = row.ObjectFlags,
                serialSize = row.SerialSize,
                publicExportHash = row.PublicExportHash,
                raw = TryParseJson(row.RawJson),
            }));
        }

        return rows;
    }

    private static ComponentAssetRelationLink[] BuildComponentAssetRelationLinks(
        string root,
        List<JObject> catalogRows,
        SourceIndexSnapshot sourceIndex)
    {
        if (!sourceIndex.Available || sourceIndex.ComponentAssetRelations.Length == 0)
            return [];

        var exportedAssets = catalogRows
            .Where(x => !string.IsNullOrWhiteSpace((string?)x["output"]) || !string.IsNullOrWhiteSpace((string?)x["source"]))
            .ToArray();

        var assetLookup = BuildExportedAssetLookup(root, exportedAssets);
        var packageObjectsByPath = sourceIndex.PackageObjectMaps
            .Where(x => !string.IsNullOrWhiteSpace(x.ObjectPath))
            .GroupBy(x => x.ObjectPath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var result = new List<ComponentAssetRelationLink>(sourceIndex.ComponentAssetRelations.Length);
        foreach (var relation in sourceIndex.ComponentAssetRelations)
        {
            var matched = FindExportedAssetForTarget(relation, assetLookup);
            var matchStatus = BuildComponentRelationMatchStatus(relation, matched, assetLookup, packageObjectsByPath, sourceIndex);
            var matchReason = BuildComponentRelationMatchReason(relation, matched, matchStatus);
            result.Add(new ComponentAssetRelationLink
            {
                SourcePath = relation.SourcePath,
                OwnerObjectPath = relation.OwnerObjectPath,
                OwnerType = relation.OwnerType,
                ComponentObjectPath = relation.ComponentObjectPath,
                ComponentType = relation.ComponentType,
                ComponentName = relation.ComponentName,
                ComponentVariableName = relation.ComponentVariableName,
                RelationSource = relation.RelationSource,
                RelationType = relation.RelationType,
                TargetPath = relation.TargetPath,
                TargetName = relation.TargetName,
                TargetAssetName = (string?)matched?["name"],
                TargetAssetKind = (string?)matched?["kind"],
                TargetAssetOutput = (string?)matched?["output"] ?? (string?)matched?["source"],
                MatchStatus = matchStatus,
                MatchReason = matchReason,
                SocketName = relation.SocketName,
                ParentComponentPath = relation.ParentComponentPath,
                Transform = BuildTransformObject(relation),
            });
        }

        return result.ToArray();
    }

    private static ComponentAssetRelationLink BuildComponentAssetRelationLink(
        SourceComponentAssetRelation relation,
        ExportedAssetLookup assetLookup,
        Dictionary<string, SourcePackageObjectMap> packageObjectsByPath,
        SourceIndexSnapshot sourceIndex)
    {
        var matched = FindExportedAssetForTarget(relation, assetLookup);
        var matchStatus = BuildComponentRelationMatchStatus(relation, matched, assetLookup, packageObjectsByPath, sourceIndex);
        var matchReason = BuildComponentRelationMatchReason(relation, matched, matchStatus);
        return new ComponentAssetRelationLink
        {
            SourcePath = relation.SourcePath,
            OwnerObjectPath = relation.OwnerObjectPath,
            OwnerType = relation.OwnerType,
            ComponentObjectPath = relation.ComponentObjectPath,
            ComponentType = relation.ComponentType,
            ComponentName = relation.ComponentName,
            ComponentVariableName = relation.ComponentVariableName,
            RelationSource = relation.RelationSource,
            RelationType = relation.RelationType,
            TargetPath = relation.TargetPath,
            TargetName = relation.TargetName,
            TargetAssetName = (string?)matched?["name"],
            TargetAssetKind = (string?)matched?["kind"],
            TargetAssetOutput = (string?)matched?["output"] ?? (string?)matched?["source"],
            MatchStatus = matchStatus,
            MatchReason = matchReason,
            SocketName = relation.SocketName,
            ParentComponentPath = relation.ParentComponentPath,
            Transform = BuildTransformObject(relation),
        };
    }

    private static string SerializeComponentAssetRelationLink(ComponentAssetRelationLink link)
        => JsonConvert.SerializeObject(new
        {
            kind = "ComponentAssetRelation",
            link.OwnerObjectPath,
            link.OwnerType,
            link.ComponentObjectPath,
            link.ComponentType,
            link.ComponentName,
            link.ComponentVariableName,
            link.RelationSource,
            link.RelationType,
            link.TargetPath,
            link.TargetName,
            link.TargetAssetName,
            link.TargetAssetKind,
            link.TargetAssetOutput,
            link.MatchStatus,
            link.MatchReason,
            link.SocketName,
            link.ParentComponentPath,
            transform = link.Transform,
            link.SourcePath,
        });

    private static bool ShouldRetainComponentAssetRelation(ComponentAssetRelationLink link)
        => IsModelRelation(link.RelationType)
           || IsAnimationRelation(link.RelationType)
           || string.Equals(link.RelationType, "Skeleton", StringComparison.OrdinalIgnoreCase)
           || string.Equals(link.RelationType, "AnimClass", StringComparison.OrdinalIgnoreCase)
           || string.Equals(link.RelationType, "AnimBlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase);

    private static ExportedAssetLookup BuildExportedAssetLookup(string root, JObject[] exportedAssets)
    {
        var byObjectPath = exportedAssets
            .Where(x => !string.IsNullOrWhiteSpace((string?)x["objectPath"]))
            .GroupBy(x => (string)x["objectPath"]!, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() == 1)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var bySuffix = new Dictionary<string, List<JObject>>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in exportedAssets)
        {
            var relative = AssetRelativeWithoutExtension(root, (string?)asset["output"] ?? (string?)asset["source"]);
            AddAssetSuffix(bySuffix, relative, asset);
            var contentIndex = relative.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
            if (contentIndex >= 0)
                AddAssetSuffix(bySuffix, relative[contentIndex..], asset);
            if (relative.StartsWith("Engine/Content/", StringComparison.OrdinalIgnoreCase))
                AddAssetSuffix(bySuffix, relative, asset);
        }

        var uniqueBySuffix = bySuffix
            .Where(x => x.Value.Count == 1)
            .ToDictionary(x => x.Key, x => x.Value[0], StringComparer.OrdinalIgnoreCase);
        var bySkeleton = exportedAssets
            .Where(x => !string.IsNullOrWhiteSpace((string?)x["skeletonPath"]))
            .GroupBy(x => (string)x["skeletonPath"]!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        return new ExportedAssetLookup(byObjectPath, uniqueBySuffix, bySkeleton);
    }

    private static void AddAssetSuffix(Dictionary<string, List<JObject>> bySuffix, string suffix, JObject asset)
    {
        if (string.IsNullOrWhiteSpace(suffix))
            return;

        var key = suffix.Replace('\\', '/').TrimStart('/');
        if (!bySuffix.TryGetValue(key, out var list))
        {
            list = [];
            bySuffix[key] = list;
        }

        list.Add(asset);
        if (!key.StartsWith("/", StringComparison.Ordinal))
        {
            var slashKey = "/" + key;
            if (!bySuffix.TryGetValue(slashKey, out var slashList))
            {
                slashList = [];
                bySuffix[slashKey] = slashList;
            }

            slashList.Add(asset);
        }
    }

    private static string BuildComponentRelationMatchStatus(
        SourceComponentAssetRelation relation,
        JObject? matched,
        ExportedAssetLookup assetLookup,
        Dictionary<string, SourcePackageObjectMap> packageObjectsByPath,
        SourceIndexSnapshot sourceIndex)
    {
        if (matched != null)
            return "matched";

        if (relation.RelationType.Equals("Component", StringComparison.OrdinalIgnoreCase))
            return "componentOnly";

        if (relation.RelationType.Equals("Skeleton", StringComparison.OrdinalIgnoreCase))
            return HasExportedModelForSkeleton(relation.TargetPath, assetLookup)
                ? "skeletonCoveredByModels"
                : "skeletonMetadata";

        if (IsClassReferenceRelation(relation.RelationType))
            return "classReference";

        if (IsUnsupportedAnimationAsset(relation, sourceIndex) ||
            IsUnsupportedAnimationAsset(relation, packageObjectsByPath))
            return "unsupportedAnimationAsset";

        return "missingExportedAsset";
    }

    private static string BuildComponentRelationMatchReason(
        SourceComponentAssetRelation relation,
        JObject? matched,
        string matchStatus)
    {
        if (matched != null)
        {
            return relation.RelationType.Equals("Skeleton", StringComparison.OrdinalIgnoreCase)
                ? "通过 UE Skeleton 原始引用匹配到同 skeletonPath 的已导出模型。"
                : "通过 UE object path 或包路径后缀匹配到已导出素材。";
        }

        return matchStatus switch
        {
            "componentOnly" => "这是 UE 组件实例/模板节点，用于组合结构和 transform，不是需要导出的独立素材。",
            "skeletonCoveredByModels" => "UE Skeleton 已由同 skeletonPath 的已导出 skinned model 覆盖，骨架本身作为元数据保留。",
            "skeletonMetadata" => "UE Skeleton 是骨架元数据引用，当前没有可直接导出的独立素材文件。",
            "classReference" => "这是 UE 蓝图/动画类引用，用于运行时逻辑或组件类型，不是模型、贴图、材质或动画素材文件。",
            "unsupportedAnimationAsset" => "这是 UE 动画容器或编辑器资产引用，当前只作为关系元数据保留，不按 ueanim 直接导出。",
            _ => "源索引记录了 UE 组件/蓝图资源关系，但当前导出目录没有找到对应资产。",
        };
    }

    private static bool HasExportedModelForSkeleton(string? skeletonPath, ExportedAssetLookup assetLookup)
    {
        if (string.IsNullOrWhiteSpace(skeletonPath))
            return false;

        return assetLookup.BySkeletonPath.TryGetValue(skeletonPath, out var matches) &&
               matches.Any(x => string.Equals((string?)x["kind"], "Model", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsClassReferenceRelation(string relationType)
        => relationType.Equals("AnimClass", StringComparison.OrdinalIgnoreCase)
           || relationType.Equals("AnimBlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase)
           || relationType.Equals("BlueprintClass", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnsupportedAnimationAsset(
        SourceComponentAssetRelation relation,
        Dictionary<string, SourcePackageObjectMap> packageObjectsByPath)
    {
        if (!relation.RelationType.Equals("Animation", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(relation.TargetPath))
            return false;

        if (packageObjectsByPath.Count == 0)
            return false;

        packageObjectsByPath.TryGetValue(relation.TargetPath, out var target);
        var typeText = $"{target?.ClassName} {target?.ClassPath} {target?.ObjectName} {relation.TargetName} {relation.TargetPath}";
        return IsUnsupportedAnimationTypeText(typeText);
    }

    private static bool IsUnsupportedAnimationAsset(SourceComponentAssetRelation relation, SourceIndexSnapshot sourceIndex)
    {
        if (!relation.RelationType.Equals("Animation", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(relation.TargetPath))
            return false;

        if (sourceIndex.UnsupportedAnimationObjectPaths.Contains(relation.TargetPath))
            return true;

        if (sourceIndex.UnsupportedAutoReferencedAnimationPaths.Contains(relation.TargetPath))
            return true;

        var typeText = $"{relation.TargetName} {relation.TargetPath}";
        return IsUnsupportedAnimationTypeText(typeText);
    }

    private static bool IsUnsupportedAnimationTypeText(string typeText)
        => typeText.Contains("BlendSpace", StringComparison.OrdinalIgnoreCase)
           || typeText.Contains("AimOffset", StringComparison.OrdinalIgnoreCase)
           || typeText.Contains("AnimBlueprint", StringComparison.OrdinalIgnoreCase);

    private static JObject? FindExportedAssetForTarget(
        SourceComponentAssetRelation relation,
        ExportedAssetLookup assetLookup)
    {
        if (!string.IsNullOrWhiteSpace(relation.TargetPath) &&
            assetLookup.ByObjectPath.TryGetValue(relation.TargetPath, out var byPath))
            return byPath;

        if (string.IsNullOrWhiteSpace(relation.TargetPath))
            return null;

        if (relation.RelationType.Equals("Skeleton", StringComparison.OrdinalIgnoreCase))
        {
            assetLookup.BySkeletonPath.TryGetValue(relation.TargetPath, out var skeletonMatches);
            skeletonMatches ??= [];
            var modelMatches = skeletonMatches
                .Where(x => string.Equals((string?)x["kind"], "Model", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (modelMatches.Length == 1)
                return modelMatches[0];
            if (skeletonMatches.Length == 1)
                return skeletonMatches[0];
        }

        var packageSuffix = BuildPackageSuffix(relation.TargetPath);
        if (string.IsNullOrWhiteSpace(packageSuffix))
            return null;

        var key = packageSuffix.Replace('\\', '/').TrimStart('/');
        if (assetLookup.ByRelativeSuffix.TryGetValue(key, out var match))
            return match;
        return assetLookup.ByRelativeSuffix.TryGetValue("/" + key, out match) ? match : null;
    }

    private static string AssetRelativeWithoutExtension(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var text = path.Replace('\\', '/');
        if (Path.IsPathRooted(text))
            text = MakeRelative(root, text).Replace('\\', '/');
        var extension = Path.GetExtension(text);
        return string.IsNullOrWhiteSpace(extension) ? text : text[..^extension.Length];
    }

    private static object? BuildTransformObject(SourceComponentAssetRelation relation)
    {
        if (relation.LocationX == null && relation.RotationPitch == null && relation.ScaleX == null)
            return null;

        return new
        {
            location = new[] { relation.LocationX ?? 0, relation.LocationY ?? 0, relation.LocationZ ?? 0 },
            rotation = new[] { relation.RotationPitch ?? 0, relation.RotationYaw ?? 0, relation.RotationRoll ?? 0 },
            scale = new[] { relation.ScaleX ?? 1, relation.ScaleY ?? 1, relation.ScaleZ ?? 1 },
        };
    }

    private static void WriteComponentGroups(string root, ComponentAssetRelationLink[] links, bool writeCompatibilityJson)
    {
        if (!writeCompatibilityJson)
        {
            DeleteIfExists(Path.Combine(root, "component_groups.json"));
            return;
        }

        if (links.Length > MaxDetailedComponentGroupRelations)
        {
            WriteComponentGroupSummary(root, links);
            return;
        }

        var groups = BuildComponentGroupRows(links)
            .Select(x => JObject.Parse(x.RawJson))
            .ToArray();

        File.WriteAllText(
            Path.Combine(root, "component_groups.json"),
            JsonConvert.SerializeObject(new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                rule = "组合关系来自 UE 蓝图/组件/默认对象里的显式 PPtr 和组件模板，不按名称猜测绑定。",
                groupCount = groups.Length,
                groups,
            }, Formatting.Indented),
            Encoding.UTF8);
    }

    private static void WriteComponentGroupSummary(string root, ComponentAssetRelationLink[] links)
    {
        File.WriteAllText(
            Path.Combine(root, "component_groups.json"),
            JsonConvert.SerializeObject(new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                rule = "组合关系来自 UE 蓝图/组件/默认对象里的显式 PPtr 和组件模板，不按名称猜测绑定。",
                skippedDetailedGroups = true,
                reason = $"保留关系数 {links.Length} 超过 {MaxDetailedComponentGroupRelations}，为避免大库后处理 OOM，仅写轻量摘要；完整逐行关系见 component_asset_relations.jsonl 和 library_index.db.component_asset_relations。",
                retainedRelationCount = links.Length,
                ownerCount = links
                    .Where(x => !string.IsNullOrWhiteSpace(x.OwnerObjectPath))
                    .Select(x => x.OwnerObjectPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                byRelationType = links
                    .GroupBy(x => string.IsNullOrWhiteSpace(x.RelationType) ? "unknown" : x.RelationType, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new { relationType = x.Key, count = x.Count() })
                    .ToArray(),
                byStatus = links
                    .GroupBy(x => string.IsNullOrWhiteSpace(x.MatchStatus) ? "unknown" : x.MatchStatus, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new { status = x.Key, count = x.Count() })
                    .ToArray(),
                groups = Array.Empty<object>(),
            }, Formatting.Indented),
            Encoding.UTF8);
    }

    private static ComponentGroupRow[] BuildComponentGroupRows(ComponentAssetRelationLink[] links)
    {
        return links
            .Where(x => !string.IsNullOrWhiteSpace(x.OwnerObjectPath))
            .GroupBy(x => x.OwnerObjectPath!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count(y => IsModelRelation(y.RelationType)))
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var json = JObject.FromObject(new
                {
                    ownerObjectPath = group.Key,
                    ownerType = group.Select(x => x.OwnerType).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                    sourcePath = group.Select(x => x.SourcePath).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                    relationCount = group.Count(),
                    componentCount = group
                        .Where(x => !string.IsNullOrWhiteSpace(x.ComponentObjectPath))
                        .Select(x => x.ComponentObjectPath)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    modelReferenceCount = group.Count(x => IsModelRelation(x.RelationType)),
                    exportedModelReferenceCount = group.Count(x => IsModelRelation(x.RelationType) && x.MatchStatus == "matched"),
                    missingModelReferenceCount = group.Count(x => IsModelRelation(x.RelationType) && IsMissingAssetRelation(x)),
                    animationReferenceCount = group.Count(x => IsAnimationRelation(x.RelationType)),
                    exportedAnimationReferenceCount = group.Count(x => IsAnimationRelation(x.RelationType) && x.MatchStatus == "matched"),
                    missingAnimationReferenceCount = group.Count(x => IsAnimationRelation(x.RelationType) && IsMissingAssetRelation(x)),
                    materialReferenceCount = group.Count(x => string.Equals(x.RelationType, "Material", StringComparison.OrdinalIgnoreCase)),
                    exportedMaterialReferenceCount = group.Count(x => string.Equals(x.RelationType, "Material", StringComparison.OrdinalIgnoreCase) && x.MatchStatus == "matched"),
                    missingMaterialReferenceCount = group.Count(x => string.Equals(x.RelationType, "Material", StringComparison.OrdinalIgnoreCase) && IsMissingAssetRelation(x)),
                    missingReferenceCount = group.Count(IsMissingAssetRelation),
                    relationSources = group.Select(x => x.RelationSource)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    components = group
                        .Where(x => string.Equals(x.RelationType, "Component", StringComparison.OrdinalIgnoreCase))
                        .DistinctBy(x => x.ComponentObjectPath ?? x.TargetPath ?? x.ComponentName ?? "", StringComparer.OrdinalIgnoreCase)
                        .Select(x => new
                        {
                            x.ComponentObjectPath,
                            x.ComponentType,
                            x.ComponentName,
                            x.ComponentVariableName,
                            x.ParentComponentPath,
                            x.SocketName,
                            x.Transform,
                        })
                        .ToArray(),
                    models = group
                        .Where(x => IsModelRelation(x.RelationType))
                        .DistinctBy(x => $"{x.RelationType}|{x.TargetPath}|{x.ComponentObjectPath}", StringComparer.OrdinalIgnoreCase)
                        .Select(x => new
                        {
                            x.RelationType,
                            x.TargetName,
                            x.TargetPath,
                            x.TargetAssetOutput,
                            x.MatchStatus,
                            x.ComponentName,
                            x.ComponentVariableName,
                            x.SocketName,
                        })
                        .ToArray(),
                    animations = group
                        .Where(x => IsAnimationRelation(x.RelationType))
                        .DistinctBy(x => $"{x.RelationType}|{x.TargetPath}|{x.ComponentObjectPath}", StringComparer.OrdinalIgnoreCase)
                        .Select(x => new
                        {
                            x.RelationType,
                            x.TargetName,
                            x.TargetPath,
                            x.TargetAssetOutput,
                            x.MatchStatus,
                            x.ComponentName,
                            x.ComponentVariableName,
                        })
                        .ToArray(),
                    materials = group
                        .Where(x => string.Equals(x.RelationType, "Material", StringComparison.OrdinalIgnoreCase))
                        .DistinctBy(x => $"{x.RelationType}|{x.TargetPath}|{x.ComponentObjectPath}", StringComparer.OrdinalIgnoreCase)
                        .Select(x => new
                        {
                            x.RelationType,
                            x.TargetName,
                            x.TargetPath,
                            x.TargetAssetOutput,
                            x.MatchStatus,
                            x.ComponentName,
                            x.ComponentVariableName,
                        })
                        .ToArray(),
                    skeletons = group
                        .Where(x => string.Equals(x.RelationType, "Skeleton", StringComparison.OrdinalIgnoreCase))
                        .DistinctBy(x => $"{x.RelationType}|{x.TargetPath}|{x.ComponentObjectPath}", StringComparer.OrdinalIgnoreCase)
                        .Select(x => new
                        {
                            x.RelationType,
                            x.TargetName,
                            x.TargetPath,
                            x.TargetAssetOutput,
                            x.MatchStatus,
                            x.ComponentName,
                            x.ComponentVariableName,
                        })
                        .ToArray(),
                    otherReferences = group
                        .Where(x => IsTrackedAssetRelation(x.RelationType) &&
                                    !IsModelRelation(x.RelationType) &&
                                    !IsAnimationRelation(x.RelationType) &&
                                    !string.Equals(x.RelationType, "Material", StringComparison.OrdinalIgnoreCase) &&
                                    !string.Equals(x.RelationType, "Skeleton", StringComparison.OrdinalIgnoreCase))
                        .DistinctBy(x => $"{x.RelationType}|{x.TargetPath}|{x.ComponentObjectPath}", StringComparer.OrdinalIgnoreCase)
                        .Select(x => new
                        {
                            x.RelationType,
                            x.TargetName,
                            x.TargetPath,
                            x.TargetAssetOutput,
                            x.MatchStatus,
                            x.ComponentName,
                            x.ComponentVariableName,
                        })
                        .ToArray(),
                    missingReferences = group
                        .Where(IsMissingAssetRelation)
                        .DistinctBy(x => $"{x.RelationType}|{x.TargetPath}|{x.ComponentObjectPath}", StringComparer.OrdinalIgnoreCase)
                        .Select(x => new
                        {
                            x.RelationType,
                            x.TargetName,
                            x.TargetPath,
                            x.MatchStatus,
                            x.MatchReason,
                            x.ComponentName,
                            x.ComponentVariableName,
                        })
                        .ToArray(),
                });

                return new ComponentGroupRow
                {
                    OwnerObjectPath = (string)json["ownerObjectPath"]!,
                    OwnerType = (string?)json["ownerType"],
                    SourcePath = (string?)json["sourcePath"],
                    RelationCount = (int)json["relationCount"]!,
                    ComponentCount = (int)json["componentCount"]!,
                    ModelReferenceCount = (int)json["modelReferenceCount"]!,
                    ExportedModelReferenceCount = (int)json["exportedModelReferenceCount"]!,
                    MissingModelReferenceCount = (int)json["missingModelReferenceCount"]!,
                    AnimationReferenceCount = (int)json["animationReferenceCount"]!,
                    ExportedAnimationReferenceCount = (int)json["exportedAnimationReferenceCount"]!,
                    MissingAnimationReferenceCount = (int)json["missingAnimationReferenceCount"]!,
                    MaterialReferenceCount = (int)json["materialReferenceCount"]!,
                    ExportedMaterialReferenceCount = (int)json["exportedMaterialReferenceCount"]!,
                    MissingMaterialReferenceCount = (int)json["missingMaterialReferenceCount"]!,
                    MissingReferenceCount = (int)json["missingReferenceCount"]!,
                    RawJson = json.ToString(Formatting.None),
                };
            })
            .ToArray();
    }

    private static bool IsModelRelation(string relationType)
        => relationType.Equals("StaticMesh", StringComparison.OrdinalIgnoreCase)
           || relationType.Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase);

    private static bool IsAnimationRelation(string relationType)
        => relationType.Equals("Animation", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrackedAssetRelation(string relationType)
        => IsModelRelation(relationType)
           || IsAnimationRelation(relationType)
           || relationType.Equals("Material", StringComparison.OrdinalIgnoreCase)
           || relationType.Equals("Texture", StringComparison.OrdinalIgnoreCase)
           || relationType.Equals("Skeleton", StringComparison.OrdinalIgnoreCase)
           || relationType.Equals("AnimBlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase)
           || relationType.Equals("BlueprintClass", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissingAssetRelation(ComponentAssetRelationLink link)
        => IsTrackedAssetRelation(link.RelationType)
           && !string.Equals(link.MatchStatus, "matched", StringComparison.OrdinalIgnoreCase)
           && !IsMetadataOnlyMatchStatus(link.MatchStatus);

    private static bool IsMetadataOnlyMatchStatus(string status)
        => status.Equals("componentOnly", StringComparison.OrdinalIgnoreCase)
           || status.Equals("classReference", StringComparison.OrdinalIgnoreCase)
           || status.Equals("skeletonMetadata", StringComparison.OrdinalIgnoreCase)
           || status.Equals("skeletonCoveredByModels", StringComparison.OrdinalIgnoreCase)
           || status.Equals("unsupportedAnimationAsset", StringComparison.OrdinalIgnoreCase);

    private static bool IsSharedGltfTextureLinkedStatus(string status)
        => status.Equals("linked", StringComparison.OrdinalIgnoreCase)
           || status.Equals("rewritten", StringComparison.OrdinalIgnoreCase)
           || status.Equals("externalized", StringComparison.OrdinalIgnoreCase);

    private static SharedGltfTextureLink[] RewriteGltfSharedTextureUris(
        string root,
        List<ModelValidationEntry> reports,
        MaterialTextureSlotLink[] materialTextureSlots,
        bool writeCompatibilityJson)
    {
        var rows = new List<SharedGltfTextureLink>();
        foreach (var report in reports.Where(x => x.RelativePath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)))
            TryExternalizeGlbEmbeddedImages(root, report, rows);

        var matchedSlots = materialTextureSlots
            .Where(x => x.MatchStatus == "matched" && !string.IsNullOrWhiteSpace(x.SharedTexture))
            .GroupBy(x => x.MaterialName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var report in reports.Where(x => x.RelativePath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)))
        {
            var gltfPath = Path.Combine(root, report.RelativePath);
            if (!File.Exists(gltfPath))
                continue;

            try
            {
                var gltf = JObject.Parse(File.ReadAllText(gltfPath));
                var materials = ArrayOf(gltf, "materials");
                var textures = ArrayOf(gltf, "textures");
                var images = ArrayOf(gltf, "images");
                var changed = false;
                var rewrittenImages = new Dictionary<int, string>();

                for (var materialIndex = 0; materialIndex < materials.Length; materialIndex++)
                {
                    var material = materials[materialIndex];
                    var materialName = material["name"]?.Value<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(materialName) || !matchedSlots.TryGetValue(materialName, out var slots))
                        continue;

                    changed |= TryRewriteMaterialTexture(root, gltfPath, report, material, textures, images, slots,
                        materialName, "baseColor", material["pbrMetallicRoughness"]?["baseColorTexture"] as JObject, rows, rewrittenImages);
                    changed |= TryRewriteMaterialTexture(root, gltfPath, report, material, textures, images, slots,
                        materialName, "metallicRoughness", material["pbrMetallicRoughness"]?["metallicRoughnessTexture"] as JObject, rows, rewrittenImages);
                    changed |= TryRewriteMaterialTexture(root, gltfPath, report, material, textures, images, slots,
                        materialName, "normal", material["normalTexture"] as JObject, rows, rewrittenImages);
                    changed |= TryRewriteMaterialTexture(root, gltfPath, report, material, textures, images, slots,
                        materialName, "occlusion", material["occlusionTexture"] as JObject, rows, rewrittenImages);
                    changed |= TryRewriteMaterialTexture(root, gltfPath, report, material, textures, images, slots,
                        materialName, "emissive", material["emissiveTexture"] as JObject, rows, rewrittenImages);
                }

                if (changed)
                {
                    File.WriteAllText(gltfPath, gltf.ToString(Formatting.Indented), Encoding.UTF8);
                    report.EmbeddedImageCount = images.Count(x => x["bufferView"] != null);
                }
            }
            catch (Exception ex)
            {
                rows.Add(new SharedGltfTextureLink
                {
                    Model = report.RelativePath,
                    Status = "error",
                    Reason = ex.Message,
                });
            }
        }

        var path = Path.Combine(root, "shared_texture_gltf_links.jsonl");
        if (!writeCompatibilityJson)
            DeleteIfExists(path);

        using var workConnection = writeCompatibilityJson ? null : OpenLibraryWorkDb(root, "shared_gltf_texture_links");
        using var workTransaction = workConnection?.BeginTransaction();
        using (var writer = writeCompatibilityJson ? new StreamWriter(path, false, Encoding.UTF8) : null)
        {
            foreach (var row in rows.OrderBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.MaterialName, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.Semantic, StringComparer.OrdinalIgnoreCase))
            {
                if (writeCompatibilityJson)
                    writer!.WriteLine(JsonConvert.SerializeObject(row));
                else
                    InsertSharedGltfTextureLink(workConnection!, workTransaction!, row);
            }
        }

        workTransaction?.Commit();
        FinalizeWorkDb(workConnection);

        return rows.ToArray();
    }

    private static void TryExternalizeGlbEmbeddedImages(string root, ModelValidationEntry report, List<SharedGltfTextureLink> rows)
    {
        var glbPath = Path.Combine(root, report.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(glbPath))
            return;

        try
        {
            var (gltf, binData) = ReadGlb(glbPath);
            var images = ArrayOf(gltf, "images");
            var bufferViews = ArrayOf(gltf, "bufferViews");
            if (images.Length == 0 || bufferViews.Length == 0 || binData.Length == 0)
            {
                RecordExistingSharedGlbImageUris(root, glbPath, report, images, rows);
                return;
            }

            RecordExistingSharedGlbImageUris(root, glbPath, report, images, rows);

            var removableBufferViews = FindImageOnlyBufferViews(gltf, images, bufferViews);
            var changed = false;
            for (var imageIndex = 0; imageIndex < images.Length; imageIndex++)
            {
                var image = images[imageIndex];
                var bufferViewIndex = (int?)image["bufferView"];
                if (bufferViewIndex == null)
                    continue;

                if (!removableBufferViews.Contains(bufferViewIndex.Value))
                {
                    rows.Add(new SharedGltfTextureLink
                    {
                        Model = report.RelativePath,
                        ImageIndex = imageIndex,
                        RemovedBufferView = false,
                        Status = "skipped",
                        Reason = "image.bufferView 可能被几何或其它数据引用，跳过外置以保护模型结构。",
                    });
                    continue;
                }

                if (!TryReadBufferViewBytes(bufferViews[bufferViewIndex.Value], binData, out var imageBytes))
                {
                    rows.Add(new SharedGltfTextureLink
                    {
                        Model = report.RelativePath,
                        ImageIndex = imageIndex,
                        RemovedBufferView = false,
                        Status = "error",
                        Reason = "image.bufferView 超出 GLB binary chunk 范围。",
                    });
                    continue;
                }

                var extension = GuessImageExtension((string?)image["mimeType"], imageBytes);
                if (extension == null)
                {
                    rows.Add(new SharedGltfTextureLink
                    {
                        Model = report.RelativePath,
                        ImageIndex = imageIndex,
                        RemovedBufferView = false,
                        Status = "skipped",
                        Reason = "暂不支持的内嵌图片格式。",
                    });
                    continue;
                }

                var hash = HashBytes(imageBytes);
                var sharedPath = BuildSharedTexturePath(root, hash, extension);
                Directory.CreateDirectory(Path.GetDirectoryName(sharedPath)!);
                if (!File.Exists(sharedPath))
                    File.WriteAllBytes(sharedPath, imageBytes);

                var uri = MakeRelative(Path.GetDirectoryName(glbPath)!, sharedPath);
                image["uri"] = uri;
                image.Remove("bufferView");
                image.Remove("mimeType");
                changed = true;
                rows.Add(new SharedGltfTextureLink
                {
                    Model = report.RelativePath,
                    ImageIndex = imageIndex,
                    SharedTexture = MakeRelative(root, sharedPath),
                    Sha256 = hash,
                    Uri = uri,
                    RemovedBufferView = true,
                    Status = "externalized",
                    Reason = "从 GLB binary chunk 提取内嵌图片，写入 Textures/_Shared，并改写 image URI。",
                });
            }

            if (!changed)
                return;

            var newBin = RebuildGlbBinaryWithoutBufferViews(gltf, bufferViews, binData, removableBufferViews);
            WriteGlb(glbPath, gltf, newBin);
            report.EmbeddedImageCount = ArrayOf(gltf, "images").Count(x => x["bufferView"] != null);
            report.ImageCount = ArrayOf(gltf, "images").Length;
            TryDeleteModelValidationCache(root, glbPath);
        }
        catch (Exception ex)
        {
            rows.Add(new SharedGltfTextureLink
            {
                Model = report.RelativePath,
                Status = "error",
                Reason = $"GLB 贴图外置失败: {ex.Message}",
            });
        }
    }

    private static void RecordExistingSharedGlbImageUris(
        string root,
        string glbPath,
        ModelValidationEntry report,
        JObject[] images,
        List<SharedGltfTextureLink> rows)
    {
        for (var imageIndex = 0; imageIndex < images.Length; imageIndex++)
        {
            var image = images[imageIndex];
            if (image["bufferView"] != null)
                continue;

            var uri = (string?)image["uri"];
            if (string.IsNullOrWhiteSpace(uri))
                continue;

            var imagePath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(glbPath)!, Uri.UnescapeDataString(uri)));
            var sharedRoot = Path.GetFullPath(Path.Combine(root, "Textures", "_Shared"));
            if (!imagePath.StartsWith(sharedRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(imagePath))
                continue;

            var sharedRelative = MakeRelative(root, imagePath);
            rows.Add(new SharedGltfTextureLink
            {
                Model = report.RelativePath,
                ImageIndex = imageIndex,
                SharedTexture = sharedRelative,
                Sha256 = TryReadHashFromSharedTexturePath(imagePath),
                Uri = uri,
                RemovedBufferView = true,
                Status = "externalized",
                Reason = "GLB image URI 已指向 Textures/_Shared，本次重建共享贴图链接记录。",
            });
        }
    }

    private static string? TryReadHashFromSharedTexturePath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length == 64 && name.All(Uri.IsHexDigit) ? name.ToLowerInvariant() : null;
    }

    private static HashSet<int> FindImageOnlyBufferViews(JObject gltf, JObject[] images, JObject[] bufferViews)
    {
        var imageBufferViews = images
            .Select(x => (int?)x["bufferView"])
            .Where(x => x is >= 0)
            .Select(x => x!.Value)
            .Where(x => x < bufferViews.Length && ((int?)bufferViews[x]["buffer"] ?? 0) == 0)
            .ToHashSet();
        if (imageBufferViews.Count == 0)
            return [];

        var protectedBufferViews = new HashSet<int>();
        CollectProtectedBufferViewReferences(gltf, protectedBufferViews);
        imageBufferViews.ExceptWith(protectedBufferViews);
        return imageBufferViews;
    }

    private static void CollectProtectedBufferViewReferences(JToken token, HashSet<int> result)
    {
        if (token is JObject obj)
        {
            if (obj["bufferView"] is JValue value && value.Type == JTokenType.Integer)
            {
                if (!IsGltfImageObject(obj))
                    result.Add(value.Value<int>());
            }

            foreach (var child in obj.Properties().Select(x => x.Value))
                CollectProtectedBufferViewReferences(child, result);
        }
        else if (token is JArray array)
        {
            foreach (var child in array)
                CollectProtectedBufferViewReferences(child, result);
        }
    }

    private static bool IsGltfImageObject(JObject obj)
        => obj.Parent is JArray array
           && array.Parent is JProperty property
           && property.Name.Equals("images", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadBufferViewBytes(JObject bufferView, byte[] binData, out byte[] bytes)
    {
        bytes = [];
        var offset = (int?)bufferView["byteOffset"] ?? 0;
        var length = (int?)bufferView["byteLength"] ?? 0;
        if (offset < 0 || length <= 0 || offset + length > binData.Length)
            return false;

        bytes = new byte[length];
        Buffer.BlockCopy(binData, offset, bytes, 0, length);
        return true;
    }

    private static byte[] RebuildGlbBinaryWithoutBufferViews(
        JObject gltf,
        JObject[] oldBufferViews,
        byte[] oldBinData,
        HashSet<int> removedBufferViews)
    {
        var bufferViewMap = new Dictionary<int, int>();
        var newBufferViews = new JArray();
        using var stream = new MemoryStream(oldBinData.Length);

        for (var oldIndex = 0; oldIndex < oldBufferViews.Length; oldIndex++)
        {
            if (removedBufferViews.Contains(oldIndex))
                continue;

            if (!TryReadBufferViewBytes(oldBufferViews[oldIndex], oldBinData, out var bytes))
                throw new InvalidDataException($"bufferView[{oldIndex}] 超出 GLB binary chunk 范围。");

            PadStream4(stream);
            var clone = (JObject)oldBufferViews[oldIndex].DeepClone();
            clone["byteOffset"] = (int)stream.Position;
            clone["byteLength"] = bytes.Length;
            bufferViewMap[oldIndex] = newBufferViews.Count;
            newBufferViews.Add(clone);
            stream.Write(bytes, 0, bytes.Length);
        }

        gltf["bufferViews"] = newBufferViews;
        RemapBufferViewReferences(gltf, bufferViewMap, removedBufferViews);
        if (gltf["buffers"] is JArray buffers && buffers.First is JObject buffer)
            buffer["byteLength"] = (int)stream.Length;

        return stream.ToArray();
    }

    private static void RemapBufferViewReferences(JToken token, Dictionary<int, int> map, HashSet<int> removed)
    {
        if (token is JObject obj)
        {
            if (obj["bufferView"] is JValue value && value.Type == JTokenType.Integer)
            {
                var oldIndex = value.Value<int>();
                if (map.TryGetValue(oldIndex, out var newIndex))
                    obj["bufferView"] = newIndex;
                else if (removed.Contains(oldIndex))
                    obj.Remove("bufferView");
            }

            foreach (var child in obj.Properties().Select(x => x.Value).ToArray())
                RemapBufferViewReferences(child, map, removed);
        }
        else if (token is JArray array)
        {
            foreach (var child in array.ToArray())
                RemapBufferViewReferences(child, map, removed);
        }
    }

    private static void PadStream4(Stream stream)
    {
        while ((stream.Position & 3) != 0)
            stream.WriteByte(0);
    }

    private static string? GuessImageExtension(string? mimeType, byte[] bytes)
    {
        if (mimeType?.Equals("image/png", StringComparison.OrdinalIgnoreCase) == true || IsPng(bytes))
            return ".png";
        if (mimeType?.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) == true || IsJpeg(bytes))
            return ".jpg";
        if (mimeType?.Equals("image/webp", StringComparison.OrdinalIgnoreCase) == true || IsWebp(bytes))
            return ".webp";
        return null;
    }

    private static bool IsPng(byte[] bytes)
        => bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;

    private static bool IsJpeg(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    private static bool IsWebp(byte[] bytes)
        => bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
           && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50;

    private static string BuildSharedTexturePath(string root, string hash, string extension)
        => Path.Combine(root, "Textures", "_Shared", hash[..2], $"{hash}{extension.ToLowerInvariant()}");

    private static void TryDeleteModelValidationCache(string root, string glbPath)
    {
        TryDeleteModelValidationCacheSqlite(root, glbPath);
        TryDeleteLegacyModelValidationCache(glbPath);
    }

    private static void TryDeleteModelValidationCacheSqlite(string root, string glbPath)
    {
        try
        {
            if (!File.Exists(GetLibraryWorkDbPath(root)))
                return;

            var connection = OpenLibraryWorkDb(root);
            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM model_validation_cache WHERE relative_path = $relativePath;";
                Add(command, "$relativePath", NormalizeCatalogOutput(root, glbPath));
                command.ExecuteNonQuery();
            }
            finally
            {
                FinalizeWorkDb(connection);
            }
        }
        catch
        {
            // 缓存删不掉只会影响下一次扫描速度，不影响当前报告。
        }
    }

    private static bool TryRewriteMaterialTexture(
        string root,
        string gltfPath,
        ModelValidationEntry report,
        JObject material,
        JObject[] textures,
        JObject[] images,
        MaterialTextureSlotLink[] slots,
        string materialName,
        string semantic,
        JObject? textureInfo,
        List<SharedGltfTextureLink> rows,
        Dictionary<int, string> rewrittenImages)
    {
        if (textureInfo == null)
            return false;

        var textureIndex = textureInfo["index"]?.Value<int>();
        if (textureIndex == null || textureIndex < 0 || textureIndex >= textures.Length)
            return false;

        var imageIndex = textures[textureIndex.Value]["source"]?.Value<int>();
        if (imageIndex == null || imageIndex < 0 || imageIndex >= images.Length)
            return false;

        var slot = FindBestSlotForSemantic(slots, semantic);
        if (slot?.SharedTexture == null)
            return false;

        var sharedPath = Path.Combine(root, slot.SharedTexture);
        if (!File.Exists(sharedPath))
            return false;

        var uri = MakeRelative(Path.GetDirectoryName(gltfPath)!, sharedPath);
        if (rewrittenImages.TryGetValue(imageIndex.Value, out var existingUri) &&
            !existingUri.Equals(uri, StringComparison.OrdinalIgnoreCase))
        {
            rows.Add(new SharedGltfTextureLink
            {
                Model = report.RelativePath,
                MaterialName = materialName,
                Semantic = semantic,
                SlotName = slot.SlotName,
                ImageIndex = imageIndex.Value,
                SharedTexture = slot.SharedTexture,
                Uri = uri,
                Status = "conflict",
                Reason = $"image[{imageIndex.Value}] 已被映射到 {existingUri}，跳过不同共享贴图。",
            });
            return false;
        }

        var image = images[imageIndex.Value];
        var hadBufferView = image["bufferView"] != null;
        image["uri"] = uri;
        image.Remove("bufferView");
        image.Remove("mimeType");
        rewrittenImages[imageIndex.Value] = uri;
        rows.Add(new SharedGltfTextureLink
        {
            Model = report.RelativePath,
            MaterialName = materialName,
            Semantic = semantic,
            SlotName = slot.SlotName,
            TextureName = slot.TextureName,
            ImageIndex = imageIndex.Value,
            SharedTexture = slot.SharedTexture,
            Sha256 = slot.Sha256,
            Uri = uri,
            RemovedBufferView = hadBufferView,
            Status = "rewritten",
            Reason = "根据 UE 材质贴图槽匹配到共享贴图，并改写文本 glTF image URI。",
        });
        return true;
    }

    private static MaterialTextureSlotLink? FindBestSlotForSemantic(MaterialTextureSlotLink[] slots, string semantic)
    {
        return slots
            .Where(x => SlotMatchesSemantic(x.SlotName, semantic))
            .OrderByDescending(x => SlotSemanticScore(x.SlotName, semantic))
            .ThenBy(x => x.SlotName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool SlotMatchesSemantic(string slotName, string semantic)
        => SlotSemanticScore(slotName, semantic) > 0;

    private static int SlotSemanticScore(string slotName, string semantic)
    {
        var slot = slotName.Replace("_", "").Replace(" ", "").Replace("-", "").ToLowerInvariant();
        return semantic switch
        {
            "baseColor" when slot.Contains("basecolor") || slot.Contains("diffuse") || slot.Contains("albedo") => 100,
            "normal" when slot.Contains("normal") => 100,
            "metallicRoughness" when slot.Contains("specularmask") || slot.Contains("roughness") || slot.Contains("metallic") || slot.Contains("orm") || slot.Contains("mask") => 80,
            "occlusion" when slot.Contains("ao") || slot.Contains("ambientocclusion") || slot.Contains("occlusion") || slot.Contains("specularmask") || slot.Contains("orm") || slot.Contains("mask") => 70,
            "emissive" when slot.Contains("emissive") || slot.Contains("emission") => 100,
            _ => 0,
        };
    }

    private static MaterialInfo? FindMaterialInfo(Dictionary<string, MaterialInfo> materialIndex, string? materialName)
    {
        if (string.IsNullOrWhiteSpace(materialName))
            return null;

        if (materialIndex.TryGetValue(materialName, out var exact))
            return exact;

        return materialIndex.Values.FirstOrDefault(x => string.Equals(x.Name, materialName, StringComparison.OrdinalIgnoreCase));
    }

    private static TextureLinkLookup BuildTextureLinkLookup(List<TextureLinkInfo> textureLinks)
    {
        var byPackageSuffix = new Dictionary<string, List<TextureLinkInfo>>(StringComparer.OrdinalIgnoreCase);
        var byFileName = new Dictionary<string, List<TextureLinkInfo>>(StringComparer.OrdinalIgnoreCase);

        foreach (var link in textureLinks)
        {
            foreach (var key in BuildTextureLookupKeys(link.RelativePath))
                AddTextureLookupValue(byPackageSuffix, key, link);

            var fileName = Path.GetFileNameWithoutExtension(link.RelativePath);
            if (!string.IsNullOrWhiteSpace(fileName))
                AddTextureLookupValue(byFileName, fileName, link);
        }

        return new TextureLinkLookup(
            byPackageSuffix.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
            byFileName.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> BuildTextureLookupKeys(string relativePath)
    {
        var normalized = NormalizeTextureLookupKey(TextureRelativeWithoutExtension(relativePath));
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in BuildTextureLookupKeyVariants(normalized))
        {
            if (emitted.Add(key))
                yield return key;
        }
    }

    private static IEnumerable<string> BuildTextureLookupKeyVariants(string normalized)
    {
        yield return normalized;

        var contentIndex = normalized.IndexOf("/content/", StringComparison.OrdinalIgnoreCase);
        if (contentIndex >= 0)
        {
            yield return normalized[(contentIndex + 1)..];

            var beforeContent = normalized[..contentIndex];
            var afterContent = normalized[(contentIndex + "/content/".Length)..];
            var pluginsIndex = beforeContent.LastIndexOf("/plugins/", StringComparison.OrdinalIgnoreCase);
            if (pluginsIndex >= 0)
            {
                var pluginName = beforeContent[(pluginsIndex + "/plugins/".Length)..].Split('/').LastOrDefault();
                if (!string.IsNullOrWhiteSpace(pluginName))
                {
                    // UE 插件资源在材质里常写成 /PluginName/Path.Asset，
                    // 但实际导出路径可能是 Game/Plugins/PluginName/Content/Path.png。
                    // 两种 mount point 都入索引，后续材质槽才能稳定命中共享贴图。
                    yield return $"{pluginName}/content/{afterContent}";
                    yield return $"{pluginName}/{afterContent}";
                }
            }
        }

        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 2 && string.Equals(parts[1], "content", StringComparison.OrdinalIgnoreCase))
            yield return parts[0] + "/" + string.Join('/', parts.Skip(2));
    }

    private static void AddTextureLookupValue(
        Dictionary<string, List<TextureLinkInfo>> lookup,
        string key,
        TextureLinkInfo link)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (!lookup.TryGetValue(key, out var list))
        {
            list = [];
            lookup[key] = list;
        }

        list.Add(link);
    }

    private static TextureLinkInfo? FindTextureLink(TextureLinkLookup lookup, SourceMaterialTextureSlot slot)
    {
        var objectPath = slot.TextureObjectPath ?? slot.TexturePath;
        if (!string.IsNullOrWhiteSpace(objectPath))
        {
            var packageSuffix = BuildPackageSuffix(objectPath);
            var packageKey = NormalizeTextureLookupKey(packageSuffix);
            if (!string.IsNullOrWhiteSpace(packageKey) &&
                lookup.ByPackageSuffix.TryGetValue(packageKey, out var exactSuffixMatches))
            {
                var exactMatch = PickPreferredTextureLink(exactSuffixMatches);
                if (exactMatch != null)
                    return exactMatch;
            }
        }

        if (string.IsNullOrWhiteSpace(slot.TextureName))
            return null;

        return lookup.ByFileName.TryGetValue(slot.TextureName, out var nameMatches)
            ? PickPreferredTextureLink(nameMatches)
            : null;
    }

    private static string NormalizeTextureLookupKey(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        return path.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();
    }

    private static TextureLinkInfo? PickPreferredTextureLink(TextureLinkInfo[] matches)
    {
        if (matches.Length == 0)
            return null;

        if (matches.Length == 1)
            return matches[0];

        // 同一个 UTexture2D 可能同时写出 HDR 伴随文件和 PNG 预览；材质槽默认使用 PNG 作为可用素材库贴图。
        var pngMatches = matches
            .Where(x => x.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (pngMatches.Length == 1)
            return pngMatches[0];

        return null;
    }

    private static string BuildPackageSuffix(string objectPath)
    {
        var packagePath = objectPath.Replace('\\', '/');
        var dot = packagePath.LastIndexOf('.');
        if (dot > 0)
            packagePath = packagePath[..dot];

        if (packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            return "/Content/" + packagePath["/Game/".Length..];
        if (packagePath.StartsWith("/Engine/", StringComparison.OrdinalIgnoreCase))
            return "Engine/Content/" + packagePath["/Engine/".Length..];
        if (packagePath.StartsWith("/", StringComparison.OrdinalIgnoreCase))
        {
            var pluginPath = packagePath.TrimStart('/');
            var slashIndex = pluginPath.IndexOf('/');
            // 和导出阶段保持一致：插件 mount point 需要映射到 PluginName/Content/...。
            if (slashIndex > 0 && !pluginPath.Contains("/Content/", StringComparison.OrdinalIgnoreCase))
                return pluginPath[..slashIndex] + "/Content/" + pluginPath[(slashIndex + 1)..];
            return pluginPath;
        }

        return packagePath;
    }

    private static string TextureRelativeWithoutExtension(string relativePath)
    {
        var text = relativePath.Replace('\\', '/');
        var extension = Path.GetExtension(text);
        return string.IsNullOrWhiteSpace(extension) ? text : text[..^extension.Length];
    }

    private static AnimationValidationSummary WriteAnimationValidation(
        string root,
        List<JObject> catalogRows,
        SourceIndexSnapshot sourceIndex,
        ComponentAssetRelationLink[] componentAssetRelations,
        bool writeCompatibilityJson)
    {
        var validations = BuildAnimationValidations(root, catalogRows, sourceIndex, componentAssetRelations);
        var summary = new AnimationValidationSummary
        {
            SourceIndexAvailable = sourceIndex.Available,
            SourceIndexError = sourceIndex.Error,
            Validations = validations,
            ByPairKey = validations.ToDictionary(x => x.PairKey, StringComparer.OrdinalIgnoreCase),
        };

        var json = new JObject
        {
            ["generatedAt"] = DateTime.UtcNow.ToString("O"),
            ["rule"] = "默认验证显式组件关系、唯一 Skeleton 关系，以及共享 Skeleton 可复用动画；再检查动画 track 骨骼是否被模型骨架覆盖，以及重叠骨骼父子层级是否兼容。",
            ["sourceIndex"] = JObject.FromObject(new
            {
                available = sourceIndex.Available,
                path = sourceIndex.Available ? MakeRelative(root, sourceIndex.Path).Replace('\\', '/') : null,
                error = sourceIndex.Error,
            }),
            ["totals"] = JObject.FromObject(new
            {
                pairs = validations.Length,
                ok = validations.Count(x => x.Status == "ok"),
                warning = validations.Count(x => x.Status == "warning"),
                error = validations.Count(x => x.Status == "error"),
                containerAnimations = validations.Count(x => x.IsContainerAnimation),
            }),
            ["detailFile"] = "animation_validation.jsonl",
        };

        var summaryPath = Path.Combine(root, "animation_validation.json");
        var detailPath = Path.Combine(root, "animation_validation.jsonl");
        if (writeCompatibilityJson)
        {
            File.WriteAllText(summaryPath, json.ToString(Formatting.Indented), Encoding.UTF8);
            WriteAnimationValidationJsonLines(detailPath, validations);
        }
        else
        {
            DeleteIfExists(summaryPath);
            DeleteIfExists(detailPath);
        }

        return summary;
    }

    private static void WriteAnimationValidationJsonLines(string path, AnimationValidationEntry[] validations)
    {
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        foreach (var validation in validations)
            writer.WriteLine(BuildAnimationValidationJson(validation).ToString(Formatting.None));
    }

    private static JObject BuildAnimationValidationJson(AnimationValidationEntry x)
    {
        return JObject.FromObject(new
        {
            status = x.Status,
            candidateReason = x.CandidateReason,
            validationCategory = x.ValidationCategory,
            reason = x.Reason,
            model = x.ModelOutput,
            modelName = x.ModelName,
            modelSource = x.ModelSource,
            animation = x.AnimationOutput,
            animationName = x.AnimationName,
            animationSource = x.AnimationSource,
            skeletonPath = x.SkeletonPath,
            skeletonName = x.SkeletonName,
            modelBoneCount = x.ModelBoneCount,
            animationTrackCount = x.AnimationTrackCount,
            trackSource = x.TrackSource,
            matchedTrackBones = x.MatchedTrackBones,
            missingTrackBoneCount = x.MissingTrackBones.Length,
            missingTrackBones = x.MissingTrackBones.Take(64).ToArray(),
            trackCoverage = x.TrackCoverage,
            hierarchyCompatible = x.HierarchyCompatible,
            isContainerAnimation = x.IsContainerAnimation,
            referencedAnimationCount = x.ReferencedAnimations.Length,
            exportedReferencedAnimationCount = x.ExportedReferencedAnimations.Length,
            missingReferencedAnimationCount = x.MissingReferencedAnimations.Length,
            referencedAnimations = x.ReferencedAnimations.Take(64).ToArray(),
            missingReferencedAnimations = x.MissingReferencedAnimations.Take(64).ToArray(),
            hierarchyMismatchCount = x.HierarchyMismatches.Length,
            hierarchyMismatches = x.HierarchyMismatches.Take(32).ToArray(),
        });
    }

    private static AnimationValidationEntry[] BuildAnimationValidations(
        string root,
        List<JObject> catalogRows,
        SourceIndexSnapshot sourceIndex,
        ComponentAssetRelationLink[] componentAssetRelations)
    {
        var models = catalogRows
            .Where(x => string.Equals((string?)x["kind"], "Model", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace((string?)x["skeletonPath"]))
            .ToArray();
        var animations = catalogRows
            .Where(x => string.Equals((string?)x["kind"], "Animation", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace((string?)x["skeletonPath"]))
            .Where(x => IsExportedAnimationFileAvailable(root, x))
            .ToArray();
        var allAnimations = animations;
        var candidates = BuildModelAnimationCandidates(models, animations, sourceIndex, componentAssetRelations);
        Console.WriteLine($"UE animation validation candidates: models={models.Length}, animations={animations.Length}, pairs={candidates.Length}");

        var result = new List<AnimationValidationEntry>();
        var modelBoneCache = new Dictionary<string, ModelBoneLookup>(StringComparer.OrdinalIgnoreCase);
        var animationTrackCache = new Dictionary<string, SourceAnimationTrack[]>(StringComparer.OrdinalIgnoreCase);
        var skeletonLookupCache = new Dictionary<string, ModelBoneLookup>(StringComparer.OrdinalIgnoreCase);
        var exportedAnimationLookup = BuildExportedAnimationReferenceLookup(allAnimations);
        foreach (var candidate in candidates)
            result.Add(ValidateAnimationPair(root, candidate.Model, candidate.Animation, exportedAnimationLookup, sourceIndex, candidate.Reason, modelBoneCache, animationTrackCache, skeletonLookupCache));

        return result
            .OrderBy(x => x.ModelOutput, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.AnimationOutput, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ModelAnimationCandidate[] BuildModelAnimationCandidates(
        JObject[] models,
        JObject[] animations,
        SourceIndexSnapshot sourceIndex,
        ComponentAssetRelationLink[] componentAssetRelations)
    {
        var modelsByOutput = BuildUniqueAssetOutputLookup(models);
        var animationsByOutput = BuildUniqueAssetOutputLookup(animations);
        var animationsByObjectPath = BuildUniqueAnimationObjectPathLookup(animations);
        var animationsByOwner = BuildAnimationsByOwner(componentAssetRelations, sourceIndex, animationsByOutput, animationsByObjectPath);
        var animationsByPackage = BuildAnimationsByPackage(animations);
        var modelsByPackage = BuildModelsByPackage(models);
        var modelsByAnimBlueprintPackage = BuildModelsByAnimBlueprintTargetSkeleton(componentAssetRelations, modelsByOutput);
        var animationsByAnimBlueprintPackage = BuildAnimationsByAnimBlueprintPackage(sourceIndex, animationsByObjectPath);
        var animationsByAssetRegistrySourcePackage = BuildAnimationsByAssetRegistrySourcePackage(sourceIndex, animationsByPackage);
        var result = new Dictionary<string, ModelAnimationCandidate>(StringComparer.OrdinalIgnoreCase);

        // 同一个 UE owner 同时显式引用模型和动画时，才作为默认推荐候选。
        foreach (var group in componentAssetRelations
                     .Where(x => !string.IsNullOrWhiteSpace(x.OwnerObjectPath))
                     .GroupBy(x => x.OwnerObjectPath!, StringComparer.OrdinalIgnoreCase))
        {
            var modelLinks = group
                .Where(x => IsModelRelation(x.RelationType) && x.MatchStatus == "matched")
                .Select(x => NormalizeCatalogOutput(x.TargetAssetOutput))
                .Where(x => !string.IsNullOrWhiteSpace(x) && modelsByOutput.ContainsKey(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var animationLinks = group
                .Where(x => IsAnimationRelation(x.RelationType) && x.MatchStatus == "matched")
                .Select(x => NormalizeCatalogOutput(x.TargetAssetOutput))
                .Where(x => !string.IsNullOrWhiteSpace(x) && animationsByOutput.ContainsKey(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var animationContainerLinks = group
                .Where(x => IsAnimationRelation(x.RelationType) && x.MatchStatus == "unsupportedAnimationAsset")
                .Where(x => !string.IsNullOrWhiteSpace(x.TargetPath))
                .ToArray();

            foreach (var modelOutput in modelLinks)
            {
            foreach (var animationOutput in animationLinks)
                AddModelAnimationCandidate(result, modelsByOutput[modelOutput], animationsByOutput[animationOutput], "componentOwner");

                // BlendSpace / AimOffset / Montage 这类 UE 动画容器本身不直接导出为 ueanim。
                // 如果源索引能解析出它显式引用的 AnimSequence，就把可播放序列挂到同组件模型上。
                foreach (var containerLink in animationContainerLinks)
                foreach (var animation in FindAnimationsFromContainerSegments(containerLink.TargetPath, sourceIndex, animationsByObjectPath))
                    AddModelAnimationCandidate(result, modelsByOutput[modelOutput], animation, "componentOwnerBlendSpaceSample");

                foreach (var animClassOwner in FindAnimationClassOwners(group))
                {
                    if (!animationsByOwner.TryGetValue(animClassOwner, out var classAnimations))
                        classAnimations = [];

                    foreach (var animation in classAnimations)
                        AddModelAnimationCandidate(result, modelsByOutput[modelOutput], animation, "componentAnimClass");

                    var animClassPackage = GetPackageNameFromObjectPath(animClassOwner);
                    if (!string.IsNullOrWhiteSpace(animClassPackage))
                    {
                        if (animationsByAnimBlueprintPackage.TryGetValue(animClassPackage, out var directRefs))
                        {
                            foreach (var animation in directRefs)
                                AddModelAnimationCandidate(result, modelsByOutput[modelOutput], animation, "animBlueprintDirect");
                        }

                        if (animationsByAssetRegistrySourcePackage.TryGetValue(animClassPackage, out var dependencyRefs))
                        {
                            foreach (var animation in dependencyRefs)
                                AddModelAnimationCandidate(result, modelsByOutput[modelOutput], animation, "animBlueprintDependency");
                        }
                    }
                }
            }
        }

        foreach (var dependencyGroup in sourceIndex.AssetRegistryDependencies
                     .Where(IsCharacterDataSetDependency)
                     .GroupBy(x => x.SourcePackage, StringComparer.OrdinalIgnoreCase))
        {
            var dependencies = dependencyGroup.ToArray();
            var relatedModels = dependencies
                .SelectMany(x => modelsByPackage.TryGetValue(x.DependencyPackage, out var packageModels) ? packageModels : [])
                .DistinctBy(x => (string?)x["output"], StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (relatedModels.Length == 0)
                continue;

            var relatedAnimations = dependencies
                .SelectMany(x => animationsByPackage.TryGetValue(x.DependencyPackage, out var packageAnimations) ? packageAnimations : [])
                .DistinctBy(x => (string?)x["output"], StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (relatedAnimations.Length == 0)
                continue;

            foreach (var model in relatedModels)
            foreach (var animation in relatedAnimations)
                AddModelAnimationCandidate(result, model, animation, "characterDataSet");
        }

        foreach (var (animBlueprintPackage, skeletonModels) in modelsByAnimBlueprintPackage)
        {
            if (!animationsByAnimBlueprintPackage.TryGetValue(animBlueprintPackage, out var directAnimations))
                continue;

            foreach (var model in skeletonModels)
            foreach (var animation in directAnimations)
                AddModelAnimationCandidate(result, model, animation, "animBlueprintTargetSkeleton");
        }

        var modelsBySkeleton = models
            .GroupBy(x => (string)x["skeletonPath"]!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var animationsBySkeleton = animations
            .GroupBy(x => (string)x["skeletonPath"]!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

        // 如果某个 Skeleton 在当前素材库中只对应一个模型，同骨架动画关系是安全的默认候选。
        foreach (var (skeletonPath, skeletonModels) in modelsBySkeleton)
        {
            if (skeletonModels.Length != 1 || !animationsBySkeleton.TryGetValue(skeletonPath, out var skeletonAnimations))
                continue;

            foreach (var animation in skeletonAnimations)
                AddModelAnimationCandidate(result, skeletonModels[0], animation, "uniqueSkeleton");
        }

        // UE 项目里角色换装、NPC 变体、头/身部件经常共享同一个 USkeleton。
        // 只要后续骨骼覆盖验证通过，同 Skeleton 动画就是可复用候选；这里保留来源标记，避免伪装成组件显式引用。
        foreach (var (skeletonPath, skeletonModels) in modelsBySkeleton)
        {
            if (skeletonModels.Length <= 1 || !animationsBySkeleton.TryGetValue(skeletonPath, out var skeletonAnimations))
                continue;

            foreach (var model in skeletonModels)
            foreach (var animation in PickSharedSkeletonAnimations(model, skeletonAnimations))
                AddModelAnimationCandidate(result, model, animation, "sharedSkeleton");
        }

        return result.Values
            .OrderBy(x => (string?)x.Model["output"], StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => (string?)x.Animation["output"], StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Dictionary<string, JObject> BuildUniqueAssetOutputLookup(JObject[] assets)
    {
        return assets
            .Select(x => new { Output = NormalizeCatalogOutput((string?)x["output"] ?? (string?)x["source"]), Asset = x })
            .Where(x => !string.IsNullOrWhiteSpace(x.Output))
            .GroupBy(x => x.Output, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() == 1)
            .ToDictionary(x => x.Key, x => x.First().Asset, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, JObject> BuildUniqueAnimationObjectPathLookup(JObject[] animations)
    {
        return animations
            .Select(x => new { ObjectPath = NormalizeAnimationObjectPath((string?)x["objectPath"] ?? (string?)x["source"]), Animation = x })
            .Where(x => !string.IsNullOrWhiteSpace(x.ObjectPath))
            .GroupBy(x => x.ObjectPath, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() == 1)
            .ToDictionary(x => x.Key, x => x.First().Animation, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, JObject[]> BuildAnimationsByPackage(JObject[] animations)
        => animations
            .Select(x => new { Package = GetAssetPackageName(x), Animation = x })
            .Where(x => !string.IsNullOrWhiteSpace(x.Package))
            .GroupBy(x => x.Package, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Animation).ToArray(), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, JObject[]> BuildModelsByPackage(JObject[] models)
        => models
            .Select(x => new { Package = GetAssetPackageName(x), Model = x })
            .Where(x => !string.IsNullOrWhiteSpace(x.Package))
            .GroupBy(x => x.Package, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Model).ToArray(), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, JObject[]> BuildModelsByAnimBlueprintTargetSkeleton(
        ComponentAssetRelationLink[] componentAssetRelations,
        Dictionary<string, JObject> modelsByOutput)
    {
        var result = new Dictionary<string, List<JObject>>(StringComparer.OrdinalIgnoreCase);
        foreach (var relation in componentAssetRelations)
        {
            if (!relation.RelationType.Equals("Skeleton", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(relation.MatchStatus, "matched", StringComparison.OrdinalIgnoreCase))
                continue;

            var ownerText = $"{relation.OwnerType} {relation.OwnerObjectPath} {relation.SourcePath}";
            if (!ownerText.Contains("AnimBlueprint", StringComparison.OrdinalIgnoreCase))
                continue;

            var modelOutput = NormalizeCatalogOutput(relation.TargetAssetOutput);
            if (string.IsNullOrWhiteSpace(modelOutput) || !modelsByOutput.TryGetValue(modelOutput, out var model))
                continue;

            var animBlueprintPackage = GetPackageNameFromObjectPath(relation.OwnerObjectPath);
            if (string.IsNullOrWhiteSpace(animBlueprintPackage))
                continue;

            if (!result.TryGetValue(animBlueprintPackage, out var list))
            {
                list = [];
                result[animBlueprintPackage] = list;
            }

            if (!list.Any(x => string.Equals((string?)x["output"], (string?)model["output"], StringComparison.OrdinalIgnoreCase)))
                list.Add(model);
        }

        return result.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, JObject[]> BuildAnimationsByAnimBlueprintPackage(
        SourceIndexSnapshot sourceIndex,
        Dictionary<string, JObject> animationsByObjectPath)
    {
        var result = new Dictionary<string, List<JObject>>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in sourceIndex.AnimBlueprintAnimationRefs)
        {
            if (string.IsNullOrWhiteSpace(reference.ReferencedAnimationPath))
                continue;

            var normalizedAnimation = NormalizeAnimationObjectPath(reference.ReferencedAnimationPath);
            if (!animationsByObjectPath.TryGetValue(normalizedAnimation, out var animation))
                continue;

            var ownerPackage = GetPackageNameFromObjectPath(reference.GeneratedClassObjectPath);
            if (string.IsNullOrWhiteSpace(ownerPackage))
                ownerPackage = GetPackageNameFromObjectPath(reference.AnimBlueprintObjectPath);
            if (string.IsNullOrWhiteSpace(ownerPackage))
                continue;

            if (!result.TryGetValue(ownerPackage, out var list))
            {
                list = [];
                result[ownerPackage] = list;
            }

            if (!list.Any(x => string.Equals((string?)x["output"], (string?)animation["output"], StringComparison.OrdinalIgnoreCase)))
                list.Add(animation);
        }

        return result.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, JObject[]> BuildAnimationsByAssetRegistrySourcePackage(
        SourceIndexSnapshot sourceIndex,
        Dictionary<string, JObject[]> animationsByPackage)
    {
        var result = new Dictionary<string, List<JObject>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dependency in sourceIndex.AssetRegistryDependencies.Where(IsAnimationAssetRegistryDependency))
        {
            if (string.IsNullOrWhiteSpace(dependency.SourcePackage) ||
                string.IsNullOrWhiteSpace(dependency.DependencyPackage) ||
                !animationsByPackage.TryGetValue(dependency.DependencyPackage, out var animations))
                continue;

            if (!result.TryGetValue(dependency.SourcePackage, out var list))
            {
                list = [];
                result[dependency.SourcePackage] = list;
            }

            foreach (var animation in animations)
            {
                if (!list.Any(x => string.Equals((string?)x["output"], (string?)animation["output"], StringComparison.OrdinalIgnoreCase)))
                    list.Add(animation);
            }
        }

        return result.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsAnimationAssetRegistryDependency(SourceAssetRegistryDependency dependency)
    {
        var classText = $"{dependency.DependencyAssetClass} {dependency.DependencyAssetName} {dependency.DependencyPackage}";
        return dependency.DependencyCategory.Equals("Package", StringComparison.OrdinalIgnoreCase) &&
               (classText.Contains("AnimSequence", StringComparison.OrdinalIgnoreCase) ||
                classText.Contains("AnimMontage", StringComparison.OrdinalIgnoreCase) ||
                classText.Contains("AnimComposite", StringComparison.OrdinalIgnoreCase) ||
                classText.Contains("BlendSpace", StringComparison.OrdinalIgnoreCase) ||
                classText.Contains("AimOffset", StringComparison.OrdinalIgnoreCase) ||
                classText.Contains("/Animation/", StringComparison.OrdinalIgnoreCase) ||
                classText.Contains("/Animations/", StringComparison.OrdinalIgnoreCase) ||
                classText.Contains("/Anim/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCharacterDataSetDependency(SourceAssetRegistryDependency dependency)
    {
        var sourceClass = $"{dependency.SourceAssetClass} {dependency.SourceAssetName} {dependency.SourcePackage}";
        if (!dependency.DependencyCategory.Equals("Package", StringComparison.OrdinalIgnoreCase))
            return false;

        return sourceClass.Contains("DataAsset", StringComparison.OrdinalIgnoreCase) ||
               sourceClass.Contains("DataTable", StringComparison.OrdinalIgnoreCase) ||
               sourceClass.Contains("PrimaryAsset", StringComparison.OrdinalIgnoreCase) ||
               sourceClass.Contains("AssetSet", StringComparison.OrdinalIgnoreCase) ||
               sourceClass.Contains("AnimationSet", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetAssetPackageName(JObject asset)
    {
        foreach (var value in new[]
                 {
                     (string?)asset["objectPath"],
                     (string?)asset["source"],
                 })
        {
            var package = GetPackageNameFromObjectPath(value);
            if (!string.IsNullOrWhiteSpace(package))
                return package;
        }

        return "";
    }

    private static string GetPackageNameFromObjectPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var path = value.Replace('\\', '/').Trim();
        if (path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
        {
            path = path[..path.LastIndexOf('.')];
            var contentIndex = path.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
            if (contentIndex >= 0)
                return "/Game/" + path[(contentIndex + "/Content/".Length)..].Trim('/');
        }

        if (path.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/Engine/", StringComparison.OrdinalIgnoreCase))
        {
            var dot = path.LastIndexOf('.');
            return dot > 0 ? path[..dot] : path;
        }

        if (path.StartsWith("Game/", StringComparison.OrdinalIgnoreCase))
            return "/" + path;

        return "";
    }

    private static Dictionary<string, JObject[]> BuildAnimationsByOwner(
        ComponentAssetRelationLink[] componentAssetRelations,
        SourceIndexSnapshot sourceIndex,
        Dictionary<string, JObject> animationsByOutput,
        Dictionary<string, JObject> animationsByObjectPath)
    {
        var result = new Dictionary<string, List<JObject>>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in componentAssetRelations
                     .Where(x => !string.IsNullOrWhiteSpace(x.OwnerObjectPath))
                     .GroupBy(x => x.OwnerObjectPath!, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var link in group.Where(x => IsAnimationRelation(x.RelationType)))
            {
                foreach (var animation in ResolveRelationAnimations(link, sourceIndex, animationsByOutput, animationsByObjectPath))
                {
                    if (!result.TryGetValue(group.Key, out var list))
                    {
                        list = [];
                        result[group.Key] = list;
                    }

                    if (!list.Any(x => string.Equals((string?)x["output"], (string?)animation["output"], StringComparison.OrdinalIgnoreCase)))
                        list.Add(animation);
                }
            }
        }

        return result.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static JObject[] ResolveRelationAnimations(
        ComponentAssetRelationLink link,
        SourceIndexSnapshot sourceIndex,
        Dictionary<string, JObject> animationsByOutput,
        Dictionary<string, JObject> animationsByObjectPath)
    {
        if (string.Equals(link.MatchStatus, "matched", StringComparison.OrdinalIgnoreCase))
        {
            var output = NormalizeCatalogOutput(link.TargetAssetOutput);
            return !string.IsNullOrWhiteSpace(output) && animationsByOutput.TryGetValue(output, out var animation)
                ? [animation]
                : [];
        }

        return string.Equals(link.MatchStatus, "unsupportedAnimationAsset", StringComparison.OrdinalIgnoreCase)
            ? FindAnimationsFromContainerSegments(link.TargetPath, sourceIndex, animationsByObjectPath)
            : [];
    }

    private static JObject[] FindAnimationsFromContainerSegments(
        string? containerPath,
        SourceIndexSnapshot sourceIndex,
        Dictionary<string, JObject> animationsByObjectPath)
    {
        if (string.IsNullOrWhiteSpace(containerPath) || sourceIndex.SegmentsByAnimation.Count == 0)
            return [];

        var containerObjectPath = NormalizeAnimationObjectPath(containerPath);
        if (!sourceIndex.SegmentsByAnimation.TryGetValue(containerObjectPath, out var segments))
            return [];

        return segments
            .Select(x => NormalizeAnimationObjectPath(x.ReferencedAnimationPath))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => animationsByObjectPath.TryGetValue(x, out var animation) ? animation : null)
            .Where(x => x != null)
            .Cast<JObject>()
            .ToArray();
    }

    private static string NormalizeAnimationObjectPath(string? objectPath)
        => string.IsNullOrWhiteSpace(objectPath) ? "" : NormalizePackageObjectPath(objectPath);

    private static string[] FindAnimationClassOwners(IEnumerable<ComponentAssetRelationLink> ownerLinks)
    {
        return ownerLinks
            .Where(x => IsAnimationClassRelation(x.RelationType))
            .Select(x => NormalizeBlueprintGeneratedClassOwnerPath(x.TargetPath))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsAnimationClassRelation(string relationType)
        => relationType.Equals("AnimClass", StringComparison.OrdinalIgnoreCase)
           || relationType.Equals("AnimBlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeBlueprintGeneratedClassOwnerPath(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return "";

        var path = targetPath.Replace('\\', '/').Trim();
        var dot = path.LastIndexOf('.');
        if (dot <= 0 || dot == path.Length - 1)
            return path;

        var package = path[..dot];
        var className = path[(dot + 1)..];
        return className.EndsWith("_C", StringComparison.OrdinalIgnoreCase)
            ? $"{package}.Default__{className}"
            : path;
    }

    private static JObject[] PickSharedSkeletonAnimations(JObject model, JObject[] skeletonAnimations)
    {
        var modelSource = NormalizeCatalogOutput((string?)model["source"] ?? (string?)model["output"]);
        return skeletonAnimations
            .OrderByDescending(x => CountCommonPathPrefixSegments(
                modelSource,
                NormalizeCatalogOutput((string?)x["source"] ?? (string?)x["output"])))
            .ThenBy(x => (string?)x["source"] ?? (string?)x["output"], StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int CountCommonPathPrefixSegments(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return 0;

        var leftParts = left.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var length = Math.Min(leftParts.Length, rightParts.Length);
        var count = 0;
        for (var i = 0; i < length; i++)
        {
            if (!string.Equals(leftParts[i], rightParts[i], StringComparison.OrdinalIgnoreCase))
                break;
            count++;
        }

        return count;
    }

    private static bool IsExportedAnimationFileAvailable(string root, JObject animation)
    {
        if (!string.Equals((string?)animation["status"], "ok", StringComparison.OrdinalIgnoreCase))
            return false;

        var output = ResolveCatalogFile(root, (string?)animation["output"]);
        return !string.IsNullOrWhiteSpace(output)
               && File.Exists(output)
               && output.EndsWith(".ueanim", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddModelAnimationCandidate(
        Dictionary<string, ModelAnimationCandidate> candidates,
        JObject model,
        JObject animation,
        string reason)
    {
        if (!string.Equals((string?)model["skeletonPath"], (string?)animation["skeletonPath"], StringComparison.OrdinalIgnoreCase))
            return;

        var key = BuildPairKey(model, animation);
        if (!candidates.ContainsKey(key))
            candidates.Add(key, new ModelAnimationCandidate(model, animation, reason));
    }

    private static string NormalizeCatalogOutput(string? value)
        => string.IsNullOrWhiteSpace(value) ? "" : value.Replace('\\', '/');

    private static AnimationValidationEntry ValidateAnimationPair(
        string root,
        JObject model,
        JObject animation,
        HashSet<string> exportedAnimationLookup,
        SourceIndexSnapshot sourceIndex,
        string candidateReason = "",
        Dictionary<string, ModelBoneLookup>? modelBoneCache = null,
        Dictionary<string, SourceAnimationTrack[]>? animationTrackCache = null,
        Dictionary<string, ModelBoneLookup>? skeletonLookupCache = null)
    {
        var skeletonPath = (string?)model["skeletonPath"];
        var modelKey = NormalizeCatalogOutput((string?)model["output"] ?? (string?)model["source"]);
        var animationKey = NormalizeCatalogOutput((string?)animation["output"] ?? (string?)animation["source"]);
        var modelBones = GetOrBuildModelBones(root, model, skeletonPath, sourceIndex, modelKey, modelBoneCache);
        var animationTracks = GetOrBuildAnimationTracks(root, animation, sourceIndex, animationKey, animationTrackCache);
        var trackSource = animationTracks.Any(x => x.FromExportedUEAnim)
            ? "exportedUEAnim"
            : animationTracks.Length > 0
                ? "sourceIndex"
                : "none";
        var missingTrackBones = animationTracks
            .Where(x => !string.IsNullOrWhiteSpace(x.BoneName))
            .Where(x => !modelBones.ByName.ContainsKey(x.BoneName!))
            .Select(x => x.BoneName!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var namedTrackCount = animationTracks.Count(x => !string.IsNullOrWhiteSpace(x.BoneName));
        var matchedTrackBones = Math.Max(0, namedTrackCount - missingTrackBones.Length);
        var trackCoverage = namedTrackCount == 0 ? 0 : Math.Round((double)matchedTrackBones / namedTrackCount, 4);
        var hierarchyMismatches = CompareHierarchy(modelBones, animationTracks, sourceIndex, skeletonPath, skeletonLookupCache);
        var isContainerAnimation = IsContainerAnimation(animation);
        var referencedAnimations = BuildReferencedAnimationPaths(animation);
        var exportedReferencedAnimations = FindExportedReferencedAnimations(referencedAnimations, exportedAnimationLookup);
        var missingReferencedAnimations = referencedAnimations
            .Where(x => !exportedReferencedAnimations.Contains(x, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var status = "ok";
        var validationCategory = "directTrack";
        var reason = "Skeleton 路径一致，动画 track 骨骼被模型骨架覆盖，重叠骨骼层级兼容。";

        if (!sourceIndex.Available)
        {
            status = "warning";
            validationCategory = "missingSourceIndex";
            reason = "缺少 ue_source_index.db，无法验证骨骼覆盖和动画 track。";
        }
        else if (modelBones.Bones.Length == 0)
        {
            status = "warning";
            validationCategory = "missingModelBones";
            reason = "源索引中没有找到模型骨骼，暂时只能依赖 UE Skeleton 路径。";
        }
        else if (animationTracks.Length == 0)
        {
            if (isContainerAnimation)
            {
                if (missingReferencedAnimations.Length > 0)
                {
                    status = "warning";
                    validationCategory = "missingContainerReferences";
                    reason = "这是 Montage/Composite 容器动画，本身没有直接 bone track；部分 segment 引用的子动画尚未导出。";
                }
                else
                {
                    status = "ok";
                    validationCategory = "containerAnimation";
                    reason = "这是 Montage/Composite 容器动画，本身没有直接 bone track；segment/section 引用的子动画已导出。";
                }
            }
            else
            {
                status = "warning";
                validationCategory = "missingAnimationTracks";
                reason = "源索引和已导出的 .ueanim 中都没有找到动画 track，暂时只能依赖 UE Skeleton 路径。";
            }
        }
        else if (missingTrackBones.Length > 0)
        {
            if (trackCoverage >= 0.9
                || (matchedTrackBones > 0 && missingTrackBones.Length <= 2)
                || (trackCoverage >= 0.8 && IsOnlyAuxiliaryMissingTrackBones(missingTrackBones))
                || (trackCoverage >= 0.85 && IsOnlyFaceExpressionMissingTrackBones(missingTrackBones)))
            {
                status = "warning";
                validationCategory = "partialTrackCoverage";
                reason = trackCoverage >= 0.85 && IsOnlyFaceExpressionMissingTrackBones(missingTrackBones)
                    ? "动画主体骨骼覆盖较高，但部分脸部/表情骨骼 track 缺失；可做身体动作预览，表情需要复核。"
                    : "动画主体骨骼覆盖较高，但部分辅助骨骼 track 缺失，需要预览复核。";
            }
            else
            {
                status = "error";
                validationCategory = "missingTrackBones";
                reason = "动画 track 引用了较多模型骨架中不存在的骨骼。";
            }
        }
        else if (hierarchyMismatches.Length > 0)
        {
            status = "warning";
            validationCategory = "hierarchyMismatch";
            reason = "动画和模型的部分重叠骨骼父级不一致，需要人工复核。";
        }

        return new AnimationValidationEntry
        {
            PairKey = BuildPairKey(model, animation),
            CandidateReason = candidateReason,
            Status = status,
            ValidationCategory = validationCategory,
            Reason = reason,
            ModelOutput = (string?)model["output"] ?? "",
            ModelName = (string?)model["name"] ?? "",
            ModelSource = (string?)model["source"] ?? "",
            AnimationOutput = (string?)animation["output"] ?? "",
            AnimationName = (string?)animation["name"] ?? "",
            AnimationSource = (string?)animation["source"] ?? "",
            SkeletonPath = skeletonPath,
            SkeletonName = (string?)model["skeletonName"],
            ModelBoneCount = modelBones.Bones.Length,
            AnimationTrackCount = animationTracks.Length,
            TrackSource = trackSource,
            MatchedTrackBones = matchedTrackBones,
            MissingTrackBones = missingTrackBones,
            TrackCoverage = trackCoverage,
            HierarchyCompatible = hierarchyMismatches.Length == 0,
            IsContainerAnimation = isContainerAnimation,
            ReferencedAnimations = referencedAnimations,
            ExportedReferencedAnimations = exportedReferencedAnimations,
            MissingReferencedAnimations = missingReferencedAnimations,
            HierarchyMismatches = hierarchyMismatches,
        };
    }

    private static ModelBoneLookup GetOrBuildModelBones(
        string root,
        JObject model,
        string? skeletonPath,
        SourceIndexSnapshot sourceIndex,
        string modelKey,
        Dictionary<string, ModelBoneLookup>? cache)
    {
        if (cache == null || string.IsNullOrWhiteSpace(modelKey))
            return FindModelBones(root, model, skeletonPath, sourceIndex);

        if (!cache.TryGetValue(modelKey, out var lookup))
        {
            lookup = FindModelBones(root, model, skeletonPath, sourceIndex);
            cache[modelKey] = lookup;
        }

        return lookup;
    }

    private static SourceAnimationTrack[] GetOrBuildAnimationTracks(
        string root,
        JObject animation,
        SourceIndexSnapshot sourceIndex,
        string animationKey,
        Dictionary<string, SourceAnimationTrack[]>? cache)
    {
        if (cache == null || string.IsNullOrWhiteSpace(animationKey))
            return FindAnimationTracks(root, animation, sourceIndex);

        if (!cache.TryGetValue(animationKey, out var tracks))
        {
            tracks = FindAnimationTracks(root, animation, sourceIndex);
            cache[animationKey] = tracks;
        }

        return tracks;
    }

    private static ModelBoneLookup GetOrBuildSkeletonLookup(
        SourceIndexSnapshot sourceIndex,
        string skeletonPath,
        Dictionary<string, ModelBoneLookup>? cache)
    {
        if (cache == null)
            return BuildSkeletonLookup(sourceIndex, skeletonPath);

        if (!cache.TryGetValue(skeletonPath, out var lookup))
        {
            lookup = BuildSkeletonLookup(sourceIndex, skeletonPath);
            cache[skeletonPath] = lookup;
        }

        return lookup;
    }

    private static ModelBoneLookup BuildSkeletonLookup(SourceIndexSnapshot sourceIndex, string skeletonPath)
    {
        if (!sourceIndex.BonesBySkeleton.TryGetValue(skeletonPath, out var skeletonBones))
            return new ModelBoneLookup([]);

        return new ModelBoneLookup(PickSingleBoneOwner(skeletonBones, "Skeleton") ?? PickSingleBoneOwner(skeletonBones, null) ?? []);
    }

    private static bool IsOnlyAuxiliaryMissingTrackBones(string[] missingTrackBones)
    {
        if (missingTrackBones.Length == 0)
            return false;

        return missingTrackBones.All(IsAuxiliaryAnimationBoneName);
    }

    private static bool IsOnlyFaceExpressionMissingTrackBones(string[] missingTrackBones)
    {
        if (missingTrackBones.Length == 0)
            return false;

        var hasSpecificFaceBone = false;
        foreach (var boneName in missingTrackBones)
        {
            var normalized = NormalizeBoneNameForSemanticCheck(boneName);
            var isSpecificFaceBone =
                normalized.Contains("lip", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("brow", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("lid", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("eye", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("jaw", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("mouth", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("teeth", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("face", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("cheek", StringComparison.OrdinalIgnoreCase)
                || normalized.Contains("nose", StringComparison.OrdinalIgnoreCase);
            if (isSpecificFaceBone)
            {
                hasSpecificFaceBone = true;
                continue;
            }

            // head 只有和明确脸部/表情骨骼一起缺失时，才视为表情预览不完整，而不是整体骨架不匹配。
            if (normalized.Contains("head", StringComparison.OrdinalIgnoreCase))
                continue;

            return false;
        }

        return hasSpecificFaceBone;
    }

    private static bool IsAuxiliaryAnimationBoneName(string boneName)
    {
        var normalized = NormalizeBoneNameForSemanticCheck(boneName);
        return normalized.EndsWith("nub", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("hair", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("cloth", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("cape", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("skirt", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("tail", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("ribbon", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("accessory", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("weapon", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("hat", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("cap", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("hood", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("sock", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("shoe", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("scarf", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("strap", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("band", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("ornament", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("pendant", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("piaodai", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBoneNameForSemanticCheck(string boneName)
        => (boneName ?? "")
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);

    private static bool IsContainerAnimation(JObject animation)
        => animation["segments"] is JArray segments && segments.Count > 0
           || animation["sections"] is JArray sections && sections.Count > 0;

    private static string[] BuildReferencedAnimationPaths(JObject animation)
    {
        return ((JArray?)animation["segments"] ?? [])
            .Select(x => (string?)x["referencedAnimationPath"])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static HashSet<string> BuildExportedAnimationReferenceLookup(JObject[] allAnimations)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var animation in allAnimations)
        {
            AddAnimationReferenceKeys(result, (string?)animation["objectPath"]);
            AddAnimationReferenceKeys(result, (string?)animation["source"]);
            AddAnimationReferenceKeys(result, (string?)animation["output"]);
        }

        return result;
    }

    private static void AddAnimationReferenceKeys(HashSet<string> result, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        var normalized = value.Replace('\\', '/');
        result.Add(normalized);
        result.Add(NormalizePackageObjectPath(normalized));

        var extension = Path.GetExtension(normalized);
        if (!string.IsNullOrWhiteSpace(extension))
            normalized = normalized[..^extension.Length];

        result.Add(normalized);
        var contentIndex = normalized.IndexOf("/Content/", StringComparison.OrdinalIgnoreCase);
        if (contentIndex >= 0)
            result.Add(normalized[(contentIndex + 1)..]);
    }

    private static string[] FindExportedReferencedAnimations(string[] referencedAnimations, HashSet<string> exportedAnimationLookup)
    {
        if (referencedAnimations.Length == 0)
            return [];

        var result = new List<string>();
        foreach (var referenced in referencedAnimations)
        {
            if (IsReferencedAnimationExported(referenced, exportedAnimationLookup))
                result.Add(referenced);
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsReferencedAnimationExported(string referencedObjectPath, HashSet<string> exportedAnimationLookup)
    {
        if (exportedAnimationLookup.Contains(referencedObjectPath))
            return true;

        var normalized = NormalizePackageObjectPath(referencedObjectPath);
        if (exportedAnimationLookup.Contains(normalized))
            return true;

        var packageSuffix = BuildPackageSuffix(referencedObjectPath);
        if (string.IsNullOrWhiteSpace(packageSuffix))
            return false;

        var suffix = packageSuffix.TrimStart('/');
        return exportedAnimationLookup.Contains(packageSuffix)
               || exportedAnimationLookup.Contains(suffix);
    }

    private static bool AnimationSourceMatchesReference(string? sourceOrOutput, string referencedObjectPath)
    {
        var packageSuffix = BuildPackageSuffix(referencedObjectPath);
        if (string.IsNullOrWhiteSpace(sourceOrOutput) || string.IsNullOrWhiteSpace(packageSuffix))
            return false;

        var normalized = sourceOrOutput.Replace('\\', '/');
        var extension = Path.GetExtension(normalized);
        if (!string.IsNullOrWhiteSpace(extension))
            normalized = normalized[..^extension.Length];
        return normalized.EndsWith(packageSuffix.TrimStart('/'), StringComparison.OrdinalIgnoreCase) ||
               normalized.EndsWith(packageSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static ModelBoneLookup FindModelBones(string root, JObject model, string? skeletonPath, SourceIndexSnapshot sourceIndex)
    {
        var objectPath = (string?)model["objectPath"];
        if (!string.IsNullOrWhiteSpace(objectPath) && sourceIndex.BonesByOwner.TryGetValue(objectPath, out var byOwner))
            return new ModelBoneLookup(byOwner);

        if (!string.IsNullOrWhiteSpace(skeletonPath) && sourceIndex.BonesBySkeleton.TryGetValue(skeletonPath, out var bySkeleton))
        {
            var singleOwner = PickSingleBoneOwner(bySkeleton, "SkeletalMesh") ?? PickSingleBoneOwner(bySkeleton, null);
            return new ModelBoneLookup(singleOwner ?? []);
        }

        // 源索引偶尔漏掉道具骨骼；已导出的 GLB skin 是模型自身的可靠验证证据。
        return TryBuildModelBonesFromExportedGltf(root, model, skeletonPath);
    }

    private static ModelBoneLookup TryBuildModelBonesFromExportedGltf(string root, JObject model, string? skeletonPath)
    {
        var outputPath = ResolveCatalogFile(root, (string?)model["output"]);
        if (string.IsNullOrWhiteSpace(outputPath) || !File.Exists(outputPath) || !IsSupportedGltfModel(outputPath))
            return new ModelBoneLookup([]);

        try
        {
            var (gltf, _) = ReadGltfModel(outputPath);
            var nodes = ArrayOf(gltf, "nodes");
            var skins = ArrayOf(gltf, "skins");
            var bones = BuildBonesFromGltfSkins(outputPath, model, skeletonPath, nodes, skins);
            return new ModelBoneLookup(bones);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARN: exported glTF skin read failed {MakeRelative(root, outputPath)} ({ex.Message})");
            return new ModelBoneLookup([]);
        }
    }

    private static SourceBone[] BuildBonesFromGltfSkins(
        string outputPath,
        JObject model,
        string? skeletonPath,
        JObject[] nodes,
        JObject[] skins)
    {
        if (nodes.Length == 0 || skins.Length == 0)
            return [];

        var jointNodes = new List<int>();
        foreach (var skin in skins)
        {
            foreach (var joint in skin["joints"]?.Select(x => (int?)x) ?? [])
            {
                if (joint is >= 0 && joint < nodes.Length && !jointNodes.Contains(joint.Value))
                    jointNodes.Add(joint.Value);
            }
        }

        if (jointNodes.Count == 0)
            return [];

        var parentNodeByChild = BuildGltfParentNodeLookup(nodes);
        var boneIndexByNode = jointNodes
            .Select((nodeIndex, boneIndex) => new { nodeIndex, boneIndex })
            .ToDictionary(x => x.nodeIndex, x => x.boneIndex);
        var owner = (string?)model["objectPath"] ?? (string?)model["output"] ?? outputPath;

        return jointNodes
            .Select((nodeIndex, boneIndex) =>
            {
                var parentIndex = -1;
                if (parentNodeByChild.TryGetValue(nodeIndex, out var parentNodeIndex) &&
                    boneIndexByNode.TryGetValue(parentNodeIndex, out var parentBoneIndex))
                {
                    parentIndex = parentBoneIndex;
                }

                return new SourceBone
                {
                    SourcePath = outputPath,
                    OwnerObjectPath = owner,
                    OwnerType = "ExportedGltfSkin",
                    SkeletonPath = skeletonPath,
                    BoneIndex = boneIndex,
                    BoneName = ((string?)nodes[nodeIndex]["name"]) ?? $"node_{nodeIndex}",
                    ParentIndex = parentIndex,
                };
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.BoneName))
            .ToArray();
    }

    private static Dictionary<int, int> BuildGltfParentNodeLookup(JObject[] nodes)
    {
        var parentNodeByChild = new Dictionary<int, int>();
        for (var parentIndex = 0; parentIndex < nodes.Length; parentIndex++)
        {
            foreach (var child in nodes[parentIndex]["children"]?.Select(x => (int?)x) ?? [])
            {
                if (child is >= 0 && child < nodes.Length)
                    parentNodeByChild[child.Value] = parentIndex;
            }
        }

        return parentNodeByChild;
    }

    private static SourceAnimationTrack[] FindAnimationTracks(string root, JObject animation, SourceIndexSnapshot sourceIndex)
    {
        var objectPath = (string?)animation["objectPath"];
        if (!string.IsNullOrWhiteSpace(objectPath) && sourceIndex.TracksByAnimation.TryGetValue(objectPath, out var byObjectPath))
            return byObjectPath;

        var segmentTracks = FindContainerSegmentTracks(animation, sourceIndex);
        if (segmentTracks.Length > 0)
            return segmentTracks;

        return ReadExportedUEAnimTracks(root, animation);
    }

    private static SourceAnimationTrack[] FindContainerSegmentTracks(JObject animation, SourceIndexSnapshot sourceIndex)
    {
        var objectPath = (string?)animation["objectPath"];
        if (string.IsNullOrWhiteSpace(objectPath))
            return [];

        if (!sourceIndex.SegmentsByAnimation.TryGetValue(NormalizePackageObjectPath(objectPath), out var segments) || segments.Length == 0)
            return [];

        var result = new List<SourceAnimationTrack>();
        foreach (var segment in segments.OrderBy(x => x.SegmentIndex))
        {
            if (string.IsNullOrWhiteSpace(segment.ReferencedAnimationPath))
                continue;

            if (!sourceIndex.TracksByAnimation.TryGetValue(segment.ReferencedAnimationPath, out var tracks) || tracks.Length == 0)
                continue;

            // Montage/Composite 是容器资产，真正能证明骨骼覆盖的是它引用的子动画 track。
            // 导出的容器 .ueanim 可能包含 ref pose 或合并轨道，不能优先拿来判定模型缺骨骼。
            foreach (var track in tracks)
            {
                result.Add(new SourceAnimationTrack
                {
                    SourcePath = track.SourcePath,
                    AnimationObjectPath = objectPath,
                    SkeletonPath = track.SkeletonPath ?? segment.SkeletonPath,
                    TrackIndex = result.Count,
                    BoneIndex = track.BoneIndex,
                    BoneName = track.BoneName,
                    FromExportedUEAnim = false,
                });
            }
        }

        return result
            .Where(x => !string.IsNullOrWhiteSpace(x.BoneName))
            .GroupBy(x => x.BoneName!, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .OrderBy(x => x.TrackIndex)
            .ToArray();
    }

    private static SourceAnimationTrack[] ReadExportedUEAnimTracks(string root, JObject animation)
    {
        var output = ResolveCatalogFile(root, (string?)animation["output"]);
        if (string.IsNullOrWhiteSpace(output) || !File.Exists(output) || !output.EndsWith(".ueanim", StringComparison.OrdinalIgnoreCase))
            return [];

        try
        {
            var data = UEAnimReader.Read(output);
            return data.Tracks
                .Select((track, index) => new SourceAnimationTrack
                {
                    SourcePath = output,
                    AnimationObjectPath = (string?)animation["objectPath"] ?? "",
                    SkeletonPath = (string?)animation["skeletonPath"],
                    TrackIndex = index,
                    BoneIndex = -1,
                    BoneName = track.BoneName,
                    FromExportedUEAnim = true,
                })
                .ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARN: exported ueanim track read failed {MakeRelative(root, output)} ({ex.Message})");
            return [];
        }
    }

    private static string ResolveCatalogFile(string root, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return Path.IsPathRooted(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(Path.Combine(root, value));
    }

    private static string[] CompareHierarchy(
        ModelBoneLookup modelBones,
        SourceAnimationTrack[] animationTracks,
        SourceIndexSnapshot sourceIndex,
        string? skeletonPath,
        Dictionary<string, ModelBoneLookup>? skeletonLookupCache)
    {
        if (modelBones.Bones.Length == 0 || animationTracks.Length == 0)
            return [];

        skeletonPath = !string.IsNullOrWhiteSpace(skeletonPath)
            ? skeletonPath
            : animationTracks.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.SkeletonPath))?.SkeletonPath;
        if (string.IsNullOrWhiteSpace(skeletonPath))
            return [];

        var skeletonLookup = GetOrBuildSkeletonLookup(sourceIndex, skeletonPath, skeletonLookupCache);
        if (skeletonLookup.Bones.Length == 0)
            return [];

        var mismatches = new List<string>();
        foreach (var track in animationTracks)
        {
            if (string.IsNullOrWhiteSpace(track.BoneName))
                continue;
            if (!modelBones.ByName.TryGetValue(track.BoneName, out var modelBone))
                continue;
            if (!skeletonLookup.ByName.TryGetValue(track.BoneName, out var skeletonBone))
                continue;

            var modelParent = modelBones.GetParentName(modelBone);
            var skeletonParent = skeletonLookup.GetParentName(skeletonBone);
            if (!string.Equals(modelParent, skeletonParent, StringComparison.OrdinalIgnoreCase))
                mismatches.Add($"{track.BoneName}: modelParent={modelParent ?? "<root>"}, skeletonParent={skeletonParent ?? "<root>"}");
        }

        return mismatches.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static SourceBone[]? PickSingleBoneOwner(SourceBone[] bones, string? preferredOwnerType)
    {
        var groups = bones
            .Where(x => string.IsNullOrWhiteSpace(preferredOwnerType) || string.Equals(x.OwnerType, preferredOwnerType, StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.OwnerObjectPath, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return groups.Length == 0
            ? null
            : groups[0].OrderBy(x => x.BoneIndex).ToArray();
    }

    private static string BuildPairKey(JObject model, JObject animation)
        => $"{((string?)model["output"] ?? "").Replace('\\', '/').ToLowerInvariant()}|{((string?)animation["output"] ?? "").Replace('\\', '/').ToLowerInvariant()}";

    private static JObject WriteModelAnimationRelations(
        string root,
        List<JObject> catalogRows,
        AnimationValidationSummary animationValidation,
        bool writeCompatibilityJson)
    {
        var models = catalogRows
            .Where(x => string.Equals((string?)x["kind"], "Model", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace((string?)x["skeletonPath"]))
            .ToArray();
        var animations = catalogRows
            .Where(x => string.Equals((string?)x["kind"], "Animation", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace((string?)x["skeletonPath"]))
            .Where(x => IsExportedAnimationFileAvailable(root, x))
            .ToArray();
        var animationsByOutput = BuildUniqueAssetOutputLookup(animations);
        var validationsByModel = animationValidation.Validations
            .GroupBy(x => NormalizeCatalogOutput(x.ModelOutput), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

        var relations = new JArray();
        foreach (var model in models.OrderBy(x => (string?)x["output"], StringComparer.OrdinalIgnoreCase))
        {
            var skeletonPath = (string)model["skeletonPath"]!;
            var modelOutput = NormalizeCatalogOutput((string?)model["output"] ?? (string?)model["source"]);
            validationsByModel.TryGetValue(modelOutput, out var matchedValidations);
            var relationAnimations = (matchedValidations ?? [])
                .OrderBy(x => x.AnimationOutput, StringComparer.OrdinalIgnoreCase)
                .Select(validation =>
                {
                    animationsByOutput.TryGetValue(NormalizeCatalogOutput(validation.AnimationOutput), out var animation);
                    var exportStatus = (string?)animation?["status"];
                    var isUsableCandidate = IsUsableAnimationCandidate(exportStatus, validation.Status, validation.CandidateReason);
                    var usageEvidence = ClassifyAnimationUsageEvidence(validation.CandidateReason);
                    var confidenceTier = ClassifyAnimationConfidenceTier(validation.CandidateReason);
                    var relationshipKind = ClassifyAnimationRelationshipKind(validation.CandidateReason);
                    return new
                    {
                        name = animation?["name"] ?? validation.AnimationName,
                        source = animation?["source"] ?? validation.AnimationSource,
                        output = animation?["output"] ?? validation.AnimationOutput,
                        status = exportStatus,
                        duration = animation?["duration"],
                        frameCount = animation?["frameCount"],
                        trackCount = animation?["trackCount"],
                        segmentCount = animation?["segments"] is JArray segments ? segments.Count : 0,
                        referencedAnimationCount = CountReferencedAnimations(animation?["segments"] as JArray),
                        segments = animation?["segments"],
                        sectionCount = animation?["sections"] is JArray sections ? sections.Count : 0,
                        sections = animation?["sections"],
                        validationStatus = validation.Status,
                        validationCategory = validation.ValidationCategory,
                        validationReason = validation.Reason,
                        relationSource = validation.CandidateReason,
                        usageEvidence,
                        confidenceTier,
                        relationshipKind,
                        recommendedUse = ClassifyAnimationRecommendedUse(exportStatus, validation.Status, validation.CandidateReason),
                        evidenceChain = BuildAnimationEvidenceChain(validation.CandidateReason),
                        isExplicitUsage = usageEvidence == "explicitUsage",
                        isDeterministicUsage = IsDeterministicAnimationUsage(validation.CandidateReason),
                        isSkeletonCompatible = usageEvidence == "skeletonCompatibility",
                        isCompatibilityCandidate = usageEvidence == "skeletonCompatibility",
                        trackCoverage = validation.TrackCoverage,
                        hierarchyCompatible = validation.HierarchyCompatible,
                        isContainerAnimation = validation.IsContainerAnimation,
                        exportedReferencedAnimationCount = validation.ExportedReferencedAnimations.Length,
                        missingReferencedAnimationCount = validation.MissingReferencedAnimations.Length,
                        missingReferencedAnimations = validation.MissingReferencedAnimations.Take(32).ToArray(),
                        missingTrackBones = validation.MissingTrackBones.Take(32).ToArray(),
                        isUsableCandidate,
                    };
                })
                .ToArray();
            var usableAnimationCount = relationAnimations.Count(x => x.isUsableCandidate);
            var confidence = usableAnimationCount <= 0
                ? relationAnimations.Length > 0 ? "RelatedButNotUsable" : "NoMatchingAnimationExported"
                : relationAnimations.Any(x => string.Equals(x.relationSource, "componentOwner", StringComparison.OrdinalIgnoreCase))
                    ? "ExplicitComponent"
                    : relationAnimations.Any(x => string.Equals(x.relationSource, "componentOwnerBlendSpaceSample", StringComparison.OrdinalIgnoreCase))
                        ? "ExplicitComponent"
                    : relationAnimations.Any(x =>
                            string.Equals(x.relationSource, "animBlueprintDirect", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.relationSource, "animBlueprintTargetSkeleton", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(x.relationSource, "animBlueprintDependency", StringComparison.OrdinalIgnoreCase))
                            ? "AnimBlueprintDirect"
                            : relationAnimations.Any(x => string.Equals(x.relationSource, "characterDataSet", StringComparison.OrdinalIgnoreCase))
                                ? "CharacterDataSet"
                                : relationAnimations.Any(x => string.Equals(x.relationSource, "uniqueSkeleton", StringComparison.OrdinalIgnoreCase))
                                    ? "UniqueSkeletonCompatible"
                                    : relationAnimations.Any(x => string.Equals(x.relationSource, "sharedSkeleton", StringComparison.OrdinalIgnoreCase))
                                        ? "SharedSkeletonCompatible"
                                        : relationAnimations.Any(x => string.Equals(x.relationSource, "componentAnimClass", StringComparison.OrdinalIgnoreCase))
                                            ? "AnimClassContext"
                                            : "RelatedButNotUsable";

            relations.Add(JObject.FromObject(new
            {
                model = model["output"],
                modelName = model["name"],
                modelSource = model["source"],
                skeletonPath,
                skeletonName = model["skeletonName"],
                confidence,
                totalAnimationCount = relationAnimations.Length,
                usableAnimationCount,
                animations = relationAnimations,
                explicitUsageAnimationCount = relationAnimations.Count(x => x.isExplicitUsage),
                deterministicUsageAnimationCount = relationAnimations.Count(x => x.isDeterministicUsage),
                skeletonCompatibleAnimationCount = relationAnimations.Count(x => x.isSkeletonCompatible),
                compatibilityCandidateAnimationCount = relationAnimations.Count(x => x.isCompatibilityCandidate),
            }));
        }

        var summary = new JObject
        {
            ["generatedAt"] = DateTime.UtcNow.ToString("O"),
            ["rule"] = "默认可用动画来自显式组件关系、唯一 Skeleton 关系，以及通过骨骼覆盖验证的共享 Skeleton 关系；不按目录名、角色名或文件名前缀硬猜。",
            ["totals"] = JObject.FromObject(new
            {
                models = models.Length,
                animations = animations.Length,
                matchedModels = relations.Count(x => ((int?)x["usableAnimationCount"] ?? 0) > 0),
                relatedModels = relations.Count(x => ((JArray)x["animations"]!).Count > 0),
                relatedAnimations = relations.Sum(x => ((JArray)x["animations"]!).Count),
                usableAnimations = relations.Sum(x => (int?)x["usableAnimationCount"] ?? 0),
                explicitUsageAnimations = relations.Sum(x => (int?)x["explicitUsageAnimationCount"] ?? 0),
                deterministicUsageAnimations = relations.Sum(x => (int?)x["deterministicUsageAnimationCount"] ?? 0),
                skeletonCompatibleAnimations = relations.Sum(x => (int?)x["skeletonCompatibleAnimationCount"] ?? 0),
                compatibilityCandidateAnimations = relations.Sum(x => (int?)x["compatibilityCandidateAnimationCount"] ?? 0),
            }),
            ["relations"] = relations,
        };

        var path = Path.Combine(root, "model_animations.json");
        if (writeCompatibilityJson)
            File.WriteAllText(path, summary.ToString(Formatting.Indented));
        else
            DeleteIfExists(path);

        return summary;
    }

    private static bool IsUsableAnimationCandidate(string? exportStatus, string validationStatus, string? candidateReason)
    {
        if (!string.IsNullOrWhiteSpace(exportStatus) &&
            !string.Equals(exportStatus, "ok", StringComparison.OrdinalIgnoreCase))
            return false;

        return !string.Equals(validationStatus, "error", StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifyAnimationUsageEvidence(string? candidateReason)
    {
        return candidateReason switch
        {
            "componentOwner" or "componentOwnerBlendSpaceSample" => "explicitUsage",
            "animBlueprintDirect" or "animBlueprintTargetSkeleton" or "animBlueprintDependency" => "animBlueprintDirect",
            "characterDataSet" => "characterDataSet",
            "componentAnimClass" => "animClassContext",
            "uniqueSkeleton" or "sharedSkeleton" => "skeletonCompatibility",
            _ => "unknown",
        };
    }

    private static string ClassifyAnimationConfidenceTier(string? candidateReason)
    {
        return candidateReason switch
        {
            "componentOwner" or "componentOwnerBlendSpaceSample" => "ExplicitComponent",
            "animBlueprintDirect" or "animBlueprintTargetSkeleton" or "animBlueprintDependency" => "AnimBlueprintDirect",
            "characterDataSet" => "CharacterDataSet",
            "componentAnimClass" => "AnimClassContext",
            "uniqueSkeleton" => "UniqueSkeletonCompatible",
            "sharedSkeleton" => "SharedSkeletonCompatible",
            _ => "Unknown",
        };
    }

    private static string ClassifyAnimationRelationshipKind(string? candidateReason)
    {
        return candidateReason switch
        {
            "componentOwner" or "componentOwnerBlendSpaceSample" or
                "animBlueprintDirect" or "animBlueprintTargetSkeleton" or "animBlueprintDependency" or
                "characterDataSet" => "deterministicUsage",
            "componentAnimClass" => "contextualUsage",
            "uniqueSkeleton" or "sharedSkeleton" => "compatibilityCandidate",
            _ => "unknown",
        };
    }

    private static string ClassifyAnimationRecommendedUse(string? exportStatus, string validationStatus, string? candidateReason)
    {
        if (!IsUsableAnimationCandidate(exportStatus, validationStatus, candidateReason))
            return "notUsable";

        var relationshipKind = ClassifyAnimationRelationshipKind(candidateReason);
        if (!string.Equals(validationStatus, "ok", StringComparison.OrdinalIgnoreCase))
            return relationshipKind == "compatibilityCandidate" ? "compatibleNeedsReview" : "manualReview";

        return relationshipKind switch
        {
            "deterministicUsage" => "defaultTrusted",
            "compatibilityCandidate" => "compatibleCandidate",
            "contextualUsage" => "manualReview",
            _ => "manualReview",
        };
    }

    private static string[] BuildAnimationEvidenceChain(string? candidateReason)
    {
        return candidateReason switch
        {
            "componentOwner" => ["USkeletalMeshComponent.SkeletalMesh", "USkeletalMeshComponent.AnimationData.AnimToPlay", "sameOwnerObject", "sameUSkeleton", "trackValidation"],
            "componentOwnerBlendSpaceSample" => ["USkeletalMeshComponent.SkeletalMesh", "animationContainerReference", "animation_segments.referenced_animation_path", "sameUSkeleton", "trackValidation"],
            "animBlueprintDirect" => ["USkeletalMeshComponent.AnimClass", "anim_blueprint_animation_refs", "sameUSkeleton", "trackValidation"],
            "animBlueprintTargetSkeleton" => ["AnimBlueprintGeneratedClass.TargetSkeleton", "matchedModelSkeleton", "anim_blueprint_animation_refs", "sameUSkeleton", "trackValidation"],
            "animBlueprintDependency" => ["USkeletalMeshComponent.AnimClass", "asset_registry_dependencies", "sameUSkeleton", "trackValidation"],
            "characterDataSet" => ["asset_registry_dependencies", "dataAssetReferencesModelAndAnimation", "sameUSkeleton", "trackValidation"],
            "componentAnimClass" => ["USkeletalMeshComponent.AnimClass", "componentOwnerAnimationReferences", "sameUSkeleton", "trackValidation"],
            "uniqueSkeleton" => ["sameUSkeleton", "singleModelForSkeletonInLibrary", "trackValidation"],
            "sharedSkeleton" => ["sameUSkeleton", "sharedSkeletonCompatibilityCandidate", "trackValidation"],
            _ => ["unknown"],
        };
    }

    private static bool IsDeterministicAnimationUsage(string? candidateReason)
    {
        return candidateReason is "componentOwner"
            or "componentOwnerBlendSpaceSample"
            or "animBlueprintDirect"
            or "animBlueprintTargetSkeleton"
            or "animBlueprintDependency"
            or "characterDataSet";
    }

    private static bool IsUsableRelationAnimation(JObject animation)
    {
        var explicitFlag = (bool?)animation["isUsableCandidate"];
        if (explicitFlag != null)
            return explicitFlag.Value;

        return IsUsableAnimationCandidate(
            (string?)animation["status"],
            (string?)animation["validationStatus"] ?? "",
            (string?)animation["relationSource"]);
    }

    private static int CountReferencedAnimations(JArray? segments)
        => segments == null
            ? 0
            : segments
                .OfType<JObject>()
                .Select(x => (string?)x["referencedAnimationPath"])
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

    private static void WriteModelValidation(string root, List<ModelValidationEntry> reports, bool writeCompatibilityJson)
    {
        var summary = new
        {
            generatedAt = DateTime.UtcNow.ToString("O"),
            rule = "验证 GLB/glTF 静态结构、材质、贴图和 skin。动画正确性需要后续 UE 动画索引和预览验证。",
            totals = new
            {
                models = reports.Count,
                ok = reports.Count(x => x.Status == "ok"),
                warning = reports.Count(x => x.Status == "warning"),
                error = reports.Count(x => x.Status == "error"),
                withSkin = reports.Count(x => x.SkinCount > 0),
                withEmbeddedImages = reports.Count(x => x.EmbeddedImageCount > 0),
                withAnimations = reports.Count(x => x.AnimationCount > 0),
            },
            models = reports.Select(x => new
            {
                status = x.Status,
                path = x.RelativePath,
                name = x.Name,
                resourceKind = x.ResourceKind,
                counts = new
                {
                    nodes = x.NodeCount,
                    meshes = x.MeshCount,
                    skins = x.SkinCount,
                    bones = x.BoneCount,
                    materials = x.MaterialCount,
                    images = x.ImageCount,
                    embeddedImages = x.EmbeddedImageCount,
                    animations = x.AnimationCount,
                },
                materialNames = x.MaterialNames,
                matchedMaterialSidecars = x.MatchedMaterialSidecars,
                missingMaterialSidecars = x.MissingMaterialSidecars,
                externalMaterialNames = x.ExternalMaterialNames,
                externalMaterialTextureCount = x.ExternalMaterialTextureCount,
                skeletonHash = x.SkeletonHash,
                bbox = x.BBox,
                notes = x.Notes,
            }),
        };
        var path = Path.Combine(root, "model_validation.json");
        if (writeCompatibilityJson)
        {
            File.WriteAllText(
                path,
                JsonConvert.SerializeObject(summary, Formatting.Indented),
                Encoding.UTF8);
        }
        else
        {
            DeleteIfExists(path);
        }
    }

    private static JObject WriteModelCoverage(
        string root,
        List<JObject> catalogRows,
        List<ModelValidationEntry> reports,
        ComponentAssetRelationLink[] componentAssetRelations,
        JObject modelAnimationRelations,
        SourceIndexSnapshot sourceIndex,
        bool writeCompatibilityJson)
    {
        var modelRows = catalogRows
            .Where(x => string.Equals((string?)x["kind"], "Model", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var reportsByPath = reports
            .GroupBy(x => NormalizeCatalogOutput(x.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var componentRefsByOutput = componentAssetRelations
            .Where(x => IsModelRelation(x.RelationType) && string.Equals(x.MatchStatus, "matched", StringComparison.OrdinalIgnoreCase))
            .Select(x => NormalizeCatalogOutput(x.TargetAssetOutput))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .GroupBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        var animationCountsByOutput = ((JArray?)modelAnimationRelations["relations"] ?? [])
            .OfType<JObject>()
            .Select(x => new
            {
                Output = NormalizeCatalogOutput((string?)x["model"]),
                Count = (int?)x["usableAnimationCount"] ?? ((JArray?)x["animations"] ?? [])
                    .OfType<JObject>()
                    .Count(IsUsableRelationAnimation),
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Output))
            .GroupBy(x => x.Output, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Count), StringComparer.OrdinalIgnoreCase);
        var sourceIndexObjectsByModel = BuildSourceIndexObjectCounts(modelRows, sourceIndex);

        var rows = modelRows
            .Select(model =>
            {
                var output = NormalizeCatalogOutput((string?)model["output"] ?? (string?)model["source"]);
                reportsByPath.TryGetValue(output, out var report);
                var source = (string?)model["source"] ?? "";
                var taskSignals = FindTaskSignals(source);
                componentRefsByOutput.TryGetValue(output, out var componentRefCount);
                animationCountsByOutput.TryGetValue(output, out var animationCount);
                sourceIndexObjectsByModel.TryGetValue(output, out var sourceIndexObjectCount);
                var hasSkin = report?.SkinCount > 0;
                return new ModelCoverageRow
                {
                    Name = (string?)model["name"] ?? report?.Name ?? Path.GetFileNameWithoutExtension(output),
                    Output = output,
                    Source = source,
                    ObjectPath = (string?)model["objectPath"],
                    ResourceKind = (string?)model["resourceKind"] ?? report?.ResourceKind ?? InferResourceKind(output),
                    SourceType = (string?)model["sourceType"] ?? (report?.SkinCount > 0 ? "SkeletalOrSkinnedMeshGltf" : "StaticMeshGltf"),
                    ValidationStatus = report?.Status ?? (string?)model["validationStatus"] ?? "unknown",
                    IsStatic = !hasSkin,
                    HasSkin = hasSkin,
                    HasSkeletonPath = !string.IsNullOrWhiteSpace((string?)model["skeletonPath"]),
                    MaterialCount = report?.MaterialCount ?? 0,
                    TextureCount = report?.ExternalMaterialTextureCount ?? 0,
                    ComponentReferenceCount = componentRefCount,
                    AnimationCandidateCount = animationCount,
                    SourceIndexObjectCount = sourceIndexObjectCount,
                    TaskSignals = taskSignals,
                };
            })
            .OrderBy(x => x.ResourceKind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Output, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var byResourceKind = rows
            .GroupBy(x => x.ResourceKind, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => BuildModelCoverageGroup(x.Key, x))
            .ToArray();
        var taskRows = rows
            .Where(x => x.TaskSignals.Length > 0 || string.Equals(x.ResourceKind, "Prop", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var byTaskSignal = taskRows
            .SelectMany(row => (row.TaskSignals.Length == 0 ? ["prop"] : row.TaskSignals).Select(signal => new { signal, row }))
            .GroupBy(x => x.signal, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new
            {
                signal = x.Key,
                total = x.Count(),
                staticModels = x.Count(y => y.row.IsStatic),
                skinnedModels = x.Count(y => y.row.HasSkin),
                withComponentReferences = x.Count(y => y.row.ComponentReferenceCount > 0),
                withSourceIndexObjects = x.Count(y => y.row.SourceIndexObjectCount > 0),
                withAnimationCandidates = x.Count(y => y.row.AnimationCandidateCount > 0),
            })
            .ToArray();

        var json = JObject.FromObject(new
        {
            generatedAt = DateTime.UtcNow.ToString("O"),
            rule = "模型覆盖报告只按导出 catalog、GLB 验证和 UE 显式组件引用统计；任务/交互/目标模型来自通用路径词元，不按单个游戏名称硬猜。",
            totals = new
            {
                models = rows.Length,
                staticModels = rows.Count(x => x.IsStatic),
                skinnedModels = rows.Count(x => x.HasSkin),
                characterModels = rows.Count(x => string.Equals(x.ResourceKind, "Character", StringComparison.OrdinalIgnoreCase)),
                skinnedCharacterModels = rows.Count(x => x.HasSkin && string.Equals(x.ResourceKind, "Character", StringComparison.OrdinalIgnoreCase)),
                taskOrPropModels = taskRows.Length,
                environmentModels = rows.Count(x => string.Equals(x.ResourceKind, "Environment", StringComparison.OrdinalIgnoreCase)),
                withComponentReferences = rows.Count(x => x.ComponentReferenceCount > 0),
                withSourceIndexObjects = rows.Count(x => x.SourceIndexObjectCount > 0),
                withAnimationCandidates = rows.Count(x => x.AnimationCandidateCount > 0),
                warnings = rows.Count(x => string.Equals(x.ValidationStatus, "warning", StringComparison.OrdinalIgnoreCase)),
                errors = rows.Count(x => string.Equals(x.ValidationStatus, "error", StringComparison.OrdinalIgnoreCase)),
            },
            byResourceKind,
            taskCoverage = new
            {
                total = taskRows.Length,
                quality = BuildTaskModelQuality(taskRows),
                bySignal = byTaskSignal,
                examples = taskRows
                    .OrderByDescending(x => x.ComponentReferenceCount)
                    .ThenBy(x => x.Output, StringComparer.OrdinalIgnoreCase)
                    .Take(200)
                    .Select(BuildModelCoverageJsonRow)
                    .ToArray(),
            },
            models = rows.Select(BuildModelCoverageJsonRow).ToArray(),
        });

        var path = Path.Combine(root, "model_coverage.json");
        if (writeCompatibilityJson)
            File.WriteAllText(path, json.ToString(Formatting.Indented), Encoding.UTF8);
        else
            DeleteIfExists(path);

        return json;
    }

    private static object BuildModelCoverageGroup(string resourceKind, IEnumerable<ModelCoverageRow> rows)
    {
        var array = rows.ToArray();
        return new
        {
            resourceKind,
            total = array.Length,
            staticModels = array.Count(x => x.IsStatic),
            skinnedModels = array.Count(x => x.HasSkin),
            withComponentReferences = array.Count(x => x.ComponentReferenceCount > 0),
            withSourceIndexObjects = array.Count(x => x.SourceIndexObjectCount > 0),
            withAnimationCandidates = array.Count(x => x.AnimationCandidateCount > 0),
            warnings = array.Count(x => string.Equals(x.ValidationStatus, "warning", StringComparison.OrdinalIgnoreCase)),
            errors = array.Count(x => string.Equals(x.ValidationStatus, "error", StringComparison.OrdinalIgnoreCase)),
        };
    }

    private static object BuildModelCoverageSignalGroup(string signal, IEnumerable<ModelCoverageRow> rows)
    {
        var array = rows.ToArray();
        return new
        {
            signal,
            total = array.Length,
            staticModels = array.Count(x => x.IsStatic),
            skinnedModels = array.Count(x => x.HasSkin),
            withComponentReferences = array.Count(x => x.ComponentReferenceCount > 0),
            withSourceIndexObjects = array.Count(x => x.SourceIndexObjectCount > 0),
            withAnimationCandidates = array.Count(x => x.AnimationCandidateCount > 0),
            ok = array.Count(x => string.Equals(x.ValidationStatus, "ok", StringComparison.OrdinalIgnoreCase)),
            warnings = array.Count(x => string.Equals(x.ValidationStatus, "warning", StringComparison.OrdinalIgnoreCase)),
            examples = array
                .OrderByDescending(x => x.ComponentReferenceCount)
                .ThenByDescending(x => x.AnimationCandidateCount)
                .ThenBy(x => x.Output, StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .Select(BuildModelCoverageJsonRow)
                .ToArray(),
        };
    }

    private static object BuildModelCoverageJsonRow(ModelCoverageRow row)
        => new
        {
            row.Name,
            row.Output,
            row.Source,
            row.ObjectPath,
            row.ResourceKind,
            row.SourceType,
            row.ValidationStatus,
            row.IsStatic,
            row.HasSkin,
            row.HasSkeletonPath,
            row.MaterialCount,
            row.TextureCount,
            row.ComponentReferenceCount,
            row.SourceIndexObjectCount,
            row.AnimationCandidateCount,
            row.TaskSignals,
        };

    private static Dictionary<string, int> BuildSourceIndexObjectCounts(JObject[] modelRows, SourceIndexSnapshot sourceIndex)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!sourceIndex.Available)
            return result;

        var sourcePaths = modelRows
            .Select(x => NormalizeCatalogOutput((string?)x["source"]))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var objectPaths = modelRows
            .Select(x => NormalizeOptionalPackageObjectPath((string?)x["objectPath"]))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var bySourcePath = QuerySourceObjectCounts(sourceIndex.Path, "source_path", sourcePaths, NormalizeCatalogOutput);
        var byObjectPath = QuerySourceObjectCounts(sourceIndex.Path, "object_path", objectPaths, NormalizeOptionalPackageObjectPath);

        // 兼容极旧的源索引：没有 source_objects 时才退回 package_object_maps。
        if (bySourcePath.Count == 0 && byObjectPath.Count == 0 && sourceIndex.PackageObjectMaps.Length > 0)
        {
            byObjectPath = sourceIndex.PackageObjectMaps
                .Where(x => !string.IsNullOrWhiteSpace(x.ObjectPath))
                .GroupBy(x => NormalizePackageObjectPath(x.ObjectPath!), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
            bySourcePath = sourceIndex.PackageObjectMaps
                .Where(x => !string.IsNullOrWhiteSpace(x.SourcePath))
                .GroupBy(x => NormalizeCatalogOutput(x.SourcePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
        }

        foreach (var model in modelRows)
        {
            var output = NormalizeCatalogOutput((string?)model["output"] ?? (string?)model["source"]);
            if (string.IsNullOrWhiteSpace(output))
                continue;

            var count = 0;
            var objectPath = (string?)model["objectPath"];
            if (!string.IsNullOrWhiteSpace(objectPath) &&
                byObjectPath.TryGetValue(NormalizePackageObjectPath(objectPath), out var objectCount))
            {
                count += objectCount;
            }

            var source = (string?)model["source"];
            if (!string.IsNullOrWhiteSpace(source) &&
                bySourcePath.TryGetValue(NormalizeCatalogOutput(source), out var sourceCount))
            {
                count = Math.Max(count, sourceCount);
            }

            if (count > 0)
                result[output] = count;
        }

        return result;
    }

    private static string NormalizeOptionalPackageObjectPath(string? objectPath)
        => string.IsNullOrWhiteSpace(objectPath) ? "" : NormalizePackageObjectPath(objectPath);

    private static Dictionary<string, int> QuerySourceObjectCounts(
        string dbPath,
        string columnName,
        string[] values,
        Func<string?, string> normalize)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(dbPath) || values.Length == 0 || !File.Exists(dbPath))
            return result;

        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            if (!TableExists(connection, "source_objects") || !TableColumnExists(connection, "source_objects", columnName))
                return result;

            foreach (var batch in values.Chunk(480))
            {
                using var command = connection.CreateCommand();
                var parameterNames = new List<string>();
                for (var i = 0; i < batch.Length; i++)
                {
                    var name = $"$p{i}";
                    parameterNames.Add(name);
                    command.Parameters.AddWithValue(name, batch[i]);
                }

                command.CommandText = $"""
                    SELECT {columnName}, COUNT(*)
                    FROM source_objects
                    WHERE {columnName} IN ({string.Join(", ", parameterNames)})
                    GROUP BY {columnName};
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var key = normalize(GetString(reader, 0));
                    if (string.IsNullOrWhiteSpace(key))
                        continue;

                    result[key] = reader.GetInt32(1);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARN: source object count query failed ({columnName}): {ex.Message}");
        }

        return result;
    }

    private static string[] FindTaskSignals(string path)
    {
        var text = path.Replace('\\', '/').ToLowerInvariant();
        var signals = new List<string>();
        AddTaskSignal(signals, text, "/item/", "item");
        AddTaskSignal(signals, text, "/items/", "items");
        AddTaskSignal(signals, text, "/props/", "props");
        AddTaskSignal(signals, text, "/prop/", "prop");
        AddTaskSignal(signals, text, "/collectable", "collectable");
        AddTaskSignal(signals, text, "/collectible", "collectible");
        AddTaskSignal(signals, text, "/targets/", "targets");
        AddTaskSignal(signals, text, "/target/", "target");
        AddTaskSignal(signals, text, "/quest", "quest");
        AddTaskSignal(signals, text, "/mission", "mission");
        AddTaskSignal(signals, text, "/objective", "objective");
        AddTaskSignal(signals, text, "/interact", "interact");
        AddTaskSignal(signals, text, "/pickup", "pickup");
        AddTaskSignal(signals, text, "/anomaly/", "anomaly");
        return signals.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddTaskSignal(List<string> signals, string text, string token, string signal)
    {
        if (text.Contains(token))
            signals.Add(signal);
    }

    private static JObject WriteLibraryAcceptance(
        string root,
        List<JObject> catalogRows,
        List<ModelValidationEntry> reports,
        List<TextureLinkInfo> textureLinks,
        MaterialTextureSlotLink[] materialTextureSlots,
        ComponentAssetRelationLink[] componentAssetRelations,
        JArray skeletonGroups,
        JObject modelAnimationRelations,
        JObject modelCoverage,
        AnimationValidationSummary animationValidation,
        SourceIndexSnapshot sourceIndex,
        bool writeCompatibilityJson)
    {
        var assets = catalogRows.ToArray();
        var models = assets.Where(x => string.Equals((string?)x["kind"], "Model", StringComparison.OrdinalIgnoreCase)).ToArray();
        var textures = assets.Where(x => string.Equals((string?)x["kind"], "Texture", StringComparison.OrdinalIgnoreCase)).ToArray();
        var animations = assets.Where(x => string.Equals((string?)x["kind"], "Animation", StringComparison.OrdinalIgnoreCase)).ToArray();
        var coverageRows = ((JArray?)modelCoverage["models"] ?? [])
            .OfType<JObject>()
            .Select(ReadModelCoverageRow)
            .ToArray();
        var taskRows = coverageRows
            .Where(x => x.TaskSignals.Length > 0 || string.Equals(x.ResourceKind, "Prop", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var taskSignalGroups = taskRows
            .SelectMany(row => (row.TaskSignals.Length == 0 ? ["prop"] : row.TaskSignals).Select(signal => new { signal, row }))
            .GroupBy(x => x.signal, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => BuildModelCoverageSignalGroup(x.Key, x.Select(y => y.row)))
            .ToArray();
        var relationModels = ((JArray?)modelAnimationRelations["relations"] ?? [])
            .OfType<JObject>()
            .ToArray();
        var relationAnimations = relationModels
            .SelectMany(relation => ((JArray?)relation["animations"] ?? []).OfType<JObject>())
            .ToArray();
        var usableRelationAnimations = relationAnimations
            .Where(IsUsableRelationAnimation)
            .ToArray();
        var animationValidationErrors = animationValidation.Validations
            .Where(x => string.Equals(x.Status, "error", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var actionableAnimationValidationErrors = animationValidation.Validations
            .Where(IsActionableAnimationValidationError)
            .ToArray();
        var fallbackDiagnosticAnimationValidationErrors = animationValidation.Validations
            .Where(IsFallbackDiagnosticAnimationValidationError)
            .ToArray();
        var modelRefs = componentAssetRelations.Where(x => IsModelRelation(x.RelationType)).ToArray();
        var animationRefs = componentAssetRelations.Where(x => IsAnimationRelation(x.RelationType)).ToArray();
        var materialRefs = componentAssetRelations.Where(x => string.Equals(x.RelationType, "Material", StringComparison.OrdinalIgnoreCase)).ToArray();

        var acceptance = JObject.FromObject(new
        {
            generatedAt = DateTime.UtcNow.ToString("O"),
            rule = "可用 UE 素材库验收只汇总已导出资产、UE 源索引关系和后处理验证事实；不按单个游戏私有名称硬猜关系。",
            sourceIndex = new
            {
                sourceIndex.Available,
                sourceIndex.Path,
                sourceIndex.Error,
            },
            models = new
            {
                total = models.Length,
                ok = reports.Count(x => string.Equals(x.Status, "ok", StringComparison.OrdinalIgnoreCase)),
                warning = reports.Count(x => string.Equals(x.Status, "warning", StringComparison.OrdinalIgnoreCase)),
                error = reports.Count(x => string.Equals(x.Status, "error", StringComparison.OrdinalIgnoreCase)),
                staticModels = reports.Count(x => x.SkinCount == 0),
                skinnedModels = reports.Count(x => x.SkinCount > 0),
                withSkeletonPath = models.Count(x => !string.IsNullOrWhiteSpace((string?)x["skeletonPath"])),
                withAnimationCandidates = relationModels.Count(x => ((int?)x["usableAnimationCount"] ?? 0) > 0),
                withRelatedAnimations = relationModels.Count(x => ((JArray?)x["animations"] ?? []).Count > 0),
                byResourceKind = coverageRows
                    .GroupBy(x => x.ResourceKind, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => BuildModelCoverageGroup(x.Key, x))
                    .ToArray(),
            },
            taskAndPropModels = new
            {
                total = taskRows.Length,
                withComponentReferences = taskRows.Count(x => x.ComponentReferenceCount > 0),
                withAnimationCandidates = taskRows.Count(x => x.AnimationCandidateCount > 0),
                quality = BuildTaskModelQuality(taskRows),
                bySignal = taskSignalGroups,
                highReferenceExamples = taskRows
                    .OrderByDescending(x => x.ComponentReferenceCount)
                    .ThenByDescending(x => x.AnimationCandidateCount)
                    .ThenBy(x => x.Output, StringComparer.OrdinalIgnoreCase)
                    .Take(64)
                    .Select(BuildModelCoverageJsonRow)
                    .ToArray(),
            },
            textures = new
            {
                catalogRows = textures.Length,
                dedupeEnabled = textureLinks.Count > 0,
                scanned = textureLinks.Count,
                unique = textureLinks.Select(x => x.Hash + x.Extension).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                hardLinked = textureLinks.Count(x => x.HardLinked),
                linkErrors = textureLinks.Count(x => !string.IsNullOrWhiteSpace(x.LinkError)),
                materialTextureSlots = materialTextureSlots.Length,
                matchedMaterialTextureSlots = materialTextureSlots.Count(x => string.Equals(x.MatchStatus, "matched", StringComparison.OrdinalIgnoreCase)),
            },
            skeletons = new
            {
                groups = skeletonGroups.Count,
                groupsWithAnimations = skeletonGroups.Count(x => ((JArray?)x["animations"] ?? []).Count > 0),
                sourceObjects = skeletonGroups.Sum(x => ((JArray?)x["skeletonSourceObjects"] ?? []).Count),
            },
            animations = new
            {
                catalogRows = animations.Length,
                relatedModels = relationModels.Length,
                relatedAnimations = relationAnimations.Length,
                usableRelatedAnimations = usableRelationAnimations.Length,
                exportFailedRelatedAnimations = relationAnimations.Count(x =>
                    !string.Equals((string?)x["status"], "ok", StringComparison.OrdinalIgnoreCase)),
                validationPairs = animationValidation.Validations.Length,
                ok = animationValidation.Validations.Count(x => string.Equals(x.Status, "ok", StringComparison.OrdinalIgnoreCase)),
                warning = animationValidation.Validations.Count(x => string.Equals(x.Status, "warning", StringComparison.OrdinalIgnoreCase)),
                error = animationValidationErrors.Length,
                actionableError = actionableAnimationValidationErrors.Length,
                fallbackDiagnosticError = fallbackDiagnosticAnimationValidationErrors.Length,
                containerAnimations = animationValidation.Validations.Count(x => x.IsContainerAnimation),
                byCategory = animationValidation.Validations
                    .GroupBy(x => x.ValidationCategory, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new
                    {
                        category = x.Key,
                        total = x.Count(),
                        ok = x.Count(y => string.Equals(y.Status, "ok", StringComparison.OrdinalIgnoreCase)),
                        warning = x.Count(y => string.Equals(y.Status, "warning", StringComparison.OrdinalIgnoreCase)),
                        error = x.Count(y => string.Equals(y.Status, "error", StringComparison.OrdinalIgnoreCase)),
                    })
                    .ToArray(),
            },
            relations = new
            {
                componentAssetRelations = componentAssetRelations.Length,
                modelReferences = BuildRelationAcceptance(modelRefs),
                animationReferences = BuildRelationAcceptance(animationRefs),
                materialReferences = BuildRelationAcceptance(materialRefs),
            },
            notes = new[]
            {
                "GLB 是当前模型/骨骼/材质预览主格式；UE .ueanim 可通过 --preview-ue-animation 与模型 GLB 离线合并成可播放动画预览，浏览器默认报告为预览缓存目录的 preview_validation.db。",
                "任务/道具模型优先看 taskAndPropModels.quality、bySignal 和 highReferenceExamples；有组件引用表示来自 UE 蓝图/组件显式关系，无组件引用但源索引确认到对象时记录为 sourceIndexedPathEvidence。",
                "动画 explicitUsage 来自 UE 组件/蓝图/AnimClass 等显式关系；skeletonCompatibility 来自同 USkeleton 且骨骼覆盖验证通过的可复用候选。验证 error 不进入可用动画。",
                "贴图去重通过 Textures/_Shared、library_index.db.texture_links 和 library_index.db.library_reports 的 texture_dedupe 摘要验证，GLB 内嵌贴图不会被强行拆出。",
            },
        });

        var path = Path.Combine(root, "library_acceptance.json");
        if (writeCompatibilityJson)
            File.WriteAllText(path, acceptance.ToString(Formatting.Indented), Encoding.UTF8);
        else
            DeleteIfExists(path);

        return acceptance;
    }

    private static object BuildRelationAcceptance(ComponentAssetRelationLink[] links)
        => new
        {
            total = links.Length,
            matchedOrCovered = links.Count(x =>
                string.Equals(x.MatchStatus, "matched", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.MatchStatus, "skeletonCoveredByModels", StringComparison.OrdinalIgnoreCase)),
            byStatus = links
                .GroupBy(x => string.IsNullOrWhiteSpace(x.MatchStatus) ? "unknown" : x.MatchStatus, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new { status = x.Key, count = x.Count() })
                .ToArray(),
        };

    private static object BuildTaskModelQuality(ModelCoverageRow[] taskRows)
    {
        var explicitRelationRows = taskRows
            .Where(x => x.ComponentReferenceCount > 0)
            .ToArray();
        var pathOnlyRows = taskRows
            .Where(x => x.ComponentReferenceCount == 0)
            .ToArray();
        var sourceIndexedRows = taskRows
            .Where(x => x.SourceIndexObjectCount > 0)
            .ToArray();
        var sourceIndexedWithoutComponentRows = taskRows
            .Where(x => x.ComponentReferenceCount == 0 && x.SourceIndexObjectCount > 0)
            .ToArray();
        var purePathOnlyRows = taskRows
            .Where(x => x.ComponentReferenceCount == 0 && x.SourceIndexObjectCount == 0)
            .ToArray();
        var warningRows = taskRows
            .Where(x => string.Equals(x.ValidationStatus, "warning", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var errorRows = taskRows
            .Where(x => string.Equals(x.ValidationStatus, "error", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var missingMaterialRows = taskRows
            .Where(x => x.MaterialCount == 0)
            .ToArray();
        var noTextureRows = taskRows
            .Where(x => x.TextureCount == 0)
            .ToArray();
        var animatedRows = taskRows
            .Where(x => x.AnimationCandidateCount > 0)
            .ToArray();
        var qualityIssueRows = taskRows
            .Where(x => BuildTaskQualityIssueReasons(x).Length > 0)
            .ToArray();

        return new
        {
            total = taskRows.Length,
            ready = new
            {
                usableModelQuality = taskRows.Length - qualityIssueRows.Length,
                withExplicitComponentReferences = explicitRelationRows.Length,
                withSourceIndexObjects = sourceIndexedRows.Length,
                sourceIndexedWithoutComponentReferences = sourceIndexedWithoutComponentRows.Length,
                sourceIndexedPathEvidence = sourceIndexedWithoutComponentRows.Length,
                pathOnlyUsableModels = purePathOnlyRows.Length - purePathOnlyRows.Count(x => BuildTaskQualityIssueReasons(x).Length > 0),
                purePathOnlyUsableModels = purePathOnlyRows.Length - purePathOnlyRows.Count(x => BuildTaskQualityIssueReasons(x).Length > 0),
                withAnimationCandidates = animatedRows.Length,
                okValidation = taskRows.Count(x => string.Equals(x.ValidationStatus, "ok", StringComparison.OrdinalIgnoreCase)),
            },
            needsReview = new
            {
                warnings = warningRows.Length,
                errors = errorRows.Length,
                missingMaterials = missingMaterialRows.Length,
                noExternalTextureSlots = noTextureRows.Length,
            },
            relationNeedsReview = new
            {
                pathOnlyWithoutSourceIndexObject = purePathOnlyRows.Length,
                sourceIndexedPathEvidence = sourceIndexedWithoutComponentRows.Length,
                purePathOnlyWithoutSourceIndexObject = purePathOnlyRows.Length,
            },
            bySourceType = taskRows
                .GroupBy(x => string.IsNullOrWhiteSpace(x.SourceType) ? "unknown" : x.SourceType, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    sourceType = x.Key,
                    total = x.Count(),
                    withComponentReferences = x.Count(y => y.ComponentReferenceCount > 0),
                    withSourceIndexObjects = x.Count(y => y.SourceIndexObjectCount > 0),
                    withAnimationCandidates = x.Count(y => y.AnimationCandidateCount > 0),
                })
                .ToArray(),
            reviewExamples = new
            {
                pathOnly = pathOnlyRows
                    .OrderByDescending(x => x.AnimationCandidateCount)
                    .ThenBy(x => x.Output, StringComparer.OrdinalIgnoreCase)
                    .Take(32)
                    .Select(BuildModelCoverageJsonRow)
                    .ToArray(),
                warnings = warningRows
                    .OrderByDescending(x => x.ComponentReferenceCount)
                    .ThenBy(x => x.Output, StringComparer.OrdinalIgnoreCase)
                    .Take(32)
                    .Select(BuildModelCoverageJsonRow)
                    .ToArray(),
                missingMaterials = missingMaterialRows
                    .OrderByDescending(x => x.ComponentReferenceCount)
                    .ThenBy(x => x.Output, StringComparer.OrdinalIgnoreCase)
                    .Take(32)
                    .Select(BuildModelCoverageJsonRow)
                    .ToArray(),
                animated = animatedRows
                    .OrderByDescending(x => x.AnimationCandidateCount)
                    .ThenByDescending(x => x.ComponentReferenceCount)
                    .ThenBy(x => x.Output, StringComparer.OrdinalIgnoreCase)
                    .Take(32)
                    .Select(BuildModelCoverageJsonRow)
                    .ToArray(),
            },
        };
    }

    private static void WriteTaskModelQualityReport(string root, JObject modelCoverage, bool writeCompatibilityJson)
    {
        var rows = ((JArray?)modelCoverage["models"] ?? [])
            .OfType<JObject>()
            .Select(ReadModelCoverageRow)
            .ToArray();
        WriteTaskModelQualityReport(root, rows, writeCompatibilityJson);
    }

    private static void WriteTaskModelQualityReport(string root, IEnumerable<ModelCoverageRow> modelCoverageRows, bool writeCompatibilityJson)
    {
        var rows = modelCoverageRows
            .Where(IsTaskOrPropCoverageRow)
            .OrderByDescending(x => BuildTaskQualityIssueReasons(x).Length)
            .ThenByDescending(x => x.AnimationCandidateCount)
            .ThenByDescending(x => x.ComponentReferenceCount)
            .ThenBy(x => x.Output, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var readyRows = rows.Where(x => BuildTaskQualityIssueReasons(x).Length == 0).ToArray();
        var reviewRows = rows.Where(x => BuildTaskQualityIssueReasons(x).Length > 0).ToArray();
        var relationReviewRows = rows.Where(x => BuildTaskRelationReviewReasons(x).Length > 0).ToArray();
        var qualityIssueRows = rows.Where(x => BuildTaskQualityIssueReasons(x).Length > 0).ToArray();
        var byReason = reviewRows
            .SelectMany(row => BuildTaskQualityIssueReasons(row).Select(reason => new { reason, row }))
            .GroupBy(x => x.reason, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new
            {
                reason = x.Key,
                count = x.Count(),
                examples = x.Select(y => BuildTaskQualityRow(y.row)).Take(32).ToArray(),
            })
            .ToArray();

        var json = JObject.FromObject(new
        {
            generatedAt = DateTime.UtcNow.ToString("O"),
            rule = "任务/道具质量报告只基于 UE 通用路径语义、组件引用、源索引对象、模型验证、材质和动画候选事实；sourceIndexedPathEvidence 表示源索引确认了资产对象，只是暂未解析到 UE 组件显式引用，不代表模型不可用。",
            totals = new
            {
                taskOrPropModels = rows.Length,
                ready = readyRows.Length,
                needsReview = reviewRows.Length,
                relationNeedsReview = relationReviewRows.Length,
                usableModelQuality = rows.Length - qualityIssueRows.Length,
                qualityIssueModels = qualityIssueRows.Length,
                withComponentReferences = rows.Count(x => x.ComponentReferenceCount > 0),
                withSourceIndexObjects = rows.Count(x => x.SourceIndexObjectCount > 0),
                sourceIndexedWithoutComponentReferences = rows.Count(x => x.ComponentReferenceCount == 0 && x.SourceIndexObjectCount > 0),
                sourceIndexedPathEvidence = rows.Count(x => x.ComponentReferenceCount == 0 && x.SourceIndexObjectCount > 0),
                pathOnlyRelation = rows.Count(x => x.ComponentReferenceCount == 0 && x.SourceIndexObjectCount == 0),
                purePathOnlyRelation = rows.Count(x => x.ComponentReferenceCount == 0 && x.SourceIndexObjectCount == 0),
                withAnimationCandidates = rows.Count(x => x.AnimationCandidateCount > 0),
                validationWarnings = rows.Count(x => string.Equals(x.ValidationStatus, "warning", StringComparison.OrdinalIgnoreCase)),
                validationErrors = rows.Count(x => string.Equals(x.ValidationStatus, "error", StringComparison.OrdinalIgnoreCase)),
                missingMaterials = rows.Count(x => x.MaterialCount == 0),
                noExternalTextureSlots = rows.Count(x => x.TextureCount == 0),
            },
            bySignal = rows
                .SelectMany(row => (row.TaskSignals.Length == 0 ? ["prop"] : row.TaskSignals).Select(signal => new { signal, row }))
                .GroupBy(x => x.signal, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    signal = x.Key,
                    total = x.Count(),
                    ready = x.Count(y => BuildTaskQualityIssueReasons(y.row).Length == 0),
                    needsReview = x.Count(y => BuildTaskQualityIssueReasons(y.row).Length > 0),
                    relationNeedsReview = x.Count(y => BuildTaskRelationReviewReasons(y.row).Length > 0),
                    withComponentReferences = x.Count(y => y.row.ComponentReferenceCount > 0),
                    withSourceIndexObjects = x.Count(y => y.row.SourceIndexObjectCount > 0),
                    sourceIndexedPathEvidence = x.Count(y => y.row.ComponentReferenceCount == 0 && y.row.SourceIndexObjectCount > 0),
                    purePathOnlyRelation = x.Count(y => y.row.ComponentReferenceCount == 0 && y.row.SourceIndexObjectCount == 0),
                    withAnimationCandidates = x.Count(y => y.row.AnimationCandidateCount > 0),
                })
                .ToArray(),
            bySourceType = rows
                .GroupBy(x => string.IsNullOrWhiteSpace(x.SourceType) ? "unknown" : x.SourceType, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Count())
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new
                {
                    sourceType = x.Key,
                    total = x.Count(),
                    ready = x.Count(y => BuildTaskQualityIssueReasons(y).Length == 0),
                    needsReview = x.Count(y => BuildTaskQualityIssueReasons(y).Length > 0),
                    relationNeedsReview = x.Count(y => BuildTaskRelationReviewReasons(y).Length > 0),
                    withComponentReferences = x.Count(y => y.ComponentReferenceCount > 0),
                    withSourceIndexObjects = x.Count(y => y.SourceIndexObjectCount > 0),
                    sourceIndexedPathEvidence = x.Count(y => y.ComponentReferenceCount == 0 && y.SourceIndexObjectCount > 0),
                    purePathOnlyRelation = x.Count(y => y.ComponentReferenceCount == 0 && y.SourceIndexObjectCount == 0),
                    withAnimationCandidates = x.Count(y => y.AnimationCandidateCount > 0),
                })
                .ToArray(),
            byReviewReason = byReason,
            reviewModels = reviewRows.Select(BuildTaskQualityRow).ToArray(),
            relationReviewModels = relationReviewRows.Select(BuildTaskQualityRow).ToArray(),
            qualityIssueModels = qualityIssueRows.Select(BuildTaskQualityRow).ToArray(),
            readyExamples = readyRows
                .OrderByDescending(x => x.ComponentReferenceCount)
                .ThenByDescending(x => x.AnimationCandidateCount)
                .ThenBy(x => x.Output, StringComparer.OrdinalIgnoreCase)
                .Take(128)
                .Select(BuildTaskQualityRow)
                .ToArray(),
        });

        var jsonPath = Path.Combine(root, "task_model_quality.json");
        if (writeCompatibilityJson)
            File.WriteAllText(jsonPath, json.ToString(Formatting.Indented), Encoding.UTF8);
        else
            DeleteIfExists(jsonPath);

        WriteTaskModelQualityReadme(root, rows, readyRows, reviewRows, relationReviewRows, writeCompatibilityJson);
    }

    private static bool IsTaskOrPropCoverageRow(ModelCoverageRow row)
        => row.TaskSignals.Length > 0 || string.Equals(row.ResourceKind, "Prop", StringComparison.OrdinalIgnoreCase);

    private static string[] BuildTaskReviewReasons(ModelCoverageRow row)
    {
        var reasons = new List<string>();
        reasons.AddRange(BuildTaskQualityIssueReasons(row));
        return reasons.ToArray();
    }

    private static string[] BuildTaskRelationReviewReasons(ModelCoverageRow row)
    {
        var reasons = new List<string>();
        if (row.ComponentReferenceCount == 0 && row.SourceIndexObjectCount == 0)
            reasons.Add("pathOnlyRelation");
        return reasons.ToArray();
    }

    private static string[] BuildTaskQualityIssueReasons(ModelCoverageRow row)
    {
        var reasons = new List<string>();
        if (string.Equals(row.ValidationStatus, "warning", StringComparison.OrdinalIgnoreCase))
            reasons.Add("modelValidationWarning");
        if (string.Equals(row.ValidationStatus, "error", StringComparison.OrdinalIgnoreCase))
            reasons.Add("modelValidationError");
        if (row.MaterialCount == 0)
            reasons.Add("missingMaterials");
        if (row.TextureCount == 0)
            reasons.Add("noExternalTextureSlots");
        return reasons.ToArray();
    }

    private static object BuildTaskQualityRow(ModelCoverageRow row)
        => new
        {
            row.Name,
            row.Output,
            row.Source,
            row.ObjectPath,
            row.ResourceKind,
            row.SourceType,
            row.ValidationStatus,
            row.IsStatic,
            row.HasSkin,
            row.HasSkeletonPath,
            row.MaterialCount,
            row.TextureCount,
            row.ComponentReferenceCount,
            row.SourceIndexObjectCount,
            row.AnimationCandidateCount,
            row.TaskSignals,
            NeedsReview = BuildTaskQualityIssueReasons(row).Length > 0,
            ReviewReasons = BuildTaskQualityIssueReasons(row),
            RelationNeedsReview = BuildTaskRelationReviewReasons(row).Length > 0,
            RelationReviewReasons = BuildTaskRelationReviewReasons(row),
        };

    private static void WriteTaskModelQualityReadme(
        string root,
        ModelCoverageRow[] rows,
        ModelCoverageRow[] readyRows,
        ModelCoverageRow[] reviewRows,
        ModelCoverageRow[] relationReviewRows,
        bool writeCompatibilityJson)
    {
        var warningCount = rows.Count(x => string.Equals(x.ValidationStatus, "warning", StringComparison.OrdinalIgnoreCase));
        var errorCount = rows.Count(x => string.Equals(x.ValidationStatus, "error", StringComparison.OrdinalIgnoreCase));
        var qualityIssueCount = rows.Count(x => BuildTaskQualityIssueReasons(x).Length > 0);
        var lines = new List<string>
        {
            "# UE 任务/道具模型质量报告",
            "",
            "本报告用于快速确认任务模型、交互道具和 Prop 是否已经作为可浏览素材进入库。`需要复查` 只统计模型质量问题；`关系待补` 只表示既没有 UE 组件显式引用、也没有源索引对象确认的纯路径命中。",
            "",
            $"总数: {rows.Length}",
            $"可直接使用: {readyRows.Length}",
            $"需要复查: {reviewRows.Length}",
            $"关系待补: {relationReviewRows.Length}",
            $"模型质量问题: {qualityIssueCount}",
            $"有 UE 组件引用: {rows.Count(x => x.ComponentReferenceCount > 0)}",
            $"源索引确认资产: {rows.Count(x => x.SourceIndexObjectCount > 0)}",
            $"源索引确认但暂无组件引用: {rows.Count(x => x.ComponentReferenceCount == 0 && x.SourceIndexObjectCount > 0)}",
            $"仅路径/分类命中: {rows.Count(x => x.ComponentReferenceCount == 0 && x.SourceIndexObjectCount == 0)}",
            $"有动画候选: {rows.Count(x => x.AnimationCandidateCount > 0)}",
            $"模型验证 warning/error: {warningCount}/{errorCount}",
            $"缺材质/无外部贴图槽: {rows.Count(x => x.MaterialCount == 0)}/{rows.Count(x => x.TextureCount == 0)}",
            "",
            writeCompatibilityJson
                ? "详细机器报告: `task_model_quality.json`"
                : "详细机器索引: `library_index.db.model_coverage`（筛选 `is_task_or_prop`、`needs_review`、`relation_needs_review`），复查原因见 `model_coverage_review_reasons`。",
            "",
            "## 复查原因",
            "",
        };

        var reasonGroups = reviewRows
            .SelectMany(row => BuildTaskQualityIssueReasons(row).Select(reason => new { reason, row }))
            .GroupBy(x => x.reason, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var group in reasonGroups)
            lines.Add($"- {group.Key}: {group.Count()}");

        lines.Add("");
        lines.Add("## 关系证据");
        lines.Add("");
        lines.Add($"- sourceIndexedPathEvidence: {rows.Count(x => x.ComponentReferenceCount == 0 && x.SourceIndexObjectCount > 0)}");
        lines.Add($"- pathOnlyRelation: {relationReviewRows.Count(x => x.SourceIndexObjectCount == 0)}");

        lines.Add("");
        lines.Add("## 复查样例");
        lines.Add("");
        foreach (var row in reviewRows.Take(40))
            lines.Add($"- {row.Name} [{string.Join(", ", BuildTaskQualityIssueReasons(row))}] `{row.Output}`");

        lines.Add("");
        lines.Add("## 关系待补样例");
        lines.Add("");
        foreach (var row in relationReviewRows.Take(40))
            lines.Add($"- {row.Name} [{string.Join(", ", BuildTaskRelationReviewReasons(row))}] `{row.Output}`");

        File.WriteAllLines(Path.Combine(root, "TASK_MODEL_QUALITY.md"), lines, Encoding.UTF8);
    }

    private static ModelCoverageRow ReadModelCoverageRow(JObject row)
    {
        var taskSignals = ((JArray?)row["TaskSignals"] ?? [])
            .Select(x => (string?)x)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToArray();
        return new ModelCoverageRow
        {
            Name = (string?)row["Name"] ?? "",
            Output = (string?)row["Output"] ?? "",
            Source = (string?)row["Source"] ?? "",
            ObjectPath = (string?)row["ObjectPath"],
            ResourceKind = (string?)row["ResourceKind"] ?? "Unknown",
            SourceType = (string?)row["SourceType"] ?? "",
            ValidationStatus = (string?)row["ValidationStatus"] ?? "unknown",
            IsStatic = (bool?)row["IsStatic"] ?? false,
            HasSkin = (bool?)row["HasSkin"] ?? false,
            HasSkeletonPath = (bool?)row["HasSkeletonPath"] ?? false,
            MaterialCount = (int?)row["MaterialCount"] ?? 0,
            TextureCount = (int?)row["TextureCount"] ?? 0,
            ComponentReferenceCount = (int?)row["ComponentReferenceCount"] ?? 0,
            SourceIndexObjectCount = (int?)row["SourceIndexObjectCount"] ?? 0,
            AnimationCandidateCount = (int?)row["AnimationCandidateCount"] ?? 0,
            TaskSignals = taskSignals,
        };
    }

    private static void WriteLibraryIndexDb(
        string root,
        List<JObject> catalogRows,
        List<ModelValidationEntry> reports,
        IEnumerable<MaterialInfo> materials,
        List<TextureLinkInfo> textureLinks,
        MaterialTextureSlotLink[] materialTextureSlots,
        SharedGltfTextureLink[] sharedGltfTextureLinks,
        ComponentAssetRelationLink[] componentAssetRelations,
        SourcePackageObjectMap[] packageObjectMaps,
        JArray skeletonGroups,
        JObject modelAnimationRelations,
        JObject modelCoverage,
        AnimationValidationSummary animationValidation,
        SourceIndexSnapshot sourceIndex,
        JObject libraryHealth,
        JObject libraryAcceptance,
        JObject? textureDedupeSummary)
    {
        var dbPath = Path.Combine(root, "library_index.db");
        DeleteSqliteOutput(dbPath);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "PRAGMA synchronous = NORMAL;");
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            CREATE TABLE assets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                kind TEXT NOT NULL,
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
                raw_json TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE texture_links (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source TEXT NOT NULL,
                shared TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                extension TEXT NOT NULL,
                hard_linked INTEGER NOT NULL,
                link_error TEXT
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE material_sidecars (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                relative_path TEXT NOT NULL,
                size_bytes INTEGER NOT NULL,
                last_write_utc_ticks INTEGER NOT NULL,
                texture_slot_count INTEGER NOT NULL,
                color_count INTEGER NOT NULL,
                scalar_count INTEGER NOT NULL,
                switch_count INTEGER NOT NULL,
                blend_mode TEXT,
                shading_model TEXT,
                raw_json TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE export_manifest (
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
        Execute(connection, transaction, """
            CREATE TABLE animation_bindings (
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
                notify_count INTEGER NOT NULL,
                curve_count INTEGER NOT NULL,
                segment_count INTEGER NOT NULL,
                section_count INTEGER NOT NULL,
                requires_acl INTEGER NOT NULL,
                compression TEXT,
                raw_json TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE auto_referenced_exports (
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
        Execute(connection, transaction, """
            CREATE TABLE model_validation (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL,
                name TEXT,
                resource_kind TEXT,
                status TEXT NOT NULL,
                mesh_count INTEGER NOT NULL,
                material_count INTEGER NOT NULL,
                texture_count INTEGER NOT NULL,
                skin_count INTEGER NOT NULL,
                bone_count INTEGER NOT NULL,
                animation_count INTEGER NOT NULL,
                skeleton_hash TEXT,
                bbox_min_x REAL,
                bbox_min_y REAL,
                bbox_min_z REAL,
                bbox_max_x REAL,
                bbox_max_y REAL,
                bbox_max_z REAL,
                bbox_size_x REAL,
                bbox_size_y REAL,
                bbox_size_z REAL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE model_validation_notes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                model_validation_id INTEGER NOT NULL,
                note_index INTEGER NOT NULL,
                note TEXT NOT NULL,
                FOREIGN KEY (model_validation_id) REFERENCES model_validation(id)
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE model_coverage (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT,
                output TEXT NOT NULL,
                source TEXT,
                object_path TEXT,
                resource_kind TEXT,
                source_type TEXT,
                validation_status TEXT,
                is_static INTEGER NOT NULL,
                has_skin INTEGER NOT NULL,
                has_skeleton_path INTEGER NOT NULL,
                material_count INTEGER NOT NULL,
                texture_count INTEGER NOT NULL,
                component_reference_count INTEGER NOT NULL,
                source_index_object_count INTEGER NOT NULL,
                animation_candidate_count INTEGER NOT NULL,
                is_task_or_prop INTEGER NOT NULL,
                is_path_only_task INTEGER NOT NULL,
                missing_materials INTEGER NOT NULL,
                no_external_texture_slots INTEGER NOT NULL,
                needs_review INTEGER NOT NULL,
                relation_needs_review INTEGER NOT NULL,
                raw_json TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE model_coverage_review_reasons (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                model_coverage_id INTEGER NOT NULL,
                reason_kind TEXT NOT NULL,
                reason_index INTEGER NOT NULL,
                reason TEXT NOT NULL,
                FOREIGN KEY (model_coverage_id) REFERENCES model_coverage(id)
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE model_coverage_task_signals (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                model_coverage_id INTEGER NOT NULL,
                signal_index INTEGER NOT NULL,
                signal TEXT NOT NULL,
                FOREIGN KEY (model_coverage_id) REFERENCES model_coverage(id)
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE material_texture_slots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                material_name TEXT NOT NULL,
                material_path TEXT,
                material_object_path TEXT,
                slot_name TEXT NOT NULL,
                texture_name TEXT,
                texture_object_path TEXT,
                texture_path TEXT,
                texture_class_name TEXT,
                texture_class_path TEXT,
                missing_category TEXT,
                exported_texture TEXT,
                shared_texture TEXT,
                sha256 TEXT,
                hard_linked INTEGER,
                match_status TEXT NOT NULL,
                match_reason TEXT,
                relation_source TEXT
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE shared_gltf_texture_links (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                model TEXT NOT NULL,
                material_name TEXT,
                semantic TEXT,
                slot_name TEXT,
                texture_name TEXT,
                image_index INTEGER,
                shared_texture TEXT,
                sha256 TEXT,
                uri TEXT,
                removed_buffer_view INTEGER NOT NULL,
                status TEXT NOT NULL,
                reason TEXT
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE component_asset_relations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                owner_object_path TEXT,
                owner_type TEXT,
                component_object_path TEXT,
                component_type TEXT,
                component_name TEXT,
                component_variable_name TEXT,
                relation_source TEXT NOT NULL,
                relation_type TEXT NOT NULL,
                target_path TEXT,
                target_name TEXT,
                target_asset_name TEXT,
                target_asset_kind TEXT,
                target_asset_output TEXT,
                match_status TEXT NOT NULL,
                match_reason TEXT,
                socket_name TEXT,
                parent_component_path TEXT,
                transform_json TEXT,
                source_path TEXT
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE component_groups (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                owner_object_path TEXT NOT NULL,
                owner_type TEXT,
                source_path TEXT,
                relation_count INTEGER NOT NULL,
                component_count INTEGER NOT NULL,
                model_reference_count INTEGER NOT NULL,
                exported_model_reference_count INTEGER NOT NULL,
                missing_model_reference_count INTEGER NOT NULL,
                animation_reference_count INTEGER NOT NULL,
                exported_animation_reference_count INTEGER NOT NULL,
                missing_animation_reference_count INTEGER NOT NULL,
                material_reference_count INTEGER NOT NULL,
                exported_material_reference_count INTEGER NOT NULL,
                missing_material_reference_count INTEGER NOT NULL,
                missing_reference_count INTEGER NOT NULL,
                raw_json TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE package_object_maps (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT,
                package_name TEXT,
                map_type TEXT NOT NULL,
                map_index INTEGER NOT NULL,
                object_name TEXT,
                object_path TEXT,
                class_name TEXT,
                class_path TEXT,
                outer_path TEXT,
                super_path TEXT,
                template_path TEXT,
                target_package TEXT,
                is_asset INTEGER,
                is_optional INTEGER,
                object_flags TEXT,
                serial_size INTEGER,
                public_export_hash TEXT,
                raw_json TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE asset_registry_dependencies (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                registry_path TEXT,
                source_package TEXT,
                source_asset_name TEXT,
                source_asset_class TEXT,
                source_identifier TEXT,
                dependency_package TEXT,
                dependency_asset_name TEXT,
                dependency_asset_class TEXT,
                dependency_identifier TEXT,
                dependency_category TEXT NOT NULL,
                dependency_kind TEXT NOT NULL,
                dependency_flags INTEGER,
                dependency_flags_text TEXT,
                is_hard_package INTEGER NOT NULL,
                is_soft_package INTEGER NOT NULL,
                is_name_dependency INTEGER NOT NULL,
                is_manage_dependency INTEGER NOT NULL,
                relation_source TEXT,
                raw_json TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE anim_blueprint_animation_refs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT,
                anim_blueprint_object_path TEXT NOT NULL,
                generated_class_object_path TEXT,
                referenced_animation_path TEXT NOT NULL,
                referenced_animation_name TEXT,
                referenced_animation_type TEXT,
                property_path TEXT,
                node_type TEXT,
                skeleton_path TEXT,
                relation_source TEXT,
                confidence_hint TEXT,
                raw_json TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE model_animation_relations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                model TEXT NOT NULL,
                model_name TEXT,
                model_source TEXT,
                skeleton_path TEXT,
                skeleton_name TEXT,
                confidence TEXT NOT NULL,
                animation_count INTEGER NOT NULL,
                raw_json TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE skeleton_groups (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                skeleton_id TEXT NOT NULL,
                skeleton_path TEXT,
                skeleton_name TEXT,
                model_count INTEGER NOT NULL,
                animation_count INTEGER NOT NULL,
                bone_count INTEGER NOT NULL,
                source_object_count INTEGER NOT NULL,
                raw_json TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE relation_animations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                relation_id INTEGER NOT NULL,
                name TEXT,
                source TEXT,
                output TEXT,
                status TEXT,
                relation_source TEXT,
                usage_evidence TEXT,
                is_explicit_usage INTEGER NOT NULL,
                is_skeleton_compatible INTEGER NOT NULL,
                validation_status TEXT,
                validation_category TEXT,
                validation_reason TEXT,
                duration REAL,
                frame_count INTEGER,
                track_count INTEGER,
                track_coverage REAL,
                hierarchy_compatible INTEGER NOT NULL,
                is_container_animation INTEGER NOT NULL,
                is_usable_candidate INTEGER NOT NULL,
                confidence_tier TEXT,
                relationship_kind TEXT,
                recommended_use TEXT,
                evidence_summary TEXT NOT NULL,
                is_deterministic_usage INTEGER NOT NULL,
                is_compatibility_candidate INTEGER NOT NULL,
                segment_count INTEGER NOT NULL,
                referenced_animation_count INTEGER NOT NULL,
                exported_referenced_animation_count INTEGER NOT NULL,
                missing_referenced_animation_count INTEGER NOT NULL,
                section_count INTEGER NOT NULL,
                raw_json TEXT NOT NULL,
                FOREIGN KEY (relation_id) REFERENCES model_animation_relations(id)
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE relation_animation_evidence (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                relation_animation_id INTEGER NOT NULL,
                step_index INTEGER NOT NULL,
                evidence_step TEXT NOT NULL,
                FOREIGN KEY (relation_animation_id) REFERENCES relation_animations(id)
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE animation_validation (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                model TEXT NOT NULL,
                animation TEXT NOT NULL,
                skeleton_path TEXT,
                status TEXT NOT NULL,
                candidate_reason TEXT,
                validation_category TEXT,
                reason TEXT,
                model_bone_count INTEGER NOT NULL,
                animation_track_count INTEGER NOT NULL,
                matched_track_bones INTEGER NOT NULL,
                track_coverage REAL NOT NULL,
                hierarchy_compatible INTEGER NOT NULL,
                is_container_animation INTEGER NOT NULL,
                raw_json TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE animation_validation_missing_track_bones (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                animation_validation_id INTEGER NOT NULL,
                bone_index INTEGER NOT NULL,
                bone_name TEXT NOT NULL,
                FOREIGN KEY (animation_validation_id) REFERENCES animation_validation(id)
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE animation_validation_hierarchy_mismatches (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                animation_validation_id INTEGER NOT NULL,
                mismatch_index INTEGER NOT NULL,
                mismatch TEXT NOT NULL,
                FOREIGN KEY (animation_validation_id) REFERENCES animation_validation(id)
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE library_reports (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                status TEXT,
                generated_at TEXT,
                raw_json TEXT NOT NULL
            );
            """);
        foreach (var row in catalogRows)
            InsertAsset(connection, transaction, row);

        foreach (var material in materials.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
            InsertMaterialSidecar(connection, transaction, material);

        if (!InsertTextureLinksFromWorkDb(connection, transaction, root))
        {
            foreach (var link in textureLinks)
                InsertTextureLink(connection, transaction, link);
        }

        InsertExportManifestRows(connection, transaction, root);
        InsertAnimationBindingRows(connection, transaction, root);
        InsertAutoReferencedExportRows(connection, transaction, root);

        foreach (var report in reports)
            InsertModelValidation(connection, transaction, report);

        InsertModelCoverage(connection, transaction, modelCoverage);

        if (!InsertMaterialTextureSlotsFromWorkDb(connection, transaction, root))
        {
            foreach (var slot in materialTextureSlots)
                InsertMaterialTextureSlot(connection, transaction, slot);
        }

        if (!InsertSharedGltfTextureLinksFromWorkDb(connection, transaction, root))
        {
            foreach (var link in sharedGltfTextureLinks)
                InsertSharedGltfTextureLink(connection, transaction, link);
        }

        if (!InsertComponentAssetRelationRowsFromWorkDb(connection, transaction, root) &&
            !InsertComponentAssetRelationRowsFromJsonLines(connection, transaction, root))
        {
            foreach (var link in componentAssetRelations)
                InsertComponentAssetRelation(connection, transaction, link);
        }

        InsertComponentGroups(connection, transaction, componentAssetRelations);

        foreach (var row in packageObjectMaps)
            InsertPackageObjectMap(connection, transaction, row);

        foreach (var row in sourceIndex.AssetRegistryDependencies)
            InsertAssetRegistryDependency(connection, transaction, row);

        foreach (var row in sourceIndex.AnimBlueprintAnimationRefs)
            InsertAnimBlueprintAnimationRef(connection, transaction, row);

        InsertSkeletonGroups(connection, transaction, skeletonGroups);
        InsertModelAnimationRelations(connection, transaction, modelAnimationRelations);
        InsertAnimationValidation(connection, transaction, animationValidation);
        InsertLibraryReport(connection, transaction, "library_health", libraryHealth);
        InsertLibraryReport(connection, transaction, "library_acceptance", libraryAcceptance);
        if (textureDedupeSummary != null)
            InsertLibraryReport(connection, transaction, "texture_dedupe", textureDedupeSummary);

        CreateLibraryIndexDbIndexes(connection, transaction);
        transaction.Commit();
        FinalizeSqliteOutput(connection);
    }

    private static void CreateLibraryIndexDbIndexes(SqliteConnection connection, SqliteTransaction transaction)
    {
        // 大库里 package_object_maps / material_texture_slots 往往是几十万到百万级。
        // 先写数据、最后建索引，能避免每插入一行都维护 BTree，NTE 这类库会快很多。
        Execute(connection, transaction, "CREATE INDEX idx_assets_kind ON assets(kind, resource_kind);");
        Execute(connection, transaction, "CREATE INDEX idx_assets_skeleton ON assets(skeleton_path);");
        Execute(connection, transaction, "CREATE INDEX idx_texture_hash ON texture_links(sha256);");
        Execute(connection, transaction, "CREATE INDEX idx_material_sidecars_name ON material_sidecars(name);");
        Execute(connection, transaction, "CREATE INDEX idx_material_sidecars_path ON material_sidecars(relative_path);");
        Execute(connection, transaction, "CREATE INDEX idx_export_manifest_source ON export_manifest(source, kind);");
        Execute(connection, transaction, "CREATE INDEX idx_export_manifest_object ON export_manifest(object_path);");
        Execute(connection, transaction, "CREATE INDEX idx_export_manifest_output ON export_manifest(output);");
        Execute(connection, transaction, "CREATE INDEX idx_animation_bindings_object ON animation_bindings(object_path, status);");
        Execute(connection, transaction, "CREATE INDEX idx_animation_bindings_skeleton ON animation_bindings(skeleton_path);");
        Execute(connection, transaction, "CREATE INDEX idx_auto_referenced_exports_target ON auto_referenced_exports(target_path, relation_type);");
        Execute(connection, transaction, "CREATE INDEX idx_auto_referenced_exports_status ON auto_referenced_exports(stage, status, reason);");
        Execute(connection, transaction, "CREATE INDEX idx_model_validation_notes ON model_validation_notes(note, model_validation_id);");
        Execute(connection, transaction, "CREATE INDEX idx_model_coverage_kind ON model_coverage(resource_kind, validation_status);");
        Execute(connection, transaction, "CREATE INDEX idx_model_coverage_task ON model_coverage(is_task_or_prop, needs_review, component_reference_count, animation_candidate_count);");
        Execute(connection, transaction, "CREATE INDEX idx_model_coverage_source_index ON model_coverage(is_task_or_prop, source_index_object_count, component_reference_count);");
        Execute(connection, transaction, "CREATE INDEX idx_model_coverage_quality ON model_coverage(is_path_only_task, missing_materials, no_external_texture_slots, validation_status);");
        Execute(connection, transaction, "CREATE INDEX idx_model_coverage_review_reasons ON model_coverage_review_reasons(reason_kind, reason, model_coverage_id);");
        Execute(connection, transaction, "CREATE INDEX idx_model_coverage_task_signals ON model_coverage_task_signals(signal, model_coverage_id);");
        Execute(connection, transaction, "CREATE INDEX idx_material_texture_slots_material ON material_texture_slots(material_name);");
        Execute(connection, transaction, "CREATE INDEX idx_material_texture_slots_texture ON material_texture_slots(texture_name, shared_texture);");
        Execute(connection, transaction, "CREATE INDEX idx_shared_gltf_texture_links_model ON shared_gltf_texture_links(model);");
        Execute(connection, transaction, "CREATE INDEX idx_shared_gltf_texture_links_status ON shared_gltf_texture_links(status);");
        Execute(connection, transaction, "CREATE INDEX idx_component_asset_relations_owner ON component_asset_relations(owner_object_path);");
        Execute(connection, transaction, "CREATE INDEX idx_component_asset_relations_target ON component_asset_relations(relation_type, target_path);");
        Execute(connection, transaction, "CREATE INDEX idx_component_asset_relations_match ON component_asset_relations(match_status, target_asset_output);");
        Execute(connection, transaction, "CREATE INDEX idx_component_groups_model_refs ON component_groups(model_reference_count, exported_model_reference_count);");
        Execute(connection, transaction, "CREATE INDEX idx_component_groups_missing ON component_groups(missing_reference_count, missing_model_reference_count, missing_material_reference_count);");
        Execute(connection, transaction, "CREATE INDEX idx_package_object_maps_source ON package_object_maps(source_path, map_type);");
        Execute(connection, transaction, "CREATE INDEX idx_package_object_maps_object ON package_object_maps(object_path);");
        Execute(connection, transaction, "CREATE INDEX idx_package_object_maps_class ON package_object_maps(class_name, class_path);");
        Execute(connection, transaction, "CREATE INDEX idx_asset_registry_dependencies_source ON asset_registry_dependencies(source_package, dependency_category);");
        Execute(connection, transaction, "CREATE INDEX idx_asset_registry_dependencies_dependency ON asset_registry_dependencies(dependency_package, dependency_kind);");
        Execute(connection, transaction, "CREATE INDEX idx_asset_registry_dependencies_class ON asset_registry_dependencies(source_asset_class, dependency_asset_class);");
        Execute(connection, transaction, "CREATE INDEX idx_anim_blueprint_refs_owner ON anim_blueprint_animation_refs(anim_blueprint_object_path);");
        Execute(connection, transaction, "CREATE INDEX idx_anim_blueprint_refs_generated ON anim_blueprint_animation_refs(generated_class_object_path);");
        Execute(connection, transaction, "CREATE INDEX idx_anim_blueprint_refs_animation ON anim_blueprint_animation_refs(referenced_animation_path);");
        Execute(connection, transaction, "CREATE INDEX idx_skeleton_groups_path ON skeleton_groups(skeleton_path);");
        Execute(connection, transaction, "CREATE INDEX idx_skeleton_groups_counts ON skeleton_groups(model_count, animation_count);");
        Execute(connection, transaction, "CREATE INDEX idx_relations_skeleton ON model_animation_relations(skeleton_path);");
        Execute(connection, transaction, "CREATE INDEX idx_relation_animations_status ON relation_animations(validation_status, validation_category);");
        Execute(connection, transaction, "CREATE INDEX idx_relation_animations_usage ON relation_animations(usage_evidence, is_explicit_usage, is_skeleton_compatible);");
        Execute(connection, transaction, "CREATE INDEX idx_relation_animations_confidence ON relation_animations(confidence_tier, is_deterministic_usage, is_compatibility_candidate);");
        Execute(connection, transaction, "CREATE INDEX idx_relation_animations_recommended ON relation_animations(relationship_kind, recommended_use);");
        Execute(connection, transaction, "CREATE INDEX idx_relation_animation_evidence_animation ON relation_animation_evidence(relation_animation_id, step_index);");
        Execute(connection, transaction, "CREATE INDEX idx_animation_validation_pair ON animation_validation(model, animation);");
        Execute(connection, transaction, "CREATE INDEX idx_animation_validation_status ON animation_validation(status);");
        Execute(connection, transaction, "CREATE INDEX idx_animation_validation_missing_bones ON animation_validation_missing_track_bones(bone_name, animation_validation_id);");
        Execute(connection, transaction, "CREATE INDEX idx_animation_validation_hierarchy_mismatches ON animation_validation_hierarchy_mismatches(mismatch, animation_validation_id);");
        Execute(connection, transaction, "CREATE INDEX idx_library_reports_name ON library_reports(name, status);");
    }

    private static void FinalizeSqliteOutput(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        Execute(connection, "PRAGMA journal_mode = DELETE;");
    }

    private static void DeleteSqliteOutput(string dbPath)
    {
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void InsertAsset(SqliteConnection connection, SqliteTransaction transaction, JObject row)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO assets (
                kind, resource_kind, name, source_type, source, object_path, output, format,
                skeleton_path, skeleton_name, validation_status, raw_json
            )
            VALUES (
                $kind, $resourceKind, $name, $sourceType, $source, $objectPath, $output, $format,
                $skeletonPath, $skeletonName, $validationStatus, $rawJson
            );
            """;
        Add(command, "$kind", (string?)row["kind"] ?? "Asset");
        Add(command, "$resourceKind", (string?)row["resourceKind"]);
        Add(command, "$name", (string?)row["name"]);
        Add(command, "$sourceType", (string?)row["sourceType"]);
        Add(command, "$source", (string?)row["source"]);
        Add(command, "$objectPath", (string?)row["objectPath"]);
        Add(command, "$output", (string?)row["output"]);
        Add(command, "$format", (string?)row["format"]);
        Add(command, "$skeletonPath", (string?)row["skeletonPath"]);
        Add(command, "$skeletonName", (string?)row["skeletonName"]);
        Add(command, "$validationStatus", (string?)row["validationStatus"] ?? (string?)row["status"]);
        Add(command, "$rawJson", row.ToString(Formatting.None));
        command.ExecuteNonQuery();
    }

    private static void InsertTextureLink(SqliteConnection connection, SqliteTransaction transaction, TextureLinkInfo link)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO texture_links (source, shared, sha256, size_bytes, extension, hard_linked, link_error)
            VALUES ($source, $shared, $sha256, $sizeBytes, $extension, $hardLinked, $linkError);
            """;
        Add(command, "$source", link.RelativePath);
        Add(command, "$shared", link.SharedRelativePath);
        Add(command, "$sha256", link.Hash);
        Add(command, "$sizeBytes", link.SizeBytes);
        Add(command, "$extension", link.Extension);
        Add(command, "$hardLinked", link.HardLinked ? 1 : 0);
        Add(command, "$linkError", link.LinkError);
        command.ExecuteNonQuery();
    }

    private static void InsertMaterialSidecar(SqliteConnection connection, SqliteTransaction transaction, MaterialInfo material)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO material_sidecars (
                name, relative_path, size_bytes, last_write_utc_ticks, texture_slot_count,
                color_count, scalar_count, switch_count, blend_mode, shading_model, raw_json
            )
            VALUES (
                $name, $relativePath, $sizeBytes, $lastWriteUtcTicks, $textureSlotCount,
                $colorCount, $scalarCount, $switchCount, $blendMode, $shadingModel, $rawJson
            );
            """;
        Add(command, "$name", material.Name);
        Add(command, "$relativePath", material.RelativePath);
        Add(command, "$sizeBytes", material.SizeBytes);
        Add(command, "$lastWriteUtcTicks", material.LastWriteUtcTicks);
        Add(command, "$textureSlotCount", material.TextureSlotCount);
        Add(command, "$colorCount", material.ColorCount);
        Add(command, "$scalarCount", material.ScalarCount);
        Add(command, "$switchCount", material.SwitchCount);
        Add(command, "$blendMode", material.BlendMode);
        Add(command, "$shadingModel", material.ShadingModel);
        Add(command, "$rawJson", material.RawJson.ToString(Formatting.None));
        command.ExecuteNonQuery();
    }

    private static bool InsertTextureLinksFromWorkDb(SqliteConnection connection, SqliteTransaction transaction, string root)
    {
        using var workConnection = OpenLibraryWorkDbReadOnly(root);
        if (workConnection == null || !TableExists(workConnection, "texture_links") || CountTableRows(workConnection, "texture_links") == 0)
            return false;

        using var command = workConnection.CreateCommand();
        command.CommandText = """
            SELECT source, shared, sha256, size_bytes, extension, hard_linked, link_error
            FROM texture_links
            ORDER BY id;
            """;
        using var reader = command.ExecuteReader();
        var inserted = 0;
        while (reader.Read())
        {
            var source = GetString(reader, 0) ?? "";
            var shared = GetString(reader, 1) ?? "";
            InsertTextureLink(connection, transaction, new TextureLinkInfo
            {
                Path = Path.Combine(root, source.Replace('/', Path.DirectorySeparatorChar)),
                RelativePath = source,
                SharedPath = Path.Combine(root, shared.Replace('/', Path.DirectorySeparatorChar)),
                SharedRelativePath = shared,
                Hash = GetString(reader, 2) ?? "",
                SizeBytes = reader.GetInt64(3),
                Extension = GetString(reader, 4) ?? "",
                HardLinked = !reader.IsDBNull(5) && reader.GetInt32(5) != 0,
                LinkError = GetString(reader, 6),
            });
            inserted++;
        }

        Console.WriteLine($"SQLite texture links inserted from work DB: {inserted}.");
        return true;
    }

    private static SqliteConnection? OpenLibraryWorkDbReadOnly(string root)
    {
        var dbPath = GetLibraryWorkDbPath(root);
        if (!File.Exists(dbPath))
            return null;

        var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        return connection;
    }

    private static void WriteLibraryWorkReport(string root, string name, JObject report)
    {
        var connection = OpenLibraryWorkDb(root);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO library_reports (name, status, generated_at, raw_json)
                VALUES ($name, $status, $generatedAt, $rawJson)
                ON CONFLICT(name) DO UPDATE SET
                    status = excluded.status,
                    generated_at = excluded.generated_at,
                    raw_json = excluded.raw_json;
                """;
            Add(command, "$name", name);
            Add(command, "$status", (string?)report["status"]);
            Add(command, "$generatedAt", (string?)report["generatedAt"]);
            Add(command, "$rawJson", report.ToString(Formatting.None));
            command.ExecuteNonQuery();
        }
        finally
        {
            FinalizeWorkDb(connection);
        }
    }

    private static JObject? LoadLibraryWorkReport(string root, string name)
    {
        using var connection = OpenLibraryWorkDbReadOnly(root);
        if (connection == null || !TableExists(connection, "library_reports"))
            return null;

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT raw_json
            FROM library_reports
            WHERE name = $name;
            """;
        Add(command, "$name", name);
        var raw = command.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        try
        {
            return JObject.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void InsertExportManifestRows(SqliteConnection connection, SqliteTransaction transaction, string root)
    {
        foreach (var row in ReadExportEventRows(root, "export_manifest", Path.Combine(root, "export_manifest.jsonl")))
        {
            using var command = connection.CreateCommand();
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
    }

    private static void InsertAnimationBindingRows(SqliteConnection connection, SqliteTransaction transaction, string root)
    {
        foreach (var row in ReadExportEventRows(root, "animation_bindings", Path.Combine(root, "animation_bindings.jsonl")))
        {
            using var command = connection.CreateCommand();
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
    }

    private static void InsertAutoReferencedExportRows(SqliteConnection connection, SqliteTransaction transaction, string root)
    {
        foreach (var row in ReadExportEventRows(root, "auto_referenced_exports", Path.Combine(root, "auto_referenced_exports.jsonl")))
        {
            using var command = connection.CreateCommand();
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
    }

    private static IEnumerable<JObject> ReadJsonLines(string path)
    {
        if (!File.Exists(path))
            yield break;

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JObject? row = null;
            try
            {
                row = JObject.Parse(line);
            }
            catch (JsonException)
            {
                // JSONL 是流式日志，旧版本或中断导出可能留下半行；索引重建时跳过坏行。
            }

            if (row != null)
                yield return row;
        }
    }

    private static IEnumerable<JObject> ReadExportEventRows(string root, string tableName, string legacyJsonLinesPath)
    {
        var dbPath = Path.Combine(root, "export_events.db");
        if (File.Exists(dbPath))
        {
            SQLitePCL.Batteries_V2.Init();
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();
            if (TableExists(connection, tableName))
            {
                using var countCommand = connection.CreateCommand();
                countCommand.CommandText = $"SELECT COUNT(*) FROM {ValidateExportEventTableName(tableName)};";
                var count = Convert.ToInt64(countCommand.ExecuteScalar());
                if (count > 0)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT raw_json FROM {ValidateExportEventTableName(tableName)} ORDER BY id;";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        JObject? row = null;
                        try
                        {
                            row = JObject.Parse(reader.GetString(0));
                        }
                        catch (JsonException)
                        {
                            // export_events.db stores raw JSON for forward compatibility; skip corrupted rows defensively.
                        }

                        if (row != null)
                            yield return row;
                    }

                    yield break;
                }
            }
        }

        foreach (var row in ReadJsonLines(legacyJsonLinesPath))
            yield return row;
    }

    private static string ValidateExportEventTableName(string tableName)
        => tableName is "export_manifest" or "asset_catalog" or "animation_bindings" or "auto_referenced_exports"
            ? tableName
            : throw new ArgumentOutOfRangeException(nameof(tableName), tableName, "Unknown export event table.");

    private static void InsertModelValidation(SqliteConnection connection, SqliteTransaction transaction, ModelValidationEntry report)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO model_validation (
                path, name, resource_kind, status, mesh_count, material_count, texture_count,
                skin_count, bone_count, animation_count, skeleton_hash,
                bbox_min_x, bbox_min_y, bbox_min_z, bbox_max_x, bbox_max_y, bbox_max_z,
                bbox_size_x, bbox_size_y, bbox_size_z
            )
            VALUES (
                $path, $name, $resourceKind, $status, $meshCount, $materialCount, $textureCount,
                $skinCount, $boneCount, $animationCount, $skeletonHash,
                $bboxMinX, $bboxMinY, $bboxMinZ, $bboxMaxX, $bboxMaxY, $bboxMaxZ,
                $bboxSizeX, $bboxSizeY, $bboxSizeZ
            );
            """;
        var bbox = ReadModelValidationBBox(report.BBox);
        Add(command, "$path", report.RelativePath);
        Add(command, "$name", report.Name);
        Add(command, "$resourceKind", report.ResourceKind);
        Add(command, "$status", report.Status);
        Add(command, "$meshCount", report.MeshCount);
        Add(command, "$materialCount", report.MaterialCount);
        Add(command, "$textureCount", report.ImageCount);
        Add(command, "$skinCount", report.SkinCount);
        Add(command, "$boneCount", report.BoneCount);
        Add(command, "$animationCount", report.AnimationCount);
        Add(command, "$skeletonHash", report.SkeletonHash);
        Add(command, "$bboxMinX", bbox?.MinX);
        Add(command, "$bboxMinY", bbox?.MinY);
        Add(command, "$bboxMinZ", bbox?.MinZ);
        Add(command, "$bboxMaxX", bbox?.MaxX);
        Add(command, "$bboxMaxY", bbox?.MaxY);
        Add(command, "$bboxMaxZ", bbox?.MaxZ);
        Add(command, "$bboxSizeX", bbox?.SizeX);
        Add(command, "$bboxSizeY", bbox?.SizeY);
        Add(command, "$bboxSizeZ", bbox?.SizeZ);
        command.ExecuteNonQuery();
        var validationId = GetLastInsertRowId(connection, transaction);
        InsertModelValidationNotes(connection, transaction, validationId, report.Notes);
    }

    private static (double MinX, double MinY, double MinZ, double MaxX, double MaxY, double MaxZ, double SizeX, double SizeY, double SizeZ)? ReadModelValidationBBox(object? bbox)
    {
        if (bbox == null)
            return null;

        var token = bbox as JToken ?? JToken.FromObject(bbox);
        if (token["min"] is not JArray min || token["max"] is not JArray max || token["size"] is not JArray size ||
            min.Count < 3 || max.Count < 3 || size.Count < 3)
            return null;

        return (
            (double)min[0]!, (double)min[1]!, (double)min[2]!,
            (double)max[0]!, (double)max[1]!, (double)max[2]!,
            (double)size[0]!, (double)size[1]!, (double)size[2]!);
    }

    private static void InsertModelValidationNotes(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long modelValidationId,
        string[] notes)
    {
        if (modelValidationId <= 0 || notes.Length == 0)
            return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO model_validation_notes (
                model_validation_id, note_index, note
            )
            VALUES (
                $modelValidationId, $noteIndex, $note
            );
            """;
        var idParam = command.Parameters.Add("$modelValidationId", SqliteType.Integer);
        var indexParam = command.Parameters.Add("$noteIndex", SqliteType.Integer);
        var noteParam = command.Parameters.Add("$note", SqliteType.Text);
        idParam.Value = modelValidationId;
        for (var i = 0; i < notes.Length; i++)
        {
            indexParam.Value = i;
            noteParam.Value = notes[i];
            command.ExecuteNonQuery();
        }
    }

    private static void InsertModelCoverage(SqliteConnection connection, SqliteTransaction transaction, JObject modelCoverage)
    {
        foreach (var row in ((JArray?)modelCoverage["models"] ?? []).OfType<JObject>())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // 这些质量标记只来自覆盖报告里的通用字段，方便浏览器直接筛出需要复查的任务/道具模型。
            var taskSignals = ((JArray?)row["TaskSignals"] ?? []);
            var resourceKind = (string?)row["ResourceKind"];
            var validationStatus = (string?)row["ValidationStatus"] ?? "unknown";
            var componentReferenceCount = (int?)row["ComponentReferenceCount"] ?? 0;
            var sourceIndexObjectCount = (int?)row["SourceIndexObjectCount"] ?? 0;
            var materialCount = (int?)row["MaterialCount"] ?? 0;
            var textureCount = (int?)row["TextureCount"] ?? 0;
            var isTaskOrProp = taskSignals.Count > 0 || string.Equals(resourceKind, "Prop", StringComparison.OrdinalIgnoreCase);
            var isPathOnlyTask = isTaskOrProp && componentReferenceCount == 0 && sourceIndexObjectCount == 0;
            var missingMaterials = isTaskOrProp && materialCount == 0;
            var noExternalTextureSlots = isTaskOrProp && textureCount == 0;
            var coverageRow = ReadModelCoverageRow(row);
            var reviewReasons = isTaskOrProp ? BuildTaskQualityIssueReasons(coverageRow) : [];
            var relationReviewReasons = isTaskOrProp ? BuildTaskRelationReviewReasons(coverageRow) : [];
            // pathOnly 只是“关系待补”，不代表模型质量不可用；质量复查只看验证、材质和贴图事实。
            var needsReview = reviewReasons.Length > 0;
            var relationNeedsReview = relationReviewReasons.Length > 0;
            command.CommandText = """
                INSERT INTO model_coverage (
                    name, output, source, object_path, resource_kind, source_type, validation_status,
                    is_static, has_skin, has_skeleton_path, material_count, texture_count,
                    component_reference_count, source_index_object_count, animation_candidate_count,
                    is_task_or_prop, is_path_only_task, missing_materials, no_external_texture_slots, needs_review,
                    relation_needs_review,
                    raw_json
                )
                VALUES (
                    $name, $output, $source, $objectPath, $resourceKind, $sourceType, $validationStatus,
                    $isStatic, $hasSkin, $hasSkeletonPath, $materialCount, $textureCount,
                    $componentReferenceCount, $sourceIndexObjectCount, $animationCandidateCount,
                    $isTaskOrProp, $isPathOnlyTask, $missingMaterials, $noExternalTextureSlots, $needsReview,
                    $relationNeedsReview,
                    $rawJson
                );
                """;
            Add(command, "$name", (string?)row["Name"]);
            Add(command, "$output", (string?)row["Output"] ?? "");
            Add(command, "$source", (string?)row["Source"]);
            Add(command, "$objectPath", (string?)row["ObjectPath"]);
            Add(command, "$resourceKind", resourceKind);
            Add(command, "$sourceType", (string?)row["SourceType"]);
            Add(command, "$validationStatus", validationStatus);
            Add(command, "$isStatic", ((bool?)row["IsStatic"] ?? false) ? 1 : 0);
            Add(command, "$hasSkin", ((bool?)row["HasSkin"] ?? false) ? 1 : 0);
            Add(command, "$hasSkeletonPath", ((bool?)row["HasSkeletonPath"] ?? false) ? 1 : 0);
            Add(command, "$materialCount", materialCount);
            Add(command, "$textureCount", textureCount);
            Add(command, "$componentReferenceCount", componentReferenceCount);
            Add(command, "$sourceIndexObjectCount", sourceIndexObjectCount);
            Add(command, "$animationCandidateCount", (int?)row["AnimationCandidateCount"] ?? 0);
            Add(command, "$isTaskOrProp", isTaskOrProp ? 1 : 0);
            Add(command, "$isPathOnlyTask", isPathOnlyTask ? 1 : 0);
            Add(command, "$missingMaterials", missingMaterials ? 1 : 0);
            Add(command, "$noExternalTextureSlots", noExternalTextureSlots ? 1 : 0);
            Add(command, "$needsReview", needsReview ? 1 : 0);
            Add(command, "$relationNeedsReview", relationNeedsReview ? 1 : 0);
            Add(command, "$rawJson", row.ToString(Formatting.None));
            command.ExecuteNonQuery();
            var coverageId = GetLastInsertRowId(connection, transaction);
            InsertModelCoverageTaskSignals(connection, transaction, coverageId, taskSignals);
            InsertModelCoverageReviewReasons(connection, transaction, coverageId, "quality", reviewReasons);
            InsertModelCoverageReviewReasons(connection, transaction, coverageId, "relation", relationReviewReasons);
        }
    }

    private static void InsertModelCoverageReviewReasons(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long modelCoverageId,
        string reasonKind,
        string[] reasons)
    {
        if (modelCoverageId <= 0 || reasons.Length == 0)
            return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO model_coverage_review_reasons (
                model_coverage_id, reason_kind, reason_index, reason
            )
            VALUES (
                $modelCoverageId, $reasonKind, $reasonIndex, $reason
            );
            """;
        var idParam = command.Parameters.Add("$modelCoverageId", SqliteType.Integer);
        var kindParam = command.Parameters.Add("$reasonKind", SqliteType.Text);
        var indexParam = command.Parameters.Add("$reasonIndex", SqliteType.Integer);
        var reasonParam = command.Parameters.Add("$reason", SqliteType.Text);
        idParam.Value = modelCoverageId;
        kindParam.Value = reasonKind;
        for (var i = 0; i < reasons.Length; i++)
        {
            indexParam.Value = i;
            reasonParam.Value = reasons[i];
            command.ExecuteNonQuery();
        }
    }

    private static void InsertModelCoverageTaskSignals(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long modelCoverageId,
        JArray taskSignals)
    {
        var signals = taskSignals
            .Select(x => (string?)x)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToArray();
        if (modelCoverageId <= 0 || signals.Length == 0)
            return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO model_coverage_task_signals (
                model_coverage_id, signal_index, signal
            )
            VALUES (
                $modelCoverageId, $signalIndex, $signal
            );
            """;
        var idParam = command.Parameters.Add("$modelCoverageId", SqliteType.Integer);
        var indexParam = command.Parameters.Add("$signalIndex", SqliteType.Integer);
        var signalParam = command.Parameters.Add("$signal", SqliteType.Text);
        idParam.Value = modelCoverageId;
        for (var i = 0; i < signals.Length; i++)
        {
            indexParam.Value = i;
            signalParam.Value = signals[i];
            command.ExecuteNonQuery();
        }
    }

    private static void InsertMaterialTextureSlot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MaterialTextureSlotLink slot)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO material_texture_slots (
                material_name, material_path, material_object_path, slot_name,
                texture_name, texture_object_path, texture_path, texture_class_name, texture_class_path, missing_category,
                exported_texture, shared_texture, sha256, hard_linked,
                match_status, match_reason, relation_source
            )
            VALUES (
                $materialName, $materialPath, $materialObjectPath, $slotName,
                $textureName, $textureObjectPath, $texturePath, $textureClassName, $textureClassPath, $missingCategory,
                $exportedTexture, $sharedTexture, $sha256, $hardLinked,
                $matchStatus, $matchReason, $relationSource
            );
            """;
        Add(command, "$materialName", slot.MaterialName);
        Add(command, "$materialPath", slot.MaterialPath);
        Add(command, "$materialObjectPath", slot.MaterialObjectPath);
        Add(command, "$slotName", slot.SlotName);
        Add(command, "$textureName", slot.TextureName);
        Add(command, "$textureObjectPath", slot.TextureObjectPath);
        Add(command, "$texturePath", slot.TexturePath);
        Add(command, "$textureClassName", slot.TextureClassName);
        Add(command, "$textureClassPath", slot.TextureClassPath);
        Add(command, "$missingCategory", slot.MissingCategory);
        Add(command, "$exportedTexture", slot.ExportedTexture);
        Add(command, "$sharedTexture", slot.SharedTexture);
        Add(command, "$sha256", slot.Sha256);
        Add(command, "$hardLinked", slot.HardLinked == null ? null : slot.HardLinked.Value ? 1 : 0);
        Add(command, "$matchStatus", slot.MatchStatus);
        Add(command, "$matchReason", slot.MatchReason);
        Add(command, "$relationSource", slot.RelationSource);
        command.ExecuteNonQuery();
    }

    private static bool InsertMaterialTextureSlotsFromWorkDb(SqliteConnection connection, SqliteTransaction transaction, string root)
    {
        using var workConnection = OpenLibraryWorkDbReadOnly(root);
        if (workConnection == null || !TableExists(workConnection, "material_texture_slots") || CountTableRows(workConnection, "material_texture_slots") == 0)
            return false;

        using var command = workConnection.CreateCommand();
        command.CommandText = """
            SELECT material_name, material_path, material_object_path, slot_name,
                   texture_name, texture_object_path, texture_path, texture_class_name, texture_class_path, missing_category,
                   exported_texture, shared_texture, sha256, hard_linked,
                   match_status, match_reason, relation_source
            FROM material_texture_slots
            ORDER BY id;
            """;
        using var reader = command.ExecuteReader();
        var inserted = 0;
        while (reader.Read())
        {
            InsertMaterialTextureSlot(connection, transaction, new MaterialTextureSlotLink
            {
                MaterialName = GetString(reader, 0) ?? "",
                MaterialPath = GetString(reader, 1),
                MaterialObjectPath = GetString(reader, 2),
                SlotName = GetString(reader, 3) ?? "",
                TextureName = GetString(reader, 4),
                TextureObjectPath = GetString(reader, 5),
                TexturePath = GetString(reader, 6),
                TextureClassName = GetString(reader, 7),
                TextureClassPath = GetString(reader, 8),
                MissingCategory = GetString(reader, 9),
                ExportedTexture = GetString(reader, 10),
                SharedTexture = GetString(reader, 11),
                Sha256 = GetString(reader, 12),
                HardLinked = reader.IsDBNull(13) ? null : reader.GetInt32(13) != 0,
                MatchStatus = GetString(reader, 14) ?? "",
                MatchReason = GetString(reader, 15),
                RelationSource = GetString(reader, 16) ?? "",
            });
            inserted++;
        }

        Console.WriteLine($"SQLite material texture slots inserted from work DB: {inserted}.");
        return true;
    }

    private static void InsertSharedGltfTextureLink(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SharedGltfTextureLink link)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO shared_gltf_texture_links (
                model, material_name, semantic, slot_name, texture_name,
                image_index, shared_texture, sha256, uri,
                removed_buffer_view, status, reason
            )
            VALUES (
                $model, $materialName, $semantic, $slotName, $textureName,
                $imageIndex, $sharedTexture, $sha256, $uri,
                $removedBufferView, $status, $reason
            );
            """;
        Add(command, "$model", link.Model);
        Add(command, "$materialName", link.MaterialName);
        Add(command, "$semantic", link.Semantic);
        Add(command, "$slotName", link.SlotName);
        Add(command, "$textureName", link.TextureName);
        Add(command, "$imageIndex", link.ImageIndex);
        Add(command, "$sharedTexture", link.SharedTexture);
        Add(command, "$sha256", link.Sha256);
        Add(command, "$uri", link.Uri);
        Add(command, "$removedBufferView", link.RemovedBufferView ? 1 : 0);
        Add(command, "$status", link.Status);
        Add(command, "$reason", link.Reason);
        command.ExecuteNonQuery();
    }

    private static bool InsertSharedGltfTextureLinksFromWorkDb(SqliteConnection connection, SqliteTransaction transaction, string root)
    {
        using var workConnection = OpenLibraryWorkDbReadOnly(root);
        if (workConnection == null || !TableExists(workConnection, "shared_gltf_texture_links") || CountTableRows(workConnection, "shared_gltf_texture_links") == 0)
            return false;

        using var command = workConnection.CreateCommand();
        command.CommandText = """
            SELECT model, material_name, semantic, slot_name, texture_name,
                   image_index, shared_texture, sha256, uri,
                   removed_buffer_view, status, reason
            FROM shared_gltf_texture_links
            ORDER BY id;
            """;
        using var reader = command.ExecuteReader();
        var inserted = 0;
        while (reader.Read())
        {
            InsertSharedGltfTextureLink(connection, transaction, new SharedGltfTextureLink
            {
                Model = GetString(reader, 0) ?? "",
                MaterialName = GetString(reader, 1) ?? "",
                Semantic = GetString(reader, 2) ?? "",
                SlotName = GetString(reader, 3) ?? "",
                TextureName = GetString(reader, 4),
                ImageIndex = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                SharedTexture = GetString(reader, 6),
                Sha256 = GetString(reader, 7),
                Uri = GetString(reader, 8),
                RemovedBufferView = !reader.IsDBNull(9) && reader.GetInt32(9) != 0,
                Status = GetString(reader, 10) ?? "",
                Reason = GetString(reader, 11),
            });
            inserted++;
        }

        Console.WriteLine($"SQLite shared glTF texture links inserted from work DB: {inserted}.");
        return true;
    }

    private static void InsertComponentAssetRelation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ComponentAssetRelationLink link)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO component_asset_relations (
                owner_object_path, owner_type, component_object_path, component_type,
                component_name, component_variable_name, relation_source, relation_type,
                target_path, target_name, target_asset_name, target_asset_kind, target_asset_output,
                match_status, match_reason, socket_name, parent_component_path, transform_json, source_path
            )
            VALUES (
                $ownerObjectPath, $ownerType, $componentObjectPath, $componentType,
                $componentName, $componentVariableName, $relationSource, $relationType,
                $targetPath, $targetName, $targetAssetName, $targetAssetKind, $targetAssetOutput,
                $matchStatus, $matchReason, $socketName, $parentComponentPath, $transformJson, $sourcePath
            );
            """;
        Add(command, "$ownerObjectPath", link.OwnerObjectPath);
        Add(command, "$ownerType", link.OwnerType);
        Add(command, "$componentObjectPath", link.ComponentObjectPath);
        Add(command, "$componentType", link.ComponentType);
        Add(command, "$componentName", link.ComponentName);
        Add(command, "$componentVariableName", link.ComponentVariableName);
        Add(command, "$relationSource", link.RelationSource);
        Add(command, "$relationType", link.RelationType);
        Add(command, "$targetPath", link.TargetPath);
        Add(command, "$targetName", link.TargetName);
        Add(command, "$targetAssetName", link.TargetAssetName);
        Add(command, "$targetAssetKind", link.TargetAssetKind);
        Add(command, "$targetAssetOutput", link.TargetAssetOutput);
        Add(command, "$matchStatus", link.MatchStatus);
        Add(command, "$matchReason", link.MatchReason);
        Add(command, "$socketName", link.SocketName);
        Add(command, "$parentComponentPath", link.ParentComponentPath);
        Add(command, "$transformJson", link.Transform == null ? null : JsonConvert.SerializeObject(link.Transform));
        Add(command, "$sourcePath", link.SourcePath);
        command.ExecuteNonQuery();
    }

    private static bool InsertComponentAssetRelationRowsFromJsonLines(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string root)
    {
        var path = Path.Combine(root, "component_asset_relations.jsonl");
        if (!File.Exists(path))
            return false;

        var inserted = 0;
        foreach (var row in ReadJsonLines(path))
        {
            InsertComponentAssetRelation(connection, transaction, ReadComponentAssetRelationLink(row));
            inserted++;
        }

        Console.WriteLine($"SQLite component relations inserted from JSONL: {inserted}.");
        return true;
    }

    private static bool InsertComponentAssetRelationRowsFromWorkDb(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string root)
    {
        var dbPath = GetLibraryWorkDbPath(root);
        if (!File.Exists(dbPath))
            return false;

        using var workConnection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        workConnection.Open();
        if (!TableExists(workConnection, "component_asset_relations"))
            return false;

        using (var countCommand = workConnection.CreateCommand())
        {
            countCommand.CommandText = "SELECT COUNT(*) FROM component_asset_relations;";
            var count = Convert.ToInt64(countCommand.ExecuteScalar());
            if (count == 0)
            {
                Console.WriteLine("SQLite component relations inserted from work DB: 0.");
                return true;
            }
        }

        using var command = workConnection.CreateCommand();
        command.CommandText = """
            SELECT owner_object_path, owner_type, component_object_path, component_type,
                   component_name, component_variable_name, relation_source, relation_type,
                   target_path, target_name, target_asset_name, target_asset_kind, target_asset_output,
                   match_status, match_reason, socket_name, parent_component_path, transform_json, source_path
            FROM component_asset_relations
            ORDER BY id;
            """;
        using var reader = command.ExecuteReader();
        var inserted = 0;
        while (reader.Read())
        {
            InsertComponentAssetRelation(connection, transaction, ReadComponentAssetRelationLink(reader));
            inserted++;
        }

        Console.WriteLine($"SQLite component relations inserted from work DB: {inserted}.");
        return true;
    }

    private static ComponentAssetRelationLink ReadComponentAssetRelationLink(JObject row)
        => new()
        {
            SourcePath = (string?)row["SourcePath"] ?? (string?)row["sourcePath"],
            OwnerObjectPath = (string?)row["OwnerObjectPath"] ?? (string?)row["ownerObjectPath"],
            OwnerType = (string?)row["OwnerType"] ?? (string?)row["ownerType"],
            ComponentObjectPath = (string?)row["ComponentObjectPath"] ?? (string?)row["componentObjectPath"],
            ComponentType = (string?)row["ComponentType"] ?? (string?)row["componentType"],
            ComponentName = (string?)row["ComponentName"] ?? (string?)row["componentName"],
            ComponentVariableName = (string?)row["ComponentVariableName"] ?? (string?)row["componentVariableName"],
            RelationSource = (string?)row["RelationSource"] ?? (string?)row["relationSource"] ?? "",
            RelationType = (string?)row["RelationType"] ?? (string?)row["relationType"] ?? "",
            TargetPath = (string?)row["TargetPath"] ?? (string?)row["targetPath"],
            TargetName = (string?)row["TargetName"] ?? (string?)row["targetName"],
            TargetAssetName = (string?)row["TargetAssetName"] ?? (string?)row["targetAssetName"],
            TargetAssetKind = (string?)row["TargetAssetKind"] ?? (string?)row["targetAssetKind"],
            TargetAssetOutput = (string?)row["TargetAssetOutput"] ?? (string?)row["targetAssetOutput"],
            MatchStatus = (string?)row["MatchStatus"] ?? (string?)row["matchStatus"] ?? "",
            MatchReason = (string?)row["MatchReason"] ?? (string?)row["matchReason"],
            SocketName = (string?)row["SocketName"] ?? (string?)row["socketName"],
            ParentComponentPath = (string?)row["ParentComponentPath"] ?? (string?)row["parentComponentPath"],
            Transform = row["transform"]?.ToObject<object>(),
        };

    private static ComponentAssetRelationLink ReadComponentAssetRelationLink(SqliteDataReader reader)
        => new()
        {
            OwnerObjectPath = GetString(reader, 0),
            OwnerType = GetString(reader, 1),
            ComponentObjectPath = GetString(reader, 2),
            ComponentType = GetString(reader, 3),
            ComponentName = GetString(reader, 4),
            ComponentVariableName = GetString(reader, 5),
            RelationSource = GetString(reader, 6) ?? "",
            RelationType = GetString(reader, 7) ?? "",
            TargetPath = GetString(reader, 8),
            TargetName = GetString(reader, 9),
            TargetAssetName = GetString(reader, 10),
            TargetAssetKind = GetString(reader, 11),
            TargetAssetOutput = GetString(reader, 12),
            MatchStatus = GetString(reader, 13) ?? "",
            MatchReason = GetString(reader, 14),
            SocketName = GetString(reader, 15),
            ParentComponentPath = GetString(reader, 16),
            Transform = TryParseJson(GetString(reader, 17)),
            SourcePath = GetString(reader, 18),
        };

    private static void InsertComponentGroups(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ComponentAssetRelationLink[] links)
    {
        if (links.Length > MaxDetailedComponentGroupRelations)
        {
            Console.WriteLine($"SQLite component_groups skipped: retained relations {links.Length} exceed {MaxDetailedComponentGroupRelations}; query component_asset_relations for full details.");
            return;
        }

        foreach (var group in BuildComponentGroupRows(links))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO component_groups (
                    owner_object_path, owner_type, source_path, relation_count, component_count,
                    model_reference_count, exported_model_reference_count, animation_reference_count,
                    missing_model_reference_count, exported_animation_reference_count, missing_animation_reference_count,
                    material_reference_count, exported_material_reference_count, missing_material_reference_count,
                    missing_reference_count, raw_json
                )
                VALUES (
                    $ownerObjectPath, $ownerType, $sourcePath, $relationCount, $componentCount,
                    $modelReferenceCount, $exportedModelReferenceCount, $animationReferenceCount,
                    $missingModelReferenceCount, $exportedAnimationReferenceCount, $missingAnimationReferenceCount,
                    $materialReferenceCount, $exportedMaterialReferenceCount, $missingMaterialReferenceCount,
                    $missingReferenceCount, $rawJson
                );
                """;
            Add(command, "$ownerObjectPath", group.OwnerObjectPath);
            Add(command, "$ownerType", group.OwnerType);
            Add(command, "$sourcePath", group.SourcePath);
            Add(command, "$relationCount", group.RelationCount);
            Add(command, "$componentCount", group.ComponentCount);
            Add(command, "$modelReferenceCount", group.ModelReferenceCount);
            Add(command, "$exportedModelReferenceCount", group.ExportedModelReferenceCount);
            Add(command, "$missingModelReferenceCount", group.MissingModelReferenceCount);
            Add(command, "$animationReferenceCount", group.AnimationReferenceCount);
            Add(command, "$exportedAnimationReferenceCount", group.ExportedAnimationReferenceCount);
            Add(command, "$missingAnimationReferenceCount", group.MissingAnimationReferenceCount);
            Add(command, "$materialReferenceCount", group.MaterialReferenceCount);
            Add(command, "$exportedMaterialReferenceCount", group.ExportedMaterialReferenceCount);
            Add(command, "$missingMaterialReferenceCount", group.MissingMaterialReferenceCount);
            Add(command, "$missingReferenceCount", group.MissingReferenceCount);
            Add(command, "$rawJson", group.RawJson);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertPackageObjectMap(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourcePackageObjectMap row)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO package_object_maps (
                source_path, package_name, map_type, map_index,
                object_name, object_path, class_name, class_path,
                outer_path, super_path, template_path, target_package,
                is_asset, is_optional, object_flags, serial_size, public_export_hash, raw_json
            )
            VALUES (
                $sourcePath, $packageName, $mapType, $mapIndex,
                $objectName, $objectPath, $className, $classPath,
                $outerPath, $superPath, $templatePath, $targetPackage,
                $isAsset, $isOptional, $objectFlags, $serialSize, $publicExportHash, $rawJson
            );
            """;
        Add(command, "$sourcePath", row.SourcePath);
        Add(command, "$packageName", row.PackageName);
        Add(command, "$mapType", row.MapType);
        Add(command, "$mapIndex", row.MapIndex);
        Add(command, "$objectName", row.ObjectName);
        Add(command, "$objectPath", row.ObjectPath);
        Add(command, "$className", row.ClassName);
        Add(command, "$classPath", row.ClassPath);
        Add(command, "$outerPath", row.OuterPath);
        Add(command, "$superPath", row.SuperPath);
        Add(command, "$templatePath", row.TemplatePath);
        Add(command, "$targetPackage", row.TargetPackage);
        Add(command, "$isAsset", row.IsAsset == null ? null : row.IsAsset.Value ? 1 : 0);
        Add(command, "$isOptional", row.IsOptional == null ? null : row.IsOptional.Value ? 1 : 0);
        Add(command, "$objectFlags", row.ObjectFlags);
        Add(command, "$serialSize", row.SerialSize);
        Add(command, "$publicExportHash", row.PublicExportHash);
        Add(command, "$rawJson", row.RawJson);
        command.ExecuteNonQuery();
    }

    private static void InsertAssetRegistryDependency(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceAssetRegistryDependency row)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO asset_registry_dependencies (
                registry_path, source_package, source_asset_name, source_asset_class, source_identifier,
                dependency_package, dependency_asset_name, dependency_asset_class, dependency_identifier,
                dependency_category, dependency_kind, dependency_flags, dependency_flags_text,
                is_hard_package, is_soft_package, is_name_dependency, is_manage_dependency,
                relation_source, raw_json
            )
            VALUES (
                $registryPath, $sourcePackage, $sourceAssetName, $sourceAssetClass, $sourceIdentifier,
                $dependencyPackage, $dependencyAssetName, $dependencyAssetClass, $dependencyIdentifier,
                $dependencyCategory, $dependencyKind, $dependencyFlags, $dependencyFlagsText,
                $isHardPackage, $isSoftPackage, $isNameDependency, $isManageDependency,
                $relationSource, $rawJson
            );
            """;
        Add(command, "$registryPath", row.RegistryPath);
        Add(command, "$sourcePackage", row.SourcePackage);
        Add(command, "$sourceAssetName", row.SourceAssetName);
        Add(command, "$sourceAssetClass", row.SourceAssetClass);
        Add(command, "$sourceIdentifier", row.SourceIdentifier);
        Add(command, "$dependencyPackage", row.DependencyPackage);
        Add(command, "$dependencyAssetName", row.DependencyAssetName);
        Add(command, "$dependencyAssetClass", row.DependencyAssetClass);
        Add(command, "$dependencyIdentifier", row.DependencyIdentifier);
        Add(command, "$dependencyCategory", row.DependencyCategory);
        Add(command, "$dependencyKind", row.DependencyKind);
        Add(command, "$dependencyFlags", row.DependencyFlags);
        Add(command, "$dependencyFlagsText", row.DependencyFlagsText);
        Add(command, "$isHardPackage", row.IsHardPackage ? 1 : 0);
        Add(command, "$isSoftPackage", row.IsSoftPackage ? 1 : 0);
        Add(command, "$isNameDependency", row.IsNameDependency ? 1 : 0);
        Add(command, "$isManageDependency", row.IsManageDependency ? 1 : 0);
        Add(command, "$relationSource", row.RelationSource);
        Add(command, "$rawJson", row.RawJson);
        command.ExecuteNonQuery();
    }

    private static void InsertAnimBlueprintAnimationRef(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SourceAnimBlueprintAnimationRef row)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO anim_blueprint_animation_refs (
                source_path, anim_blueprint_object_path, generated_class_object_path,
                referenced_animation_path, referenced_animation_name, referenced_animation_type,
                property_path, node_type, skeleton_path, relation_source, confidence_hint, raw_json
            )
            VALUES (
                $sourcePath, $animBlueprintObjectPath, $generatedClassObjectPath,
                $referencedAnimationPath, $referencedAnimationName, $referencedAnimationType,
                $propertyPath, $nodeType, $skeletonPath, $relationSource, $confidenceHint, $rawJson
            );
            """;
        Add(command, "$sourcePath", row.SourcePath);
        Add(command, "$animBlueprintObjectPath", row.AnimBlueprintObjectPath);
        Add(command, "$generatedClassObjectPath", row.GeneratedClassObjectPath);
        Add(command, "$referencedAnimationPath", row.ReferencedAnimationPath);
        Add(command, "$referencedAnimationName", row.ReferencedAnimationName);
        Add(command, "$referencedAnimationType", row.ReferencedAnimationType);
        Add(command, "$propertyPath", row.PropertyPath);
        Add(command, "$nodeType", row.NodeType);
        Add(command, "$skeletonPath", row.SkeletonPath);
        Add(command, "$relationSource", row.RelationSource);
        Add(command, "$confidenceHint", row.ConfidenceHint);
        Add(command, "$rawJson", row.RawJson);
        command.ExecuteNonQuery();
    }

    private static void InsertSkeletonGroups(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JArray skeletonGroups)
    {
        foreach (var token in skeletonGroups.OfType<JObject>())
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO skeleton_groups (
                    skeleton_id, skeleton_path, skeleton_name,
                    model_count, animation_count, bone_count, source_object_count, raw_json
                )
                VALUES (
                    $skeletonId, $skeletonPath, $skeletonName,
                    $modelCount, $animationCount, $boneCount, $sourceObjectCount, $rawJson
                );
                """;
            Add(command, "$skeletonId", (string?)token["skeletonId"] ?? "");
            Add(command, "$skeletonPath", (string?)token["skeletonPath"]);
            Add(command, "$skeletonName", (string?)token["skeletonName"]);
            Add(command, "$modelCount", (int?)token["modelCount"] ?? 0);
            Add(command, "$animationCount", (int?)token["animationCount"] ?? 0);
            Add(command, "$boneCount", (int?)token["boneCount"] ?? 0);
            Add(command, "$sourceObjectCount", token["skeletonSourceObjects"] is JArray sources ? sources.Count : 0);
            Add(command, "$rawJson", token.ToString(Formatting.None));
            command.ExecuteNonQuery();
        }
    }

    private static void InsertModelAnimationRelations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        JObject modelAnimationRelations)
    {
        foreach (var relation in (JArray?)modelAnimationRelations["relations"] ?? [])
        {
            var relationObj = (JObject)relation;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO model_animation_relations (
                    model, model_name, model_source, skeleton_path, skeleton_name,
                    confidence, animation_count, raw_json
                )
                VALUES (
                    $model, $modelName, $modelSource, $skeletonPath, $skeletonName,
                    $confidence, $animationCount, $rawJson
                );
                SELECT last_insert_rowid();
                """;
            var animations = (JArray?)relationObj["animations"] ?? [];
            Add(command, "$model", (string?)relationObj["model"] ?? "");
            Add(command, "$modelName", (string?)relationObj["modelName"]);
            Add(command, "$modelSource", (string?)relationObj["modelSource"]);
            Add(command, "$skeletonPath", (string?)relationObj["skeletonPath"]);
            Add(command, "$skeletonName", (string?)relationObj["skeletonName"]);
            Add(command, "$confidence", (string?)relationObj["confidence"] ?? "Unknown");
            Add(command, "$animationCount", animations.Count);
            Add(command, "$rawJson", BuildModelAnimationRelationDbRawJson(relationObj, animations));
            var relationId = (long)command.ExecuteScalar()!;

            foreach (var animation in animations.OfType<JObject>())
                InsertRelationAnimation(connection, transaction, relationId, animation);
        }
    }

    private static string BuildModelAnimationRelationDbRawJson(JObject relationObj, JArray animations)
    {
        // 完整动画数组已经逐条写入 relation_animations。
        // SQLite 这一列只保留模型级摘要，避免大库里把同一批动画 JSON 重复写两遍。
        var summary = new JObject();
        foreach (var property in relationObj.Properties())
        {
            if (!string.Equals(property.Name, "animations", StringComparison.OrdinalIgnoreCase))
                summary[property.Name] = property.Value.DeepClone();
        }

        summary["totalAnimationCount"] = animations.Count;
        summary["usableAnimationCount"] = animations
            .OfType<JObject>()
            .Count(x => (bool?)x["isUsableCandidate"] ?? false);
        summary["sqliteRawJsonMode"] = "summaryOnly";
        summary["sqliteRawJsonNote"] = "动画明细存放在 relation_animations；SQLite-only 模式下不会重复写 model_animations.json 兼容视图。";
        return summary.ToString(Formatting.None);
    }

    private static void InsertRelationAnimation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long relationId,
        JObject animation)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO relation_animations (
                relation_id, name, source, output, status, duration, frame_count, track_count,
                relation_source, usage_evidence, is_explicit_usage, is_skeleton_compatible,
                validation_status, validation_category, validation_reason, track_coverage, hierarchy_compatible, is_container_animation,
                is_usable_candidate, confidence_tier, relationship_kind, recommended_use, evidence_summary, is_deterministic_usage, is_compatibility_candidate,
                segment_count, referenced_animation_count, exported_referenced_animation_count,
                missing_referenced_animation_count, section_count, raw_json
            )
            VALUES (
                $relationId, $name, $source, $output, $status, $duration, $frameCount, $trackCount,
                $relationSource, $usageEvidence, $isExplicitUsage, $isSkeletonCompatible,
                $validationStatus, $validationCategory, $validationReason, $trackCoverage, $hierarchyCompatible, $isContainerAnimation,
                $isUsableCandidate, $confidenceTier, $relationshipKind, $recommendedUse, $evidenceSummary, $isDeterministicUsage, $isCompatibilityCandidate,
                $segmentCount, $referencedAnimationCount, $exportedReferencedAnimationCount,
                $missingReferencedAnimationCount, $sectionCount, $rawJson
            );
            """;
        Add(command, "$relationId", relationId);
        Add(command, "$name", (string?)animation["name"]);
        Add(command, "$source", (string?)animation["source"]);
        Add(command, "$output", (string?)animation["output"]);
        Add(command, "$status", (string?)animation["status"]);
        Add(command, "$relationSource", (string?)animation["relationSource"]);
        Add(command, "$usageEvidence", (string?)animation["usageEvidence"]);
        Add(command, "$isExplicitUsage", ((bool?)animation["isExplicitUsage"] ?? false) ? 1 : 0);
        Add(command, "$isSkeletonCompatible", ((bool?)animation["isSkeletonCompatible"] ?? false) ? 1 : 0);
        Add(command, "$validationStatus", (string?)animation["validationStatus"]);
        Add(command, "$validationCategory", (string?)animation["validationCategory"]);
        Add(command, "$validationReason", (string?)animation["validationReason"]);
        Add(command, "$duration", (double?)animation["duration"]);
        Add(command, "$frameCount", (int?)animation["frameCount"]);
        Add(command, "$trackCount", (int?)animation["trackCount"]);
        Add(command, "$trackCoverage", (double?)animation["trackCoverage"]);
        Add(command, "$hierarchyCompatible", ((bool?)animation["hierarchyCompatible"] ?? false) ? 1 : 0);
        Add(command, "$isContainerAnimation", ((bool?)animation["isContainerAnimation"] ?? false) ? 1 : 0);
        Add(command, "$isUsableCandidate", ((bool?)animation["isUsableCandidate"] ?? false) ? 1 : 0);
        Add(command, "$confidenceTier", (string?)animation["confidenceTier"]);
        Add(command, "$relationshipKind", (string?)animation["relationshipKind"]);
        Add(command, "$recommendedUse", (string?)animation["recommendedUse"]);
        var evidenceSteps = ReadEvidenceSteps(animation["evidenceChain"]);
        Add(command, "$evidenceSummary", BuildEvidenceSummary(evidenceSteps));
        Add(command, "$isDeterministicUsage", ((bool?)animation["isDeterministicUsage"] ?? false) ? 1 : 0);
        Add(command, "$isCompatibilityCandidate", ((bool?)animation["isCompatibilityCandidate"] ?? false) ? 1 : 0);
        Add(command, "$segmentCount", (int?)animation["segmentCount"] ?? 0);
        Add(command, "$referencedAnimationCount", (int?)animation["referencedAnimationCount"] ?? 0);
        Add(command, "$exportedReferencedAnimationCount", (int?)animation["exportedReferencedAnimationCount"] ?? 0);
        Add(command, "$missingReferencedAnimationCount", (int?)animation["missingReferencedAnimationCount"] ?? 0);
        Add(command, "$sectionCount", (int?)animation["sectionCount"] ?? 0);
        Add(command, "$rawJson", animation.ToString(Formatting.None));
        command.ExecuteNonQuery();
        var animationId = GetLastInsertRowId(connection, transaction);
        InsertRelationAnimationEvidence(connection, transaction, animationId, evidenceSteps);
    }

    private static string[] ReadEvidenceSteps(JToken? evidenceChain)
    {
        if (evidenceChain is not JArray array)
            return [];

        return array
            .Select(x => (string?)x)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToArray();
    }

    private static string BuildEvidenceSummary(string[] evidenceSteps)
        => evidenceSteps.Length == 0 ? "" : string.Join(" -> ", evidenceSteps);

    private static long GetLastInsertRowId(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT last_insert_rowid();";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private static void InsertRelationAnimationEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long relationAnimationId,
        string[] evidenceSteps)
    {
        if (relationAnimationId <= 0 || evidenceSteps.Length == 0)
            return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO relation_animation_evidence (
                relation_animation_id, step_index, evidence_step
            )
            VALUES (
                $relationAnimationId, $stepIndex, $evidenceStep
            );
            """;
        var idParam = command.Parameters.Add("$relationAnimationId", SqliteType.Integer);
        var indexParam = command.Parameters.Add("$stepIndex", SqliteType.Integer);
        var stepParam = command.Parameters.Add("$evidenceStep", SqliteType.Text);
        idParam.Value = relationAnimationId;
        for (var i = 0; i < evidenceSteps.Length; i++)
        {
            indexParam.Value = i;
            stepParam.Value = evidenceSteps[i];
            command.ExecuteNonQuery();
        }
    }

    private static void InsertAnimationValidation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AnimationValidationSummary summary)
    {
        foreach (var validation in summary.Validations)
        {
            var rawJson = JObject.FromObject(new
            {
                validation.Status,
                validation.CandidateReason,
                validation.ValidationCategory,
                validation.Reason,
                model = validation.ModelOutput,
                animation = validation.AnimationOutput,
                validation.SkeletonPath,
                validation.ModelBoneCount,
                validation.AnimationTrackCount,
                validation.TrackSource,
                validation.MatchedTrackBones,
                validation.TrackCoverage,
                validation.HierarchyCompatible,
                validation.IsContainerAnimation,
                validation.MissingTrackBones,
                validation.HierarchyMismatches,
            });

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO animation_validation (
                    model, animation, skeleton_path, status, candidate_reason, reason,
                    validation_category,
                    model_bone_count, animation_track_count, matched_track_bones,
                    track_coverage, hierarchy_compatible, is_container_animation,
                    raw_json
                )
                VALUES (
                    $model, $animation, $skeletonPath, $status, $candidateReason, $reason,
                    $validationCategory,
                    $modelBoneCount, $animationTrackCount, $matchedTrackBones,
                    $trackCoverage, $hierarchyCompatible, $isContainerAnimation,
                    $rawJson
                );
                """;
            Add(command, "$model", validation.ModelOutput);
            Add(command, "$animation", validation.AnimationOutput);
            Add(command, "$skeletonPath", validation.SkeletonPath);
            Add(command, "$status", validation.Status);
            Add(command, "$candidateReason", validation.CandidateReason);
            Add(command, "$reason", validation.Reason);
            Add(command, "$validationCategory", validation.ValidationCategory);
            Add(command, "$modelBoneCount", validation.ModelBoneCount);
            Add(command, "$animationTrackCount", validation.AnimationTrackCount);
            Add(command, "$matchedTrackBones", validation.MatchedTrackBones);
            Add(command, "$trackCoverage", validation.TrackCoverage);
            Add(command, "$hierarchyCompatible", validation.HierarchyCompatible ? 1 : 0);
            Add(command, "$isContainerAnimation", validation.IsContainerAnimation ? 1 : 0);
            Add(command, "$rawJson", rawJson.ToString(Formatting.None));
            command.ExecuteNonQuery();
            var validationId = GetLastInsertRowId(connection, transaction);
            InsertAnimationValidationMissingTrackBones(connection, transaction, validationId, validation.MissingTrackBones);
            InsertAnimationValidationHierarchyMismatches(connection, transaction, validationId, validation.HierarchyMismatches);
        }
    }

    private static void InsertAnimationValidationMissingTrackBones(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long animationValidationId,
        string[] missingTrackBones)
    {
        if (animationValidationId <= 0 || missingTrackBones.Length == 0)
            return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO animation_validation_missing_track_bones (
                animation_validation_id, bone_index, bone_name
            )
            VALUES (
                $animationValidationId, $boneIndex, $boneName
            );
            """;
        var idParam = command.Parameters.Add("$animationValidationId", SqliteType.Integer);
        var indexParam = command.Parameters.Add("$boneIndex", SqliteType.Integer);
        var nameParam = command.Parameters.Add("$boneName", SqliteType.Text);
        idParam.Value = animationValidationId;
        for (var i = 0; i < missingTrackBones.Length; i++)
        {
            indexParam.Value = i;
            nameParam.Value = missingTrackBones[i];
            command.ExecuteNonQuery();
        }
    }

    private static void InsertAnimationValidationHierarchyMismatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long animationValidationId,
        string[] hierarchyMismatches)
    {
        if (animationValidationId <= 0 || hierarchyMismatches.Length == 0)
            return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO animation_validation_hierarchy_mismatches (
                animation_validation_id, mismatch_index, mismatch
            )
            VALUES (
                $animationValidationId, $mismatchIndex, $mismatch
            );
            """;
        var idParam = command.Parameters.Add("$animationValidationId", SqliteType.Integer);
        var indexParam = command.Parameters.Add("$mismatchIndex", SqliteType.Integer);
        var mismatchParam = command.Parameters.Add("$mismatch", SqliteType.Text);
        idParam.Value = animationValidationId;
        for (var i = 0; i < hierarchyMismatches.Length; i++)
        {
            indexParam.Value = i;
            mismatchParam.Value = hierarchyMismatches[i];
            command.ExecuteNonQuery();
        }
    }

    private static void InsertLibraryReport(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string name,
        JObject report)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO library_reports (name, status, generated_at, raw_json)
            VALUES ($name, $status, $generatedAt, $rawJson);
            """;
        Add(command, "$name", name);
        Add(command, "$status", (string?)report["status"]);
        Add(command, "$generatedAt", (string?)report["generatedAt"]);
        Add(command, "$rawJson", report.ToString(Formatting.None));
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static string? GetString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string[] SplitSqliteList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split((char)31, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int GetRequiredInt32(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));

    private static bool GetRequiredBool(SqliteDataReader reader, int ordinal)
        => GetRequiredInt32(reader, ordinal) != 0;

    private static bool? GetNullableBool(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal) != 0;

    private static double? GetNullableDouble(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static JToken? TryParseJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            return JToken.Parse(text);
        }
        catch
        {
            return null;
        }
    }

    private static JArray WriteSkeletonIndex(
        string root,
        List<ModelValidationEntry> reports,
        List<JObject> catalogRows,
        SourceIndexSnapshot sourceIndex,
        bool writeCompatibilityJson)
    {
        var modelsByOutput = catalogRows
            .Where(x => string.Equals((string?)x["kind"], "Model", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace((string?)x["output"]))
            .GroupBy(x => ((string)x["output"]!).Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        var animationsBySkeleton = catalogRows
            .Where(x => string.Equals((string?)x["kind"], "Animation", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace((string?)x["skeletonPath"]))
            .GroupBy(x => (string)x["skeletonPath"]!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

        var skeletons = reports
            .Where(x => !string.IsNullOrWhiteSpace(x.SkeletonHash))
            .GroupBy(x => x.SkeletonHash!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var modelRows = group
                    .Select(report => new
                    {
                        Report = report,
                        Catalog = modelsByOutput.TryGetValue(report.RelativePath.Replace('\\', '/'), out var catalog) ? catalog : null,
                    })
                    .ToArray();
                var skeletonPaths = modelRows
                    .Select(x => (string?)x.Catalog?["skeletonPath"])
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var primarySkeletonPath = skeletonPaths.FirstOrDefault();
                var skeletonSourceObjects = skeletonPaths
                    .SelectMany(path => sourceIndex.BonesBySkeleton.TryGetValue(path!, out var bones) ? bones : [])
                    .GroupBy(x => $"{x.SourcePath}|{x.OwnerObjectPath}|{x.OwnerType}", StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x.First().OwnerType, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.First().OwnerObjectPath, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new
                    {
                        sourcePath = x.First().SourcePath,
                        ownerObjectPath = x.First().OwnerObjectPath,
                        ownerType = x.First().OwnerType,
                        boneCount = x.Count(),
                    })
                    .ToArray();
                var animations = skeletonPaths
                    .SelectMany(path => animationsBySkeleton.TryGetValue(path!, out var rows) ? rows : [])
                    .OrderBy(x => (string?)x["output"], StringComparer.OrdinalIgnoreCase)
                    .Select(x => new
                    {
                        name = (string?)x["name"],
                        source = (string?)x["source"],
                        output = (string?)x["output"],
                        status = (string?)x["status"],
                        duration = (double?)x["duration"],
                        frameCount = (int?)x["frameCount"],
                        trackCount = (int?)x["trackCount"],
                    })
                    .ToArray();

                return new
                {
                    skeletonId = group.Key,
                    skeletonPath = primarySkeletonPath,
                    skeletonName = modelRows.Select(x => (string?)x.Catalog?["skeletonName"]).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                    skeletonPaths,
                    modelCount = group.Count(),
                    animationCount = animations.Length,
                    boneCount = group.First().BoneCount,
                    relationBasis = "glTF skin joints + UE Skeleton source reference",
                    sourceIndexAvailable = sourceIndex.Available,
                    skeletonSourceObjects,
                    models = modelRows
                    .OrderBy(x => x.Report.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new
                    {
                        name = x.Report.Name,
                        output = x.Report.RelativePath,
                        resourceKind = x.Report.ResourceKind,
                        source = (string?)x.Catalog?["source"],
                        objectPath = (string?)x.Catalog?["objectPath"],
                        skeletonPath = (string?)x.Catalog?["skeletonPath"],
                        skeletonName = (string?)x.Catalog?["skeletonName"],
                    })
                    .ToArray(),
                    animations,
                    boneNames = group.First().BoneNames.Take(256).ToArray(),
                    boneNamesTruncated = group.First().BoneNames.Length > 256,
                };
            })
            .ToArray();
        var skeletonArray = JArray.FromObject(skeletons);

        var path = Path.Combine(root, "skeletons.json");
        if (writeCompatibilityJson)
        {
            File.WriteAllText(
                path,
                new JObject
                {
                    ["generatedAt"] = DateTime.UtcNow.ToString("O"),
                    ["rule"] = "骨架分组以已导出 GLB/glTF skin joints 为预览基准，同时合并 UE Skeleton 原始路径、源索引骨架对象和同 Skeleton 动画列表。",
                    ["sourceIndex"] = JObject.FromObject(new
                    {
                        available = sourceIndex.Available,
                        path = sourceIndex.Available ? MakeRelative(root, sourceIndex.Path).Replace('\\', '/') : null,
                        error = sourceIndex.Error,
                    }),
                    ["skeletonCount"] = skeletons.Length,
                    ["skeletons"] = skeletonArray,
                }.ToString(Formatting.Indented),
                Encoding.UTF8);
        }
        else
        {
            DeleteIfExists(path);
        }

        return skeletonArray;
    }

    private static JObject WriteLibraryHealth(
        string root,
        List<JObject> catalogRows,
        List<ModelValidationEntry> reports,
        IReadOnlyList<TextureLinkInfo> textureLinks,
        MaterialTextureSlotLink[] materialTextureSlots,
        SharedGltfTextureLink[] sharedGltfTextureLinks,
        ComponentAssetRelationLink[] componentAssetRelations,
        SourcePackageObjectMap[] packageObjectMaps,
        JArray skeletonGroups,
        JObject modelAnimationRelations,
        JObject modelCoverage,
        AnimationValidationSummary animationValidation,
        SourceIndexSnapshot sourceIndex,
        bool writeCompatibilityJson)
    {
        var models = catalogRows.Where(x => string.Equals((string?)x["kind"], "Model", StringComparison.OrdinalIgnoreCase)).ToArray();
        var materials = catalogRows.Where(x => string.Equals((string?)x["kind"], "Material", StringComparison.OrdinalIgnoreCase)).ToArray();
        var textures = catalogRows.Where(x => string.Equals((string?)x["kind"], "Texture", StringComparison.OrdinalIgnoreCase)).ToArray();
        var animations = catalogRows.Where(x => string.Equals((string?)x["kind"], "Animation", StringComparison.OrdinalIgnoreCase)).ToArray();
        var componentGroups = componentAssetRelations.Length > MaxDetailedComponentGroupRelations
            ? Array.Empty<ComponentGroupRow>()
            : BuildComponentGroupRows(componentAssetRelations);
        var detailedComponentGroupsSkipped = componentAssetRelations.Length > MaxDetailedComponentGroupRelations;
        var animationRelations = (JArray?)modelAnimationRelations["relations"] ?? [];
        var matchedModelAnimationRelations = animationRelations.Count(x => ((JArray?)x["animations"] ?? []).Count > 0);
        var unmatchedMaterialSlots = materialTextureSlots.Count(x => !string.Equals(x.MatchStatus, "matched", StringComparison.OrdinalIgnoreCase));
        var actionableMissingMaterialSlots = materialTextureSlots.Count(x => string.Equals(x.MatchStatus, "missingExportedTexture", StringComparison.OrdinalIgnoreCase));
        var nonExportableMaterialSlots = materialTextureSlots.Count(x => string.Equals(x.MatchStatus, "nonExportableTexture", StringComparison.OrdinalIgnoreCase));
        var unresolvedMaterialSlots = materialTextureSlots.Count(x => string.Equals(x.MatchStatus, "unresolvedTexturePackage", StringComparison.OrdinalIgnoreCase));
        var missingComponentRefs = detailedComponentGroupsSkipped
            ? componentAssetRelations.Count(IsMissingAssetRelation)
            : componentGroups.Sum(x => x.MissingReferenceCount);
        var modelWarnings = reports.Count(x => string.Equals(x.Status, "warning", StringComparison.OrdinalIgnoreCase));
        var modelErrors = reports.Count(x => string.Equals(x.Status, "error", StringComparison.OrdinalIgnoreCase));
        var validationErrors = animationValidation.Validations.Count(x => string.Equals(x.Status, "error", StringComparison.OrdinalIgnoreCase));
        var actionableValidationErrors = animationValidation.Validations.Count(IsActionableAnimationValidationError);
        var fallbackDiagnosticValidationErrors = animationValidation.Validations.Count(IsFallbackDiagnosticAnimationValidationError);
        var validationWarnings = animationValidation.Validations.Count(x => string.Equals(x.Status, "warning", StringComparison.OrdinalIgnoreCase));
        var exportedAnimations = animations.Count(x => IsExportedAnimationFileAvailable(root, x));
        var metadataAnimations = animations.Count(x => string.Equals((string?)x["status"], "metadata", StringComparison.OrdinalIgnoreCase));
        var failedAnimations = animations.Count(x => string.Equals((string?)x["status"], "error", StringComparison.OrdinalIgnoreCase));
        var linkErrors = textureLinks.Count(x => !string.IsNullOrWhiteSpace(x.LinkError));
        var embeddedGltfImageCount = reports.Sum(x => x.EmbeddedImageCount);
        var modelsWithEmbeddedGltfImages = reports.Count(x => x.EmbeddedImageCount > 0);

        var healthStatus =
            modelErrors > 0 ? "error" :
            modelWarnings > 0 || missingComponentRefs > 0 || actionableMissingMaterialSlots > 0 || unresolvedMaterialSlots > 0 || linkErrors > 0 || actionableValidationErrors > 0 || validationWarnings > 0 ? "warning" :
            "ok";

        var issues = new JArray();
        if (!sourceIndex.Available)
            issues.Add(new JObject
            {
                ["level"] = "warning",
                ["area"] = "sourceIndex",
                ["message"] = "缺少 ue_source_index.db，部分 UE 原始关系、骨骼和动画验证无法完成。",
                ["detail"] = sourceIndex.Error,
            });
        if (modelErrors > 0)
            issues.Add(new JObject { ["level"] = "error", ["area"] = "models", ["message"] = $"有 {modelErrors} 个模型结构验证失败。" });
        if (modelWarnings > 0)
            issues.Add(new JObject { ["level"] = "warning", ["area"] = "models", ["message"] = $"有 {modelWarnings} 个模型存在结构或材质侧车验证警告。" });
        if (unmatchedMaterialSlots > 0)
            issues.Add(new JObject
            {
                ["level"] = actionableMissingMaterialSlots > 0 || unresolvedMaterialSlots > 0 ? "warning" : "info",
                ["area"] = "materials",
                ["message"] = $"有 {unmatchedMaterialSlots} 个材质贴图槽没有匹配到已导出贴图，其中普通贴图缺失 {actionableMissingMaterialSlots} 个、源包未定位 {unresolvedMaterialSlots} 个、暂不可按 PNG 导出 {nonExportableMaterialSlots} 个。",
            });
        if (missingComponentRefs > 0)
            issues.Add(new JObject { ["level"] = "warning", ["area"] = "components", ["message"] = $"有 {missingComponentRefs} 个蓝图/组件显式资源引用没有匹配到已导出素材。" });
        if (failedAnimations > 0)
            issues.Add(new JObject
            {
                ["level"] = "warning",
                ["area"] = "animations",
                ["message"] = $"有 {failedAnimations} 个动画没有成功导出为 .ueanim，已从默认模型动画候选中排除。",
            });
        if (actionableValidationErrors > 0 || validationWarnings > 0)
            issues.Add(new JObject
            {
                ["level"] = "warning",
                ["area"] = "animations",
                ["message"] = $"动画骨架验证存在 actionableError={actionableValidationErrors}, warning={validationWarnings}；actionable error 候选不会进入默认可用动画列表。",
            });
        if (fallbackDiagnosticValidationErrors > 0)
            issues.Add(new JObject
            {
                ["level"] = "info",
                ["area"] = "animations",
                ["message"] = $"另有 {fallbackDiagnosticValidationErrors} 个 sharedSkeleton 兼容候选缺少必要 track 骨骼，已保留在明细中但不会作为可用动画。",
            });
        if (linkErrors > 0)
            issues.Add(new JObject { ["level"] = "warning", ["area"] = "textures", ["message"] = $"有 {linkErrors} 个共享贴图硬链接创建失败。" });
        if (embeddedGltfImageCount > 0)
            issues.Add(new JObject
            {
                ["level"] = "info",
                ["area"] = "textures",
                ["message"] = $"有 {modelsWithEmbeddedGltfImages} 个 GLB 仍包含内嵌图片，共 {embeddedGltfImageCount} 张；独立贴图已进入 Textures/_Shared，后续可继续做 GLB 贴图外置化以减少模型文件体积。",
            });

        var health = new JObject
        {
            ["generatedAt"] = DateTime.UtcNow.ToString("O"),
            ["status"] = healthStatus,
            ["rule"] = "素材库健康度只统计 UnrealExporter 导出和源索引解析得到的事实，不按名称猜测模型、贴图、骨骼、动画关系。",
            ["sourceIndex"] = JObject.FromObject(new
            {
                available = sourceIndex.Available,
                path = sourceIndex.Available ? MakeRelative(root, sourceIndex.Path).Replace('\\', '/') : null,
                error = sourceIndex.Error,
            }),
            ["models"] = JObject.FromObject(new
            {
                total = models.Length,
                ok = reports.Count(x => string.Equals(x.Status, "ok", StringComparison.OrdinalIgnoreCase)),
                warning = reports.Count(x => string.Equals(x.Status, "warning", StringComparison.OrdinalIgnoreCase)),
                error = modelErrors,
                withSkin = reports.Count(x => x.SkinCount > 0),
                withSkeletonPath = models.Count(x => !string.IsNullOrWhiteSpace((string?)x["skeletonPath"])),
                withEmbeddedAnimation = reports.Count(x => x.AnimationCount > 0),
                missingMaterialSidecars = reports.Sum(x => x.MissingMaterialSidecars.Length),
                coverage = modelCoverage["totals"],
            }),
            ["textures"] = JObject.FromObject(new
            {
                catalogRows = textures.Length,
                scanned = textureLinks.Count,
                unique = textureLinks.Select(x => x.Hash).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                hardLinked = textureLinks.Count(x => x.HardLinked),
                linkErrors,
                embeddedGltfImages = embeddedGltfImageCount,
                modelsWithEmbeddedGltfImages,
                sharedGltfLinks = sharedGltfTextureLinks.Length,
                sharedGltfLinked = sharedGltfTextureLinks.Count(x => IsSharedGltfTextureLinkedStatus(x.Status)),
            }),
            ["materials"] = JObject.FromObject(new
            {
                total = materials.Length,
                textureSlots = materialTextureSlots.Length,
                matchedTextureSlots = materialTextureSlots.Count(x => string.Equals(x.MatchStatus, "matched", StringComparison.OrdinalIgnoreCase)),
                missingTextureSlots = unmatchedMaterialSlots,
                actionableMissingTextureSlots = actionableMissingMaterialSlots,
                unresolvedTextureSlots = unresolvedMaterialSlots,
                nonExportableTextureSlots = nonExportableMaterialSlots,
                missingCategories = materialTextureSlots
                    .Where(x => !string.IsNullOrWhiteSpace(x.MissingCategory))
                    .GroupBy(x => x.MissingCategory, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new { name = x.Key, count = x.Count() })
                    .ToArray(),
            }),
            ["components"] = JObject.FromObject(new
            {
                relationCount = componentAssetRelations.Length,
                groupCount = componentGroups.Length,
                detailedGroupsSkipped = detailedComponentGroupsSkipped,
                groupsWithMissingReferences = componentGroups.Count(x => x.MissingReferenceCount > 0),
                missingReferenceCount = missingComponentRefs,
                modelReferences = detailedComponentGroupsSkipped
                    ? componentAssetRelations.Count(x => IsModelRelation(x.RelationType))
                    : componentGroups.Sum(x => x.ModelReferenceCount),
                exportedModelReferences = detailedComponentGroupsSkipped
                    ? componentAssetRelations.Count(x => IsModelRelation(x.RelationType) && string.Equals(x.MatchStatus, "matched", StringComparison.OrdinalIgnoreCase))
                    : componentGroups.Sum(x => x.ExportedModelReferenceCount),
                modelReferenceStatus = BuildComponentRelationStatusSummary(componentAssetRelations, IsModelRelation),
                skeletonReferences = componentAssetRelations.Count(x => string.Equals(x.RelationType, "Skeleton", StringComparison.OrdinalIgnoreCase)),
                exportedSkeletonReferences = componentAssetRelations.Count(x =>
                    string.Equals(x.RelationType, "Skeleton", StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(x.MatchStatus, "matched", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(x.MatchStatus, "skeletonCoveredByModels", StringComparison.OrdinalIgnoreCase))),
                missingSkeletonReferences = componentAssetRelations.Count(x =>
                    string.Equals(x.RelationType, "Skeleton", StringComparison.OrdinalIgnoreCase) &&
                    IsMissingAssetRelation(x)),
                animationReferences = detailedComponentGroupsSkipped
                    ? componentAssetRelations.Count(x => IsAnimationRelation(x.RelationType))
                    : componentGroups.Sum(x => x.AnimationReferenceCount),
                exportedAnimationReferences = detailedComponentGroupsSkipped
                    ? componentAssetRelations.Count(x => IsAnimationRelation(x.RelationType) && string.Equals(x.MatchStatus, "matched", StringComparison.OrdinalIgnoreCase))
                    : componentGroups.Sum(x => x.ExportedAnimationReferenceCount),
                animationReferenceStatus = BuildComponentRelationStatusSummary(componentAssetRelations, IsAnimationRelation),
                materialReferences = componentGroups.Sum(x => x.MaterialReferenceCount),
                exportedMaterialReferences = componentGroups.Sum(x => x.ExportedMaterialReferenceCount),
                materialReferenceStatus = BuildComponentRelationStatusSummary(
                    componentAssetRelations,
                    relationType => string.Equals(relationType, "Material", StringComparison.OrdinalIgnoreCase)),
            }),
            ["skeletons"] = JObject.FromObject(new
            {
                groupCount = skeletonGroups.Count,
                groupsWithAnimations = skeletonGroups.Count(x => ((JArray?)x["animations"] ?? []).Count > 0),
                skeletonSourceObjects = skeletonGroups.Sum(x => ((JArray?)x["skeletonSourceObjects"] ?? []).Count),
            }),
            ["animations"] = JObject.FromObject(new
            {
                catalogRows = animations.Length,
                exported = exportedAnimations,
                metadata = metadataAnimations,
                failed = failedAnimations,
                relationModels = animationRelations.Count,
                matchedModels = matchedModelAnimationRelations,
                validationPairs = animationValidation.Validations.Length,
                validationOk = animationValidation.Validations.Count(x => string.Equals(x.Status, "ok", StringComparison.OrdinalIgnoreCase)),
                validationWarning = validationWarnings,
                validationError = validationErrors,
                actionableValidationError = actionableValidationErrors,
                fallbackDiagnosticValidationError = fallbackDiagnosticValidationErrors,
                containerAnimations = animationValidation.Validations.Count(x => x.IsContainerAnimation),
                validationCategories = animationValidation.Validations
                    .GroupBy(x => x.ValidationCategory, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(x => x.Count())
                    .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new { name = x.Key, count = x.Count() })
                    .ToArray(),
            }),
            ["sourceObjects"] = JObject.FromObject(new
            {
                packageObjectMaps = packageObjectMaps.Length,
                materialTextureSlots = sourceIndex.MaterialTextureSlots.Length,
                componentAssetRelations = sourceIndex.ComponentAssetRelationCount > 0
                    ? sourceIndex.ComponentAssetRelationCount
                    : sourceIndex.ComponentAssetRelations.Length,
            }),
            ["issues"] = issues,
        };

        var path = Path.Combine(root, "library_health.json");
        if (writeCompatibilityJson)
            File.WriteAllText(path, health.ToString(Formatting.Indented), Encoding.UTF8);
        else
            DeleteIfExists(path);

        return health;
    }

    private static object[] BuildComponentRelationStatusSummary(
        ComponentAssetRelationLink[] links,
        Func<string, bool> relationTypeFilter)
    {
        return links
            .Where(x => relationTypeFilter(x.RelationType))
            .GroupBy(x => string.IsNullOrWhiteSpace(x.MatchStatus) ? "unknown" : x.MatchStatus, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new
            {
                status = x.Key,
                count = x.Count(),
                exportedOrCovered = x.Count(y =>
                    string.Equals(y.MatchStatus, "matched", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(y.MatchStatus, "skeletonCoveredByModels", StringComparison.OrdinalIgnoreCase)),
            })
            .ToArray<object>();
    }

    private static bool IsActionableAnimationValidationError(AnimationValidationEntry validation)
        => string.Equals(validation.Status, "error", StringComparison.OrdinalIgnoreCase)
           && !IsFallbackDiagnosticAnimationValidationError(validation);

    private static bool IsFallbackDiagnosticAnimationValidationError(AnimationValidationEntry validation)
        => string.Equals(validation.Status, "error", StringComparison.OrdinalIgnoreCase)
           && string.Equals(validation.CandidateReason, "sharedSkeleton", StringComparison.OrdinalIgnoreCase);

    private static void WriteLibraryReadme(
        string root,
        List<ModelValidationEntry> reports,
        IEnumerable<MaterialInfo> materials,
        ComponentAssetRelationLink[] componentAssetRelations,
        SourcePackageObjectMap[] packageObjectMaps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# UE Asset Library");
        sb.AppendLine();
        sb.AppendLine("这份目录由 UnrealExporter 导出主链路和素材库索引步骤生成。当前阶段重点验证 GLB/glTF、材质 JSON、贴图硬链接、骨骼、动画和 UE 原始关系。");
        sb.AppendLine();
        sb.AppendLine("## 统计");
        sb.AppendLine();
        sb.AppendLine($"- 模型: `{reports.Count}`");
        sb.AppendLine($"- 带 skin/骨骼模型: `{reports.Count(x => x.SkinCount > 0)}`");
        sb.AppendLine($"- 材质 JSON: `{materials.Count()}`");
        sb.AppendLine($"- 模型内动画: `{reports.Count(x => x.AnimationCount > 0)}`");
        sb.AppendLine($"- 蓝图/组件资源关系: `{componentAssetRelations.Length}`");
        sb.AppendLine($"- UE 包 Import/Export 摘要: `{packageObjectMaps.Length}`（完整记录见 `ue_source_index.db`）");
        sb.AppendLine();
        sb.AppendLine("## 索引文件");
        sb.AppendLine();
        sb.AppendLine("| 文件 | 用途 |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine("| `library_index.db` | 浏览器和自动化脚本的主 SQLite 索引，包含资产、模型验证、贴图、材质、组件关系、骨架、动画关系、健康报告和验收状态。 |");
        sb.AppendLine("| `export_events.db` | 导出主流程实时写入的 SQLite 事件库，记录 export manifest、asset catalog、animation bindings 和自动补导诊断；后处理优先读取它。 |");
        sb.AppendLine("| `library_work.db` | 后处理工作 SQLite 库，用于承载不应再写成超大 JSONL 的流式中间关系；例如材质 sidecar 摘要、贴图去重关系、材质贴图槽、glTF 共享贴图改写关系和完整组件关系。 |");
        sb.AppendLine("| `ue_source_index.db` | 启用源索引时生成，记录源索引 resume/fingerprint 状态、完整源文件表、已检查对象、Import/Export、Skeleton/Material/Texture/Blueprint/Component 关系、骨骼层级、动画 track 和 Montage/Composite segment。 |");
        sb.AppendLine("| `asset_catalog.jsonl` | 兼容/人工排查视图；新导出以后同类数据以 `export_events.db.asset_catalog` 和 `library_index.db.assets` 为主。 |");
        sb.AppendLine("| `library_health.json` | 健康摘要兼容/人工排查视图；主数据在 `library_index.db.library_reports`。 |");
        sb.AppendLine("| `library_acceptance.json` | 验收摘要兼容/人工排查视图；主数据在 `library_index.db.library_reports`。 |");
        sb.AppendLine("| `texture_dedupe_summary.json` | 贴图去重摘要兼容/人工排查视图；主数据在 `library_index.db.library_reports` 的 `texture_dedupe` 记录。 |");
        sb.AppendLine("| `export_manifest.jsonl` | 兼容/人工排查视图；主数据在 `export_events.db.export_manifest` 和 `library_index.db.export_manifest`。 |");
        sb.AppendLine("| `auto_referenced_exports.jsonl` | 兼容/人工排查视图；主数据在 `export_events.db.auto_referenced_exports` 和 `library_index.db.auto_referenced_exports`。 |");
        sb.AppendLine("| `animation_bindings.jsonl` | 兼容/人工排查视图；主数据在 `export_events.db.animation_bindings` 和 `library_index.db.animation_bindings`。 |");
        sb.AppendLine("| `model_coverage.json` | 模型覆盖兼容/人工排查视图；主数据在 `library_index.db.model_coverage`。 |");
        sb.AppendLine("| `model_animations.json` | 模型动画关系兼容/人工排查视图；主数据在 `library_index.db.model_animation_relations` 和 `library_index.db.relation_animations`。 |");
        sb.AppendLine("| `animation_validation.json` | 动画兼容验证兼容/人工排查视图；主数据在 `library_index.db.animation_validation`。 |");
        sb.AppendLine("| `model_validation.json` | GLB/glTF 静态结构、材质、贴图、skin 验证兼容/人工排查视图；主数据在 `library_index.db.model_validation`。 |");
        sb.AppendLine("| `skeletons.json` | 骨架分组兼容/人工排查视图；主数据在 `library_index.db.skeleton_groups`。 |");
        sb.AppendLine("| `library_index.db.material_sidecars` | 材质 sidecar 摘要和原始参数缓存；新后处理优先通过 `export_events.db.asset_catalog` 定位材质 JSON，再缓存到 SQLite，避免全盘递归扫描 JSON。 |");
        sb.AppendLine("| `texture_links.jsonl` | 原贴图文件、共享贴图、sha256 和硬链接状态。 |");
        sb.AppendLine("| `material_texture_slots.jsonl` | 材质 slot 到 UE 贴图、导出贴图和共享贴图的对应关系。 |");
        sb.AppendLine("| `shared_texture_gltf_links.jsonl` | 文本 glTF image URI 改写到共享贴图的记录。 |");
        sb.AppendLine("| `component_asset_relations.jsonl` | 蓝图、组件、默认对象到模型/材质/动画/Skeleton 的显式 UE 关系。 |");
        sb.AppendLine("| `component_groups.json` | 按 owner 蓝图/组件聚合的组合模型与任务素材关系兼容/人工排查视图；主数据在 `library_index.db.component_groups`，完整逐行关系在 `library_index.db.component_asset_relations`。 |");
        sb.AppendLine("| `package_object_maps.jsonl` | UE 包 ImportMap/ExportMap 摘要；完整原始依赖和导出对象记录保留在 `ue_source_index.db`。 |");
        sb.AppendLine("| `Textures/_Shared` | 启用硬链接去重后生成的共享贴图库。 |");
        sb.AppendLine();
        sb.AppendLine("## 模型动画关系字段");
        sb.AppendLine();
        sb.AppendLine("浏览器和验收脚本优先读取 `library_index.db.relation_animations`；`model_animations.json` 只是显式请求兼容/调试 JSON 时才生成的人读视图，默认可不存在。默认可信动画请筛选 `recommended_use = 'defaultTrusted'`，不要只按 `validation_status = 'ok'` 或旧字段 `is_explicit_usage` 统计。");
        sb.AppendLine();
        sb.AppendLine("默认导出和后处理采用 SQLite-first：大型机器索引进入 SQLite，不再生成对应 JSON/JSONL 兼容视图；例如导出事件 JSONL、`asset_catalog.jsonl`、`package_object_maps.jsonl`、`texture_links.jsonl`、`texture_dedupe_summary.json`、`material_texture_slots.jsonl`、`shared_texture_gltf_links.jsonl`、`component_asset_relations.jsonl`、`component_groups.json`、`model_coverage.json`、`task_model_quality.json`、`animation_validation.jsonl`、`model_animations.json`、`model_validation.json`、`skeletons.json`、`library_health.json` 和 `library_acceptance.json` 会由 `export_events.db`、`ue_source_index.db`、`library_work.db` 和 `library_index.db` 承载；材质 sidecar 摘要会进入 `library_index.db.material_sidecars`，贴图去重摘要会进入 `library_index.db.library_reports`。只有显式请求兼容/调试 JSON 时才写这些人读视图，完整查询仍以 `library_index.db` 为准。");
        sb.AppendLine();
        sb.AppendLine("| 字段 | 用途 |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine("| `confidence_tier` / `confidenceTier` | 关系最高置信层级，例如 `ExplicitComponent`、`AnimBlueprintDirect`、`CharacterDataSet`、`UniqueSkeletonCompatible`、`SharedSkeletonCompatible`。 |");
        sb.AppendLine("| `relationship_kind` / `relationshipKind` | 关系大类：`deterministicUsage`、`contextualUsage`、`compatibilityCandidate`、`unknown`。 |");
        sb.AppendLine("| `recommended_use` / `recommendedUse` | 默认推荐状态：`defaultTrusted`、`compatibleCandidate`、`manualReview`、`compatibleNeedsReview`、`notUsable`。 |");
        sb.AppendLine("| `evidence_summary` | 证据链的简短文本摘要，供 UI 列表和详情直接显示。 |");
        sb.AppendLine("| `relation_animation_evidence` | 证据链明细表，按 `step_index` 保存组件、AnimBP、DataAsset、同 Skeleton、track 验证等确定性步骤。 |");
        sb.AppendLine("| `usage_evidence` / `usageEvidence` | 兼容旧报告的粗粒度证据字段，新 UI 不应只靠它判断可信度。 |");
        sb.AppendLine("| `is_deterministic_usage` / `isDeterministicUsage` | 是否来自确定性使用或强上下文证据。 |");
        sb.AppendLine("| `is_compatibility_candidate` / `isCompatibilityCandidate` | 是否只是同 Skeleton/唯一 Skeleton 兼容候选。 |");
        sb.AppendLine();
        sb.AppendLine("`library_index.db` 还会同步 `export_manifest.jsonl`、`animation_bindings.jsonl` 和 `auto_referenced_exports.jsonl` 到 `export_manifest`、`animation_bindings`、`auto_referenced_exports` 表，供浏览器直接查询导出来源、动画绑定和自动补导诊断。");
        sb.AppendLine();
        sb.AppendLine("## 下一步");
        sb.AppendLine();
        sb.AppendLine("- 增加动画采样预览验证，检查播放姿态、bbox 变化和异常骨骼变换。");
        sb.AppendLine("- 扩展 Montage/Composite segment 报告，保留 slot、section 和 segment 时间范围。");
        sb.AppendLine("- 为 Montage/Composite 自动选择已导出的 segment 子动画生成预览。");
        File.WriteAllText(Path.Combine(root, "LIBRARY_README.md"), sb.ToString(), Encoding.UTF8);
    }

    private static void WriteAssetReadme(string root, ModelValidationEntry report)
    {
        var dir = Path.GetDirectoryName(report.Path);
        if (string.IsNullOrWhiteSpace(dir))
            return;

        var path = Path.Combine(dir, "ASSET_README.md");
        if (File.Exists(path))
            return;

        var sb = new StringBuilder();
        sb.AppendLine($"# {report.Name}");
        sb.AppendLine();
        sb.AppendLine("- 类型: `" + report.ResourceKind + "`");
        sb.AppendLine("- 格式: `glb`");
        sb.AppendLine("- Mesh: `" + report.MeshCount + "`");
        sb.AppendLine("- 材质: `" + report.MaterialCount + "`");
        sb.AppendLine("- 贴图: `" + report.ImageCount + "`");
        sb.AppendLine("- Skin: `" + report.SkinCount + "`");
        sb.AppendLine("- 骨骼: `" + report.BoneCount + "`");
        sb.AppendLine("- 动画: `" + report.AnimationCount + "`");
        if (!string.IsNullOrWhiteSpace(report.SkeletonHash))
            sb.AppendLine("- SkeletonHash: `" + report.SkeletonHash + "`");
        sb.AppendLine();
        sb.AppendLine("机器索引以素材库根目录的 `library_index.db` 为准；JSON/JSONL 文件只是兼容或人工排查视图，SQLite-only 导出可不生成。");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    public static void DeduplicateTextureFiles(string root, bool writeCompatibilityJson = false)
    {
        DeduplicateTextureFilesCore(root, writeCompatibilityJson);
    }

    private static List<TextureLinkInfo> LoadExistingTextureLinks(string root)
    {
        var workRows = LoadExistingTextureLinksFromWorkDb(root);
        if (workRows.Count > 0)
            return workRows;

        var path = Path.Combine(root, "texture_links.jsonl");
        if (!File.Exists(path))
            return [];

        var result = new List<TextureLinkInfo>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var row = JObject.Parse(line);
                var source = (string?)row["source"];
                var shared = (string?)row["shared"];
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(shared))
                    continue;

                result.Add(new TextureLinkInfo
                {
                    Path = Path.Combine(root, source.Replace('/', Path.DirectorySeparatorChar)),
                    RelativePath = source,
                    SharedPath = Path.Combine(root, shared.Replace('/', Path.DirectorySeparatorChar)),
                    SharedRelativePath = shared,
                    Hash = (string?)row["sha256"] ?? "",
                    SizeBytes = (long?)row["sizeBytes"] ?? 0,
                    Extension = (string?)row["extension"] ?? Path.GetExtension(source),
                    HardLinked = (bool?)row["hardLinked"] ?? false,
                    LinkError = (string?)row["linkError"] ?? "",
                });
            }
            catch (JsonException)
            {
                continue;
            }
        }

        return result;
    }

    private static List<TextureLinkInfo> LoadExistingTextureLinksFromWorkDb(string root)
    {
        using var workConnection = OpenLibraryWorkDbReadOnly(root);
        if (workConnection == null || !TableExists(workConnection, "texture_links") || CountTableRows(workConnection, "texture_links") == 0)
            return [];

        var result = new List<TextureLinkInfo>();
        using var command = workConnection.CreateCommand();
        command.CommandText = """
            SELECT source, shared, sha256, size_bytes, extension, hard_linked, link_error
            FROM texture_links
            ORDER BY id;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var source = GetString(reader, 0) ?? "";
            var shared = GetString(reader, 1) ?? "";
            result.Add(new TextureLinkInfo
            {
                Path = Path.Combine(root, source.Replace('/', Path.DirectorySeparatorChar)),
                RelativePath = source,
                SharedPath = Path.Combine(root, shared.Replace('/', Path.DirectorySeparatorChar)),
                SharedRelativePath = shared,
                Hash = GetString(reader, 2) ?? "",
                SizeBytes = reader.GetInt64(3),
                Extension = GetString(reader, 4) ?? "",
                HardLinked = !reader.IsDBNull(5) && reader.GetInt32(5) != 0,
                LinkError = GetString(reader, 6),
            });
        }

        return result;
    }

    private static TextureDedupeResult DeduplicateTextureFilesCore(string root, bool writeCompatibilityJson)
    {
        var textureFiles = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(x => TextureExtensions.Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase))
            .Where(x => IsAssetTextureFile(root, x))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sharedRoot = Path.Combine(root, "Textures", "_Shared");
        Directory.CreateDirectory(sharedRoot);

        var byHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<TextureLinkInfo>(textureFiles.Length);
        var linked = 0;
        var copied = 0;
        foreach (var path in textureFiles)
        {
            var hash = HashFile(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var sizeBytes = new FileInfo(path).Length;
            var sharedPath = Path.Combine(sharedRoot, hash[..2], $"{hash}{ext}");
            if (!byHash.TryGetValue(hash + ext, out var canonical))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(sharedPath)!);
                if (!File.Exists(sharedPath))
                {
                    File.Copy(path, sharedPath);
                    copied++;
                }
                byHash[hash + ext] = sharedPath;
                canonical = sharedPath;
            }

            var linkError = string.Empty;
            var hardLinked = TryReplaceWithHardLink(path, canonical, out linkError);
            if (hardLinked)
                linked++;

            links.Add(new TextureLinkInfo
            {
                Path = path,
                RelativePath = MakeRelative(root, path),
                SharedPath = canonical,
                SharedRelativePath = MakeRelative(root, canonical),
                Extension = ext,
                Hash = hash,
                SizeBytes = sizeBytes,
                HardLinked = hardLinked,
                LinkError = string.IsNullOrWhiteSpace(linkError) ? null : linkError,
            });
        }

        WriteTextureLinks(root, links, writeCompatibilityJson);
        var removedStaleSharedFiles = RemoveUnreferencedSharedTextures(root, sharedRoot, links);
        var summary = JObject.FromObject(new
        {
            generatedAt = DateTime.UtcNow.ToString("O"),
            status = "ok",
            rule = "素材 PNG/HDR 统一复制到 Textures/_Shared，再把原素材文件替换为硬链接；缓存缩略图和浏览器临时目录不会进入素材贴图索引。",
            scanned = textureFiles.Length,
            unique = byHash.Count,
            copiedToShared = copied,
            hardLinkedFiles = linked,
            sharedTextureFiles = links.Select(x => x.SharedRelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            removedStaleSharedFiles,
            note = "所有素材 PNG/HDR 文件都会尽量替换为指向 Textures/_Shared 的硬链接；GLB 保持独立预览，文本 glTF 可通过 library_index.db.shared_gltf_texture_links 追踪共享贴图改写。",
        });
        WriteLibraryWorkReport(root, "texture_dedupe", summary);
        var summaryPath = Path.Combine(root, "texture_dedupe_summary.json");
        if (writeCompatibilityJson)
        {
            File.WriteAllText(summaryPath, summary.ToString(Formatting.Indented), Encoding.UTF8);
        }
        else
        {
            DeleteIfExists(summaryPath);
        }
        Console.WriteLine($"Texture dedupe finished: scanned={textureFiles.Length}, unique={byHash.Count}, linked={linked}");
        return new TextureDedupeResult(links, summary);
    }

    private static bool IsAssetTextureFile(string root, string path)
    {
        var relative = MakeRelative(root, path).Replace('\\', '/');
        if (relative.StartsWith("Textures/_Shared/", StringComparison.OrdinalIgnoreCase))
            return false;

        // 浏览器缓存、动画预览缓存和其它点号目录不是素材本体，不能污染可用素材库贴图索引。
        var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return !parts.Any(part => part.StartsWith(".", StringComparison.Ordinal));
    }

    private static int RemoveUnreferencedSharedTextures(string root, string sharedRoot, List<TextureLinkInfo> links)
    {
        if (!Directory.Exists(sharedRoot))
            return 0;

        var referenced = links
            .Select(x => Path.GetFullPath(x.SharedPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(sharedRoot, "*.*", SearchOption.AllDirectories).ToArray())
        {
            if (referenced.Contains(Path.GetFullPath(path)))
                continue;

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARN: stale shared texture cleanup failed {MakeRelative(root, path)} ({ex.Message})");
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(sharedRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(x => x.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch
            {
                // 空目录清理失败不影响素材关系，下一次后处理还会重试。
            }
        }

        return removed;
    }

    private static void WriteTextureLinks(string root, List<TextureLinkInfo> links, bool writeCompatibilityJson)
    {
        var path = Path.Combine(root, "texture_links.jsonl");
        if (!writeCompatibilityJson)
            DeleteIfExists(path);

        using var workConnection = writeCompatibilityJson ? null : OpenLibraryWorkDb(root, "texture_links");
        using var workTransaction = workConnection?.BeginTransaction();
        using (var writer = writeCompatibilityJson ? new StreamWriter(path, false, Encoding.UTF8) : null)
        {
            foreach (var link in links.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                if (writeCompatibilityJson)
                {
                    writer!.WriteLine(JsonConvert.SerializeObject(new
                    {
                        kind = "TextureLink",
                        source = link.RelativePath,
                        shared = link.SharedRelativePath,
                        sha256 = link.Hash,
                        sizeBytes = link.SizeBytes,
                        extension = link.Extension,
                        hardLinked = link.HardLinked,
                        linkError = link.LinkError,
                    }));
                }
                else
                {
                    InsertTextureLink(workConnection!, workTransaction!, link);
                }
            }
        }

        workTransaction?.Commit();
        FinalizeWorkDb(workConnection);
    }

    private static bool TryReplaceWithHardLink(string path, string canonical, out string linkError)
    {
        linkError = string.Empty;
        var temp = path + ".dedupe.tmp";
        try
        {
            if (File.Exists(temp))
                File.Delete(temp);
            File.Move(path, temp);
            if (!CreateHardLinkW(path, canonical, IntPtr.Zero))
                throw new IOException($"CreateHardLinkW failed with Win32 error {Marshal.GetLastWin32Error()}.");
            File.Delete(temp);
            return true;
        }
        catch (Exception ex)
        {
            try
            {
                if (!File.Exists(path) && File.Exists(temp))
                    File.Move(temp, path);
                else if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch
            {
                // 回滚失败时保留原错误，人工可通过 .dedupe.tmp 排查。
            }

            Console.WriteLine($"WARN: hardlink failed {path} -> {canonical} ({ex.Message})");
            linkError = ex.Message;
            return false;
        }
    }

    private static string InferResourceKind(string path)
    {
        var text = path.Replace('\\', '/').ToLowerInvariant();
        if (text.Contains("/weapon") || text.Contains("/weapons/") || text.Contains("/gadgets/") ||
            text.Contains("/grappling/") || text.Contains("/grapplegun/"))
            return "Weapon";
        if (text.Contains("/characters/item/") || text.Contains("/characters/items/") ||
            text.Contains("/characters/props/") || text.Contains("/characters/prop/"))
            return "Prop";
        if (text.Contains("/characters/") || text.Contains("/character/"))
            return "Character";
        if (IsTaskOrPropLikePath(text))
            return "Prop";
        if (text.Contains("/environment/") || text.Contains("/scenery/") || text.Contains("/building/") || text.Contains("/plants/"))
            return "Environment";
        if (text.Contains("/vehicle") || text.Contains("/vehicles/"))
            return "Vehicle";
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

    private static string MakeRelative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static string HashBytes(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }

    private static string ComputeHash(string text)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private sealed class MaterialInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public int TextureSlotCount { get; set; }
        public int ColorCount { get; set; }
        public int ScalarCount { get; set; }
        public int SwitchCount { get; set; }
        public string? BlendMode { get; set; }
        public string? ShadingModel { get; set; }
        public JObject RawJson { get; set; } = new();
    }

    private sealed class TextureLinkInfo
    {
        public string Path { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string SharedPath { get; set; } = string.Empty;
        public string SharedRelativePath { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public bool HardLinked { get; set; }
        public string? LinkError { get; set; }
    }

    private sealed record TextureDedupeResult(List<TextureLinkInfo> Links, JObject Summary);

    private sealed record TextureLinkLookup(
        Dictionary<string, TextureLinkInfo[]> ByPackageSuffix,
        Dictionary<string, TextureLinkInfo[]> ByFileName);

    private sealed class ModelValidationEntry
    {
        public string Status { get; set; } = "unknown";
        public string Path { get; set; } = string.Empty;
        public string RelativePath { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ResourceKind { get; set; } = "Unknown";
        public int NodeCount { get; set; }
        public int MeshCount { get; set; }
        public int SkinCount { get; set; }
        public int MaterialCount { get; set; }
        public int ImageCount { get; set; }
        public int EmbeddedImageCount { get; set; }
        public int AnimationCount { get; set; }
        public int BoneCount { get; set; }
        public string[] BoneNames { get; set; } = [];
        public string? SkeletonHash { get; set; }
        public string[] MaterialNames { get; set; } = [];
        public string[] MatchedMaterialSidecars { get; set; } = [];
        public string[] MissingMaterialSidecars { get; set; } = [];
        public string[] ExternalMaterialNames { get; set; } = [];
        public int ExternalMaterialTextureCount { get; set; }
        public object? BBox { get; set; }
        public string[] Notes { get; set; } = [];
    }

    private sealed class SourceIndexSnapshot
    {
        public string Path { get; set; } = string.Empty;
        public bool Available { get; set; }
        public string? Error { get; set; }
        public Dictionary<string, SourceBone[]> BonesByOwner { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SourceBone[]> BonesBySkeleton { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SourceAnimationTrack[]> TracksByAnimation { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SourceAnimationSegment[]> SegmentsByAnimation { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public SourceMaterialTextureSlot[] MaterialTextureSlots { get; set; } = [];
        public SourceComponentAssetRelation[] ComponentAssetRelations { get; set; } = [];
        public int ComponentAssetRelationCount { get; set; }
        public SourceAssetRegistryDependency[] AssetRegistryDependencies { get; set; } = [];
        public SourceAnimBlueprintAnimationRef[] AnimBlueprintAnimationRefs { get; set; } = [];
        public HashSet<string> UnsupportedAnimationObjectPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> UnsupportedAutoReferencedAnimationPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public SourcePackageObjectMap[] PackageObjectMaps { get; set; } = [];
        public int PackageObjectMapCount { get; set; }
    }

    private sealed class SourceBone
    {
        public string? SourcePath { get; set; }
        public string OwnerObjectPath { get; set; } = string.Empty;
        public string OwnerType { get; set; } = string.Empty;
        public string? SkeletonPath { get; set; }
        public int BoneIndex { get; set; }
        public string BoneName { get; set; } = string.Empty;
        public int ParentIndex { get; set; }
    }

    private sealed class SourceAnimationTrack
    {
        public string? SourcePath { get; set; }
        public string AnimationObjectPath { get; set; } = string.Empty;
        public string? SkeletonPath { get; set; }
        public int TrackIndex { get; set; }
        public int BoneIndex { get; set; }
        public string? BoneName { get; set; }
        public bool FromExportedUEAnim { get; set; }
    }

    private sealed class SourceAnimationSegment
    {
        public string? SourcePath { get; set; }
        public string AnimationObjectPath { get; set; } = string.Empty;
        public string? SkeletonPath { get; set; }
        public int SegmentIndex { get; set; }
        public string? SlotName { get; set; }
        public string? ReferencedAnimationPath { get; set; }
        public string? ReferencedAnimationName { get; set; }
        public double? StartPos { get; set; }
        public double? AnimStartTime { get; set; }
        public double? AnimEndTime { get; set; }
        public double? PlayRate { get; set; }
        public int? LoopingCount { get; set; }
        public double? Length { get; set; }
        public string RelationSource { get; set; } = string.Empty;
    }

    private sealed class SourceMaterialTextureSlot
    {
        public string? SourcePath { get; set; }
        public string? MaterialObjectPath { get; set; }
        public string? MaterialName { get; set; }
        public string? SlotName { get; set; }
        public string? TexturePath { get; set; }
        public string? TextureName { get; set; }
        public string? TextureObjectPath { get; set; }
        public string? TextureClassName { get; set; }
        public string? TextureClassPath { get; set; }
        public string RelationSource { get; set; } = string.Empty;
    }

    private sealed class SourceComponentAssetRelation
    {
        public string? SourcePath { get; set; }
        public string? OwnerObjectPath { get; set; }
        public string? OwnerType { get; set; }
        public string? ComponentObjectPath { get; set; }
        public string? ComponentType { get; set; }
        public string? ComponentName { get; set; }
        public string? ComponentVariableName { get; set; }
        public string RelationSource { get; set; } = string.Empty;
        public string RelationType { get; set; } = string.Empty;
        public string? TargetPath { get; set; }
        public string? TargetName { get; set; }
        public string? SocketName { get; set; }
        public string? ParentComponentPath { get; set; }
        public double? LocationX { get; set; }
        public double? LocationY { get; set; }
        public double? LocationZ { get; set; }
        public double? RotationPitch { get; set; }
        public double? RotationYaw { get; set; }
        public double? RotationRoll { get; set; }
        public double? ScaleX { get; set; }
        public double? ScaleY { get; set; }
        public double? ScaleZ { get; set; }
    }

    private sealed class SourceAssetRegistryDependency
    {
        public string? RegistryPath { get; set; }
        public string SourcePackage { get; set; } = string.Empty;
        public string? SourceAssetName { get; set; }
        public string? SourceAssetClass { get; set; }
        public string? SourceIdentifier { get; set; }
        public string DependencyPackage { get; set; } = string.Empty;
        public string? DependencyAssetName { get; set; }
        public string? DependencyAssetClass { get; set; }
        public string? DependencyIdentifier { get; set; }
        public string DependencyCategory { get; set; } = string.Empty;
        public string DependencyKind { get; set; } = string.Empty;
        public int? DependencyFlags { get; set; }
        public string? DependencyFlagsText { get; set; }
        public bool IsHardPackage { get; set; }
        public bool IsSoftPackage { get; set; }
        public bool IsNameDependency { get; set; }
        public bool IsManageDependency { get; set; }
        public string RelationSource { get; set; } = string.Empty;
        public string RawJson { get; set; } = "{}";
    }

    private sealed class SourceAnimBlueprintAnimationRef
    {
        public string? SourcePath { get; set; }
        public string AnimBlueprintObjectPath { get; set; } = string.Empty;
        public string GeneratedClassObjectPath { get; set; } = string.Empty;
        public string ReferencedAnimationPath { get; set; } = string.Empty;
        public string? ReferencedAnimationName { get; set; }
        public string? ReferencedAnimationType { get; set; }
        public string? PropertyPath { get; set; }
        public string? NodeType { get; set; }
        public string? SkeletonPath { get; set; }
        public string RelationSource { get; set; } = string.Empty;
        public string ConfidenceHint { get; set; } = string.Empty;
        public string RawJson { get; set; } = "{}";
    }

    private sealed class SourcePackageObjectMap
    {
        public string? SourcePath { get; set; }
        public string? PackageName { get; set; }
        public string MapType { get; set; } = string.Empty;
        public int MapIndex { get; set; }
        public string? ObjectName { get; set; }
        public string? ObjectPath { get; set; }
        public string? ClassName { get; set; }
        public string? ClassPath { get; set; }
        public string? OuterPath { get; set; }
        public string? SuperPath { get; set; }
        public string? TemplatePath { get; set; }
        public string? TargetPackage { get; set; }
        public bool? IsAsset { get; set; }
        public bool? IsOptional { get; set; }
        public string? ObjectFlags { get; set; }
        public long? SerialSize { get; set; }
        public string? PublicExportHash { get; set; }
        public string RawJson { get; set; } = "{}";
    }

    private sealed record ExportedAssetLookup(
        Dictionary<string, JObject> ByObjectPath,
        Dictionary<string, JObject> ByRelativeSuffix,
        Dictionary<string, JObject[]> BySkeletonPath);

    private sealed class MaterialTextureSlotLink
    {
        public string MaterialName { get; set; } = string.Empty;
        public string? MaterialPath { get; set; }
        public string? MaterialObjectPath { get; set; }
        public string SlotName { get; set; } = string.Empty;
        public string? TextureName { get; set; }
        public string? TextureObjectPath { get; set; }
        public string? TexturePath { get; set; }
        public string? TextureClassName { get; set; }
        public string? TextureClassPath { get; set; }
        public string? MissingCategory { get; set; }
        public string? ExportedTexture { get; set; }
        public string? SharedTexture { get; set; }
        public string? Sha256 { get; set; }
        public bool? HardLinked { get; set; }
        public string MatchStatus { get; set; } = string.Empty;
        public string? MatchReason { get; set; }
        public string RelationSource { get; set; } = string.Empty;
    }

    private sealed class SharedGltfTextureLink
    {
        public string Model { get; set; } = string.Empty;
        public string MaterialName { get; set; } = string.Empty;
        public string Semantic { get; set; } = string.Empty;
        public string SlotName { get; set; } = string.Empty;
        public string? TextureName { get; set; }
        public int? ImageIndex { get; set; }
        public string? SharedTexture { get; set; }
        public string? Sha256 { get; set; }
        public string? Uri { get; set; }
        public bool RemovedBufferView { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? Reason { get; set; }
    }

    private sealed class ComponentAssetRelationLink
    {
        public string? SourcePath { get; set; }
        public string? OwnerObjectPath { get; set; }
        public string? OwnerType { get; set; }
        public string? ComponentObjectPath { get; set; }
        public string? ComponentType { get; set; }
        public string? ComponentName { get; set; }
        public string? ComponentVariableName { get; set; }
        public string RelationSource { get; set; } = string.Empty;
        public string RelationType { get; set; } = string.Empty;
        public string? TargetPath { get; set; }
        public string? TargetName { get; set; }
        public string? TargetAssetName { get; set; }
        public string? TargetAssetKind { get; set; }
        public string? TargetAssetOutput { get; set; }
        public string MatchStatus { get; set; } = string.Empty;
        public string? MatchReason { get; set; }
        public string? SocketName { get; set; }
        public string? ParentComponentPath { get; set; }
        public object? Transform { get; set; }
    }

    private sealed class ComponentGroupRow
    {
        public string OwnerObjectPath { get; set; } = string.Empty;
        public string? OwnerType { get; set; }
        public string? SourcePath { get; set; }
        public int RelationCount { get; set; }
        public int ComponentCount { get; set; }
        public int ModelReferenceCount { get; set; }
        public int ExportedModelReferenceCount { get; set; }
        public int MissingModelReferenceCount { get; set; }
        public int AnimationReferenceCount { get; set; }
        public int ExportedAnimationReferenceCount { get; set; }
        public int MissingAnimationReferenceCount { get; set; }
        public int MaterialReferenceCount { get; set; }
        public int ExportedMaterialReferenceCount { get; set; }
        public int MissingMaterialReferenceCount { get; set; }
        public int MissingReferenceCount { get; set; }
        public string RawJson { get; set; } = string.Empty;
    }

    private sealed class ModelBoneLookup
    {
        public ModelBoneLookup(SourceBone[] bones)
        {
            Bones = bones;
            ByName = bones
                .Where(x => !string.IsNullOrWhiteSpace(x.BoneName))
                .GroupBy(x => x.BoneName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            ByIndex = bones
                .GroupBy(x => x.BoneIndex)
                .ToDictionary(x => x.Key, x => x.First());
        }

        public SourceBone[] Bones { get; }
        public Dictionary<string, SourceBone> ByName { get; }
        private Dictionary<int, SourceBone> ByIndex { get; }

        public string? GetParentName(SourceBone bone)
        {
            if (bone.ParentIndex < 0)
                return null;
            return ByIndex.TryGetValue(bone.ParentIndex, out var parent) ? parent.BoneName : null;
        }
    }

    private sealed class AnimationValidationSummary
    {
        public bool SourceIndexAvailable { get; set; }
        public string? SourceIndexError { get; set; }
        public AnimationValidationEntry[] Validations { get; set; } = [];
        public Dictionary<string, AnimationValidationEntry> ByPairKey { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record ModelAnimationCandidate(JObject Model, JObject Animation, string Reason);

    private sealed class AnimationMetadataMaterializeResult
    {
        public AnimationMetadataMaterializeSummary Primary { get; set; } = new();
        public AnimationMetadataMaterializeSummary? CompatibilityJsonl { get; set; }
    }

    private sealed class AnimationMetadataMaterializeSummary
    {
        public string Backend { get; set; } = "jsonl";
        public string? Table { get; set; }
        public string Path { get; set; } = string.Empty;
        public string? SkippedReason { get; set; }
        public int Rows { get; set; }
        public int Materialized { get; set; }
        public int JsonSidecarsWritten { get; set; }
        public int MissingOutput { get; set; }
        public int JsonErrors { get; set; }
    }

    private sealed class ModelCoverageRow
    {
        public string Name { get; set; } = string.Empty;
        public string Output { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string? ObjectPath { get; set; }
        public string ResourceKind { get; set; } = "Unknown";
        public string SourceType { get; set; } = string.Empty;
        public string ValidationStatus { get; set; } = "unknown";
        public bool IsStatic { get; set; }
        public bool HasSkin { get; set; }
        public bool HasSkeletonPath { get; set; }
        public int MaterialCount { get; set; }
        public int TextureCount { get; set; }
        public int ComponentReferenceCount { get; set; }
        public int SourceIndexObjectCount { get; set; }
        public int AnimationCandidateCount { get; set; }
        public string[] TaskSignals { get; set; } = [];
    }

    private sealed class AnimationValidationEntry
    {
        public string PairKey { get; set; } = string.Empty;
        public string CandidateReason { get; set; } = string.Empty;
        public string Status { get; set; } = "unknown";
        public string ValidationCategory { get; set; } = "unknown";
        public string Reason { get; set; } = string.Empty;
        public string ModelOutput { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public string ModelSource { get; set; } = string.Empty;
        public string AnimationOutput { get; set; } = string.Empty;
        public string AnimationName { get; set; } = string.Empty;
        public string AnimationSource { get; set; } = string.Empty;
        public string? SkeletonPath { get; set; }
        public string? SkeletonName { get; set; }
        public int ModelBoneCount { get; set; }
        public int AnimationTrackCount { get; set; }
        public string TrackSource { get; set; } = "none";
        public int MatchedTrackBones { get; set; }
        public string[] MissingTrackBones { get; set; } = [];
        public double TrackCoverage { get; set; }
        public bool HierarchyCompatible { get; set; }
        public bool IsContainerAnimation { get; set; }
        public string[] ReferencedAnimations { get; set; } = [];
        public string[] ExportedReferencedAnimations { get; set; } = [];
        public string[] MissingReferencedAnimations { get; set; } = [];
        public string[] HierarchyMismatches { get; set; } = [];
    }
}
