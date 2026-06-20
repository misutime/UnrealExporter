using Microsoft.Data.Sqlite;

namespace UE5LibraryBrowser;

internal static class UeLibraryIndexReader
{
    public static UeLibraryIndex Load(string root)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"素材库目录不存在: {root}");

        var dbPath = Path.Combine(root, "library_index.db");
        if (!File.Exists(dbPath))
            throw new FileNotFoundException("没有找到 library_index.db。请先运行 UnrealExporter 的素材库导出或 --postprocess-library。", dbPath);

        SQLitePCL.Batteries_V2.Init();
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        RequireTable(connection, "assets");
        RequireTable(connection, "model_animation_relations");
        RequireTable(connection, "relation_animations");

        var animationsByModel = LoadAnimations(root, connection);
        var models = LoadModels(root, connection, animationsByModel);

        return new UeLibraryIndex
        {
            Root = root,
            Models = models,
            AnimationsByModel = animationsByModel
        };
    }

    private static List<UeLibraryModel> LoadModels(
        string root,
        SqliteConnection connection,
        Dictionary<string, List<UeLibraryAnimation>> animationsByModel)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.name,
                   a.output,
                   a.source,
                   a.source_type,
                   a.resource_kind,
                   COALESCE(a.skeleton_path, json_extract(a.raw_json, '$.skeletonPath')),
                   COALESCE(a.skeleton_name, json_extract(a.raw_json, '$.skeletonName')),
                   COALESCE(mv.bone_count, json_extract(a.raw_json, '$.boneCount')),
                   COALESCE(mc.material_count, mv.material_count, json_extract(a.raw_json, '$.materialCount')),
                   COALESCE(mc.has_skin, CASE WHEN mv.skin_count > 0 THEN 1 ELSE 0 END, json_extract(a.raw_json, '$.hasSkin')),
                   COALESCE(mc.validation_status, mv.status, a.validation_status, ''),
                   COALESCE(mar.confidence, ''),
                   COALESCE(mar.animation_count, 0)
            FROM assets a
            LEFT JOIN model_validation mv ON mv.path = a.output
            LEFT JOIN model_coverage mc ON mc.output = a.output
            LEFT JOIN model_animation_relations mar ON mar.model = a.output
            WHERE a.kind = 'Model'
              AND a.output IS NOT NULL
            ORDER BY COALESCE(mar.animation_count, 0) DESC, a.name COLLATE NOCASE;
            """;

        var models = new List<UeLibraryModel>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var output = ResolveLibraryPath(root, ReadString(reader, 1));
            if (string.IsNullOrWhiteSpace(output) || !File.Exists(output))
                continue;

            var relationKey = MakeLibraryRelative(root, output);
            animationsByModel.TryGetValue(relationKey, out var animations);
            var usable = animations?.Count(x => x.IsPreviewable) ?? 0;
            var reportedCount = ReadInt32(reader, 12);

            models.Add(new UeLibraryModel
            {
                Name = ReadString(reader, 0) ?? Path.GetFileNameWithoutExtension(output),
                Output = output,
                Source = ReadString(reader, 2) ?? "",
                SourceType = ReadString(reader, 3) ?? "",
                ResourceKind = ReadString(reader, 4) ?? "",
                SkeletonPath = ReadString(reader, 5) ?? "",
                SkeletonName = ReadString(reader, 6) ?? "",
                BoneCount = ReadInt32(reader, 7),
                MaterialCount = ReadInt32(reader, 8),
                HasSkin = ReadBool(reader, 9),
                ValidationStatus = ReadString(reader, 10) ?? "",
                Confidence = ReadString(reader, 11) ?? "",
                AnimationCount = Math.Max(reportedCount, animations?.Count ?? 0),
                UsableAnimationCount = usable
            });
        }

        return models;
    }

    private static Dictionary<string, List<UeLibraryAnimation>> LoadAnimations(string root, SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT mar.model,
                   ra.name,
                   ra.output,
                   ra.source,
                   ra.status,
                   ra.relation_source,
                   ra.validation_status,
                   ra.validation_category,
                   ra.validation_reason,
                   COALESCE(ra.duration, 0),
                   COALESCE(ra.frame_count, 0),
                   COALESCE(ra.track_count, 0),
                   COALESCE(ra.track_coverage, 0),
                   ra.hierarchy_compatible,
                   ra.is_container_animation,
                   ra.is_usable_candidate
            FROM relation_animations ra
            JOIN model_animation_relations mar ON mar.id = ra.relation_id
            ORDER BY mar.model COLLATE NOCASE, ra.is_usable_candidate DESC, ra.name COLLATE NOCASE;
            """;

        var result = new Dictionary<string, List<UeLibraryAnimation>>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var model = NormalizeLibraryKey(ReadString(reader, 0));
            if (string.IsNullOrWhiteSpace(model))
                continue;

            var output = ResolveLibraryPath(root, ReadString(reader, 2));
            var animation = new UeLibraryAnimation
            {
                Name = ReadString(reader, 1) ?? Path.GetFileNameWithoutExtension(output),
                Output = output,
                Source = ReadString(reader, 3) ?? "",
                Status = ReadString(reader, 4) ?? "",
                RelationSource = ReadString(reader, 5) ?? "",
                ValidationStatus = ReadString(reader, 6) ?? "",
                ValidationCategory = ReadString(reader, 7) ?? "",
                ValidationReason = ReadString(reader, 8) ?? "",
                Duration = ReadDouble(reader, 9),
                FrameCount = ReadInt32(reader, 10),
                TrackCount = ReadInt32(reader, 11),
                TrackCoverage = ReadDouble(reader, 12),
                HierarchyCompatible = ReadBool(reader, 13),
                IsContainerAnimation = ReadBool(reader, 14),
                IsUsableCandidate = ReadBool(reader, 15)
            };

            if (!result.TryGetValue(model, out var list))
            {
                list = [];
                result[model] = list;
            }

            list.Add(animation);
        }

        return result;
    }

    private static void RequireTable(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", tableName);
        if (command.ExecuteScalar() == null)
            throw new InvalidDataException($"library_index.db 缺少表 {tableName}，请重新后处理素材库。");
    }

    public static string MakeLibraryRelative(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, full);
        return NormalizeLibraryKey(relative);
    }

    public static string ResolveLibraryPath(string root, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = value.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathFullyQualified(normalized))
            return Path.GetFullPath(normalized);

        return Path.GetFullPath(Path.Combine(root, normalized));
    }

    private static string NormalizeLibraryKey(string? value)
        => (value ?? "").Replace('\\', '/').TrimStart('/');

    private static string? ReadString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal));

    private static int ReadInt32(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return 0;
        var value = reader.GetValue(ordinal);
        if (value is bool b)
            return b ? 1 : 0;
        return Convert.ToInt32(value);
    }

    private static double ReadDouble(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0 : Convert.ToDouble(reader.GetValue(ordinal));

    private static bool ReadBool(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return false;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            bool b => b,
            long l => l != 0,
            int i => i != 0,
            string s => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1",
            _ => Convert.ToInt32(value) != 0
        };
    }
}
