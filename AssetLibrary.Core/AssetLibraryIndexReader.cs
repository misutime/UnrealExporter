using Microsoft.Data.Sqlite;

namespace AssetLibrary.Core;

public static class AssetLibraryIndexReader
{
    public static AssetLibraryIndex Load(string root)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"素材库目录不存在: {root}");

        var dbPath = Path.Combine(root, AssetLibrarySchema.IndexFileName);
        if (!File.Exists(dbPath))
            throw new FileNotFoundException("没有找到 library_index.db。请先运行导出工具生成统一素材库索引。", dbPath);

        SQLitePCL.Batteries_V2.Init();
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();
        RequireTable(connection, AssetLibrarySchema.Tables.Assets);

        var hasAnimationTables = HasTable(connection, "model_animation_relations")
            && HasTable(connection, "relation_animations");
        var manifest = AssetLibraryManifest.LoadOrDefault(root, hasAnimationTables);
        var animationsEnabled = manifest.Capabilities.Animations && hasAnimationTables;

        var animationsByModel = animationsEnabled
            ? LoadAnimations(root, connection)
            : new Dictionary<string, List<AssetLibraryAnimation>>(StringComparer.OrdinalIgnoreCase);
        var models = LoadModels(root, connection, animationsByModel);
        var animationUsages = animationsEnabled ? BuildAnimationUsages(root, models, animationsByModel) : [];
        var animationGroups = animationsEnabled ? BuildAnimationGroups(animationUsages) : [];
        var textures = LoadAssetsByKind(root, connection, "Texture");
        var materials = LoadAssetsByKind(root, connection, "Material");

        return new AssetLibraryIndex
        {
            Root = root,
            Manifest = manifest,
            Models = models,
            AnimationsByModel = animationsByModel,
            AnimationUsages = animationUsages,
            AnimationGroups = animationGroups,
            Textures = textures,
            Materials = materials
        };
    }

    public static string ResolveLibraryPath(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        path = path.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        return Path.GetFullPath(Path.Combine(root, path));
    }

    public static string MakeLibraryRelative(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        try
        {
            var relative = Path.GetRelativePath(fullRoot, fullPath);
            return NormalizeLibraryKey(relative);
        }
        catch
        {
            return NormalizeLibraryKey(path);
        }
    }

    public static string NormalizeLibraryKey(string? path)
        => (path ?? "").Replace('\\', '/').TrimStart('/');

    private static List<AssetLibraryModel> LoadModels(
        string root,
        SqliteConnection connection,
        IReadOnlyDictionary<string, List<AssetLibraryAnimation>> animationsByModel)
    {
        var validation = LoadModelValidation(root, connection);
        var coverage = LoadModelCoverage(root, connection);
        var relationCounts = LoadModelRelationCounts(connection);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ColumnExpr(connection, "assets", "name", "''")},
                   {ColumnExpr(connection, "assets", "output", "''")},
                   {ColumnExpr(connection, "assets", "source", "''")},
                   {ColumnExpr(connection, "assets", "source_type", "''")},
                   {ColumnExpr(connection, "assets", "resource_kind", "''")},
                   {ColumnExpr(connection, "assets", "skeleton_path", "''")},
                   {ColumnExpr(connection, "assets", "skeleton_name", "''")},
                   {ColumnExpr(connection, "assets", "validation_status", "''")}
            FROM assets
            WHERE {ColumnExpr(connection, "assets", "kind", "''")} = 'Model'
              AND {ColumnExpr(connection, "assets", "output", "''")} IS NOT NULL
            ORDER BY name COLLATE NOCASE;
            """;

        var models = new List<AssetLibraryModel>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var rawOutput = ReadString(reader, 1) ?? "";
            var output = ResolveLibraryPath(root, rawOutput);
            if (string.IsNullOrWhiteSpace(output) || !File.Exists(output))
                continue;

            var relationKey = MakeLibraryRelative(root, output);
            animationsByModel.TryGetValue(relationKey, out var animations);
            relationCounts.TryGetValue(NormalizeLibraryKey(rawOutput), out var reportedCount);
            validation.TryGetValue(NormalizeLibraryKey(rawOutput), out var modelValidation);
            validation.TryGetValue(relationKey, out modelValidation);
            coverage.TryGetValue(NormalizeLibraryKey(rawOutput), out var modelCoverage);
            coverage.TryGetValue(relationKey, out modelCoverage);

            var usable = animations?.Count(x => x.IsPreviewable) ?? 0;
            var trusted = animations?.Count(x => x.IsDefaultTrusted) ?? 0;
            var compatible = animations?.Count(x => string.Equals(x.RecommendedUse, "compatibleCandidate", StringComparison.OrdinalIgnoreCase)) ?? 0;
            var review = animations?.Count(x =>
                string.Equals(x.RecommendedUse, "manualReview", StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.RecommendedUse, "compatibleNeedsReview", StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.RecommendedUse, "notUsable", StringComparison.OrdinalIgnoreCase)) ?? 0;

            models.Add(new AssetLibraryModel
            {
                Name = ReadString(reader, 0) ?? Path.GetFileNameWithoutExtension(output),
                Output = output,
                Source = ReadString(reader, 2) ?? "",
                SourceType = ReadString(reader, 3) ?? "",
                ResourceKind = ReadString(reader, 4) ?? "",
                SkeletonPath = ReadString(reader, 5) ?? modelCoverage.SkeletonPath,
                SkeletonName = ReadString(reader, 6) ?? modelCoverage.SkeletonName,
                ValidationStatus = FirstNonEmpty(ReadString(reader, 7), modelCoverage.ValidationStatus, modelValidation.Status),
                Confidence = modelCoverage.Confidence,
                BoneCount = FirstNonZero(modelCoverage.BoneCount, modelValidation.BoneCount),
                MaterialCount = FirstNonZero(modelCoverage.MaterialCount, modelValidation.MaterialCount),
                HasSkin = modelCoverage.HasSkin || modelValidation.SkinCount > 0,
                AnimationCount = Math.Max(reportedCount, animations?.Count ?? 0),
                UsableAnimationCount = usable,
                TrustedAnimationCount = trusted,
                CompatibleAnimationCount = compatible,
                ReviewAnimationCount = review
            });
        }

        return models
            .OrderByDescending(x => x.AnimationCount)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Dictionary<string, AssetLibraryAnimation> LoadAnimationByOutput(string root, SqliteConnection connection)
    {
        var result = new Dictionary<string, AssetLibraryAnimation>(StringComparer.OrdinalIgnoreCase);
        if (!HasTable(connection, "assets"))
            return result;

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ColumnExpr(connection, "assets", "name", "''")},
                   {ColumnExpr(connection, "assets", "output", "''")},
                   {ColumnExpr(connection, "assets", "source", "''")},
                   {ColumnExpr(connection, "assets", "source_type", "''")},
                   {ColumnExpr(connection, "assets", "format", "''")},
                   {ColumnExpr(connection, "assets", "validation_status", "''")}
            FROM assets
            WHERE {ColumnExpr(connection, "assets", "kind", "''")} = 'Animation'
              AND {ColumnExpr(connection, "assets", "output", "''")} IS NOT NULL;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var rawOutput = ReadString(reader, 1) ?? "";
            var output = ResolveLibraryPath(root, rawOutput);
            if (string.IsNullOrWhiteSpace(output))
                continue;

            var animation = new AssetLibraryAnimation
            {
                Name = ReadString(reader, 0) ?? Path.GetFileNameWithoutExtension(output),
                Output = output,
                Source = ReadString(reader, 2) ?? "",
                Status = "ok",
                RelationSource = "asset",
                UsageEvidence = "asset",
                ConfidenceTier = "Asset",
                RelationshipKind = "asset",
                RecommendedUse = "manualReview",
                ValidationStatus = ReadString(reader, 5) ?? "",
                IsUsableCandidate = File.Exists(output)
            };
            result[NormalizeLibraryKey(rawOutput)] = animation;
        }

        return result;
    }

    private static Dictionary<string, List<AssetLibraryAnimation>> LoadAnimations(string root, SqliteConnection connection)
    {
        var fallbackAnimations = LoadAnimationByOutput(root, connection);
        var result = new Dictionary<string, List<AssetLibraryAnimation>>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ColumnExpr(connection, "model_animation_relations", "model_output", "mar.model")},
                   ra.name,
                   ra.output,
                   ra.source,
                   {ColumnExpr(connection, "relation_animations", "status", "''")},
                   {ColumnExpr(connection, "relation_animations", "relation_source", "''")},
                   {ColumnExpr(connection, "relation_animations", "usage_evidence", "''")},
                   {ColumnExpr(connection, "relation_animations", "is_explicit_usage", "0")},
                   {ColumnExpr(connection, "relation_animations", "is_skeleton_compatible", "0")},
                   {ColumnExpr(connection, "relation_animations", "confidence_tier", "''")},
                   {ColumnExpr(connection, "relation_animations", "relationship_kind", "''")},
                   {ColumnExpr(connection, "relation_animations", "recommended_use", "''")},
                   {ColumnExpr(connection, "relation_animations", "evidence_summary", "''")},
                   {ColumnExpr(connection, "relation_animations", "is_deterministic_usage", "0")},
                   {ColumnExpr(connection, "relation_animations", "is_compatibility_candidate", "0")},
                   {ColumnExpr(connection, "relation_animations", "validation_status", "''")},
                   {ColumnExpr(connection, "relation_animations", "validation_category", "''")},
                   {ColumnExpr(connection, "relation_animations", "validation_reason", "''")},
                   {ColumnExpr(connection, "relation_animations", "duration", "0")},
                   {ColumnExpr(connection, "relation_animations", "frame_count", "0")},
                   {ColumnExpr(connection, "relation_animations", "track_count", "0")},
                   {ColumnExpr(connection, "relation_animations", "track_coverage", "0")},
                   {ColumnExpr(connection, "relation_animations", "hierarchy_compatible", "0")},
                   {ColumnExpr(connection, "relation_animations", "is_container_animation", "0")},
                   {ColumnExpr(connection, "relation_animations", "is_usable_candidate", "1")}
            FROM relation_animations ra
            JOIN model_animation_relations mar ON mar.id = ra.relation_id
            ORDER BY {ColumnExpr(connection, "model_animation_relations", "model_output", "mar.model")} COLLATE NOCASE, ra.name COLLATE NOCASE;
            """;

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var model = NormalizeLibraryKey(ReadString(reader, 0));
            if (string.IsNullOrWhiteSpace(model))
                continue;

            var rawOutput = ReadString(reader, 2) ?? "";
            var output = ResolveLibraryPath(root, rawOutput);
            fallbackAnimations.TryGetValue(NormalizeLibraryKey(rawOutput), out var fallback);
            var animation = new AssetLibraryAnimation
            {
                Name = ReadString(reader, 1) ?? fallback?.Name ?? Path.GetFileNameWithoutExtension(output),
                Output = output,
                Source = ReadString(reader, 3) ?? fallback?.Source ?? "",
                Status = FirstNonEmpty(ReadString(reader, 4), fallback?.Status),
                RelationSource = ReadString(reader, 5) ?? "",
                UsageEvidence = ReadString(reader, 6) ?? "",
                IsExplicitUsage = ReadBool(reader, 7),
                IsSkeletonCompatible = ReadBool(reader, 8),
                ConfidenceTier = ReadString(reader, 9) ?? "",
                RelationshipKind = ReadString(reader, 10) ?? "",
                RecommendedUse = ReadString(reader, 11) ?? "",
                EvidenceSummary = ReadString(reader, 12) ?? "",
                IsDeterministicUsage = ReadBool(reader, 13),
                IsCompatibilityCandidate = ReadBool(reader, 14),
                ValidationStatus = ReadString(reader, 15) ?? fallback?.ValidationStatus ?? "",
                ValidationCategory = ReadString(reader, 16) ?? "",
                ValidationReason = ReadString(reader, 17) ?? "",
                Duration = ReadDouble(reader, 18),
                FrameCount = ReadInt32(reader, 19),
                TrackCount = ReadInt32(reader, 20),
                TrackCoverage = ReadDouble(reader, 21),
                HierarchyCompatible = ReadBool(reader, 22),
                IsContainerAnimation = ReadBool(reader, 23),
                IsUsableCandidate = ReadBool(reader, 24)
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

    private static List<AssetLibraryAsset> LoadAssetsByKind(string root, SqliteConnection connection, string kind)
    {
        if (!HasTable(connection, "assets"))
            return [];

        var textureLinks = string.Equals(kind, "Texture", StringComparison.OrdinalIgnoreCase)
            ? LoadTextureLinkMap(connection)
            : new Dictionary<string, TextureLinkRow>(StringComparer.OrdinalIgnoreCase);
        var materialSidecars = string.Equals(kind, "Material", StringComparison.OrdinalIgnoreCase)
            ? LoadMaterialSidecarMap(connection)
            : new Dictionary<string, MaterialSidecarRow>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ColumnExpr(connection, "assets", "name", "''")},
                   {ColumnExpr(connection, "assets", "output", "''")},
                   {ColumnExpr(connection, "assets", "source", "''")},
                   {ColumnExpr(connection, "assets", "source_type", "''")},
                   {ColumnExpr(connection, "assets", "resource_kind", "''")},
                   {ColumnExpr(connection, "assets", "format", "''")},
                   {ColumnExpr(connection, "assets", "validation_status", "''")}
            FROM assets
            WHERE {ColumnExpr(connection, "assets", "kind", "''")} = $kind
              AND {ColumnExpr(connection, "assets", "output", "''")} IS NOT NULL
            ORDER BY name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$kind", kind);

        var result = new List<AssetLibraryAsset>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var rawOutput = ReadString(reader, 1) ?? "";
            var output = ResolveLibraryPath(root, rawOutput);
            textureLinks.TryGetValue(NormalizeLibraryKey(rawOutput), out var textureLink);
            materialSidecars.TryGetValue(NormalizeLibraryKey(rawOutput), out var material);
            result.Add(new AssetLibraryAsset
            {
                Kind = kind,
                Name = ReadString(reader, 0) ?? Path.GetFileNameWithoutExtension(output),
                Output = output,
                Source = ReadString(reader, 2) ?? "",
                SourceType = ReadString(reader, 3) ?? "",
                ResourceKind = ReadString(reader, 4) ?? "",
                Format = ReadString(reader, 5) ?? "",
                ValidationStatus = ReadString(reader, 6) ?? "",
                SharedTexture = ResolveLibraryPath(root, textureLink.Shared),
                Sha256 = textureLink.Sha256 ?? "",
                SizeBytes = textureLink.SizeBytes == 0 ? material.SizeBytes : textureLink.SizeBytes,
                HardLinked = textureLink.HardLinked,
                LinkError = textureLink.LinkError ?? "",
                TextureSlotCount = material.TextureSlotCount,
                ColorCount = material.ColorCount,
                ScalarCount = material.ScalarCount,
                SwitchCount = material.SwitchCount,
                BlendMode = material.BlendMode ?? "",
                ShadingModel = material.ShadingModel ?? ""
            });
        }

        return result;
    }

    private static Dictionary<string, ModelValidationRow> LoadModelValidation(string root, SqliteConnection connection)
    {
        var result = new Dictionary<string, ModelValidationRow>(StringComparer.OrdinalIgnoreCase);
        if (!HasTable(connection, AssetLibrarySchema.Tables.ModelValidation))
            return result;

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ColumnExpr(connection, "model_validation", "output", ColumnExpr(connection, "model_validation", "path", "''"))},
                   {ColumnExpr(connection, "model_validation", "status", "''")},
                   {ColumnExpr(connection, "model_validation", "material_count", "0")},
                   {ColumnExpr(connection, "model_validation", "texture_count", "0")},
                   {ColumnExpr(connection, "model_validation", "skin_count", "0")},
                   {ColumnExpr(connection, "model_validation", "bone_count", "0")}
            FROM model_validation;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var rawPath = ReadString(reader, 0);
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            var row = new ModelValidationRow(
                ReadString(reader, 1) ?? "",
                ReadInt32(reader, 2),
                ReadInt32(reader, 3),
                ReadInt32(reader, 4),
                ReadInt32(reader, 5));
            result[NormalizeLibraryKey(rawPath)] = row;
            result[MakeLibraryRelative(root, ResolveLibraryPath(root, rawPath))] = row;
        }

        return result;
    }

    private static Dictionary<string, ModelCoverageRow> LoadModelCoverage(string root, SqliteConnection connection)
    {
        var result = new Dictionary<string, ModelCoverageRow>(StringComparer.OrdinalIgnoreCase);
        if (!HasTable(connection, "model_coverage"))
            return result;

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ColumnExpr(connection, "model_coverage", "output", "''")},
                   {ColumnExpr(connection, "model_coverage", "validation_status", "''")},
                   {ColumnExpr(connection, "model_coverage", "has_skin", "0")},
                   {ColumnExpr(connection, "model_coverage", "material_count", "0")},
                   {ColumnExpr(connection, "model_coverage", "texture_count", "0")},
                   {ColumnExpr(connection, "model_coverage", "animation_candidate_count", "0")},
                   {ColumnExpr(connection, "model_coverage", "resource_kind", "''")},
                   {ColumnExpr(connection, "model_coverage", "source_type", "''")}
            FROM model_coverage;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var rawOutput = ReadString(reader, 0);
            if (string.IsNullOrWhiteSpace(rawOutput))
                continue;

            var row = new ModelCoverageRow(
                ReadString(reader, 1) ?? "",
                ReadBool(reader, 2),
                ReadInt32(reader, 3),
                ReadInt32(reader, 5),
                "",
                "",
                "",
                0);
            result[NormalizeLibraryKey(rawOutput)] = row;
            result[MakeLibraryRelative(root, ResolveLibraryPath(root, rawOutput))] = row;
        }

        return result;
    }

    private static Dictionary<string, int> LoadModelRelationCounts(SqliteConnection connection)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!HasTable(connection, "model_animation_relations"))
            return result;

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ColumnExpr(connection, "model_animation_relations", "model_output", "model")}, COALESCE(animation_count, 0)
            FROM model_animation_relations;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var model = NormalizeLibraryKey(ReadString(reader, 0));
            if (!string.IsNullOrWhiteSpace(model))
                result[model] = ReadInt32(reader, 1);
        }

        return result;
    }

    private static Dictionary<string, TextureLinkRow> LoadTextureLinkMap(SqliteConnection connection)
    {
        var result = new Dictionary<string, TextureLinkRow>(StringComparer.OrdinalIgnoreCase);
        if (!HasTable(connection, "texture_links"))
            return result;

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ColumnExpr(connection, "texture_links", "source", "''")},
                   {ColumnExpr(connection, "texture_links", "shared", "''")},
                   {ColumnExpr(connection, "texture_links", "sha256", "''")},
                   {ColumnExpr(connection, "texture_links", "size_bytes", "0")},
                   {ColumnExpr(connection, "texture_links", "hard_linked", "0")},
                   {ColumnExpr(connection, "texture_links", "link_error", "''")}
            FROM texture_links;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var source = NormalizeLibraryKey(ReadString(reader, 0));
            if (string.IsNullOrWhiteSpace(source))
                continue;

            result[source] = new TextureLinkRow(
                ReadString(reader, 1) ?? "",
                ReadString(reader, 2) ?? "",
                ReadInt64(reader, 3),
                ReadBool(reader, 4),
                ReadString(reader, 5) ?? "");
        }

        return result;
    }

    private static Dictionary<string, MaterialSidecarRow> LoadMaterialSidecarMap(SqliteConnection connection)
    {
        var result = new Dictionary<string, MaterialSidecarRow>(StringComparer.OrdinalIgnoreCase);
        if (!HasTable(connection, "material_sidecars"))
            return result;

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ColumnExpr(connection, "material_sidecars", "relative_path", "''")},
                   {ColumnExpr(connection, "material_sidecars", "size_bytes", "0")},
                   {ColumnExpr(connection, "material_sidecars", "texture_slot_count", "0")},
                   {ColumnExpr(connection, "material_sidecars", "color_count", "0")},
                   {ColumnExpr(connection, "material_sidecars", "scalar_count", "0")},
                   {ColumnExpr(connection, "material_sidecars", "switch_count", "0")},
                   {ColumnExpr(connection, "material_sidecars", "blend_mode", "''")},
                   {ColumnExpr(connection, "material_sidecars", "shading_model", "''")}
            FROM material_sidecars;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var path = NormalizeLibraryKey(ReadString(reader, 0));
            if (string.IsNullOrWhiteSpace(path))
                continue;

            result[path] = new MaterialSidecarRow(
                ReadInt64(reader, 1),
                ReadInt32(reader, 2),
                ReadInt32(reader, 3),
                ReadInt32(reader, 4),
                ReadInt32(reader, 5),
                ReadString(reader, 6) ?? "",
                ReadString(reader, 7) ?? "");
        }

        return result;
    }

    private static List<AssetLibraryAnimationUsage> BuildAnimationUsages(
        string root,
        IReadOnlyList<AssetLibraryModel> models,
        IReadOnlyDictionary<string, List<AssetLibraryAnimation>> animationsByModel)
    {
        var result = new List<AssetLibraryAnimationUsage>();
        foreach (var pair in animationsByModel)
        {
            var model = models.FirstOrDefault(x => string.Equals(MakeLibraryRelative(root, x.Output), pair.Key, StringComparison.OrdinalIgnoreCase));
            if (model == null)
                continue;

            result.AddRange(pair.Value.Select(animation => new AssetLibraryAnimationUsage
            {
                Model = model,
                Animation = animation
            }));
        }

        return result;
    }

    private static List<AssetLibraryAnimationGroup> BuildAnimationGroups(IEnumerable<AssetLibraryAnimationUsage> usages)
        => usages
            .GroupBy(x => AnimationKey(x.Animation), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var list = group.ToList();
                var representative = list
                    .OrderBy(x => RecommendedUseSortKey(x.Animation.RecommendedUse))
                    .ThenByDescending(x => x.Animation.IsPreviewable)
                    .ThenBy(x => x.Animation.Name, StringComparer.OrdinalIgnoreCase)
                    .First()
                    .Animation;
                return new AssetLibraryAnimationGroup
                {
                    Key = group.Key,
                    Representative = representative,
                    Usages = list
                };
            })
            .OrderByDescending(x => x.DefaultTrustedCount)
            .ThenByDescending(x => x.CompatibleCount)
            .ThenByDescending(x => x.PreviewableCount)
            .ThenByDescending(x => x.ModelCount)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static int RecommendedUseSortKey(string? value)
        => value switch
        {
            "defaultTrusted" => 0,
            "compatibleCandidate" => 1,
            "manualReview" => 2,
            "compatibleNeedsReview" => 3,
            "notUsable" => 4,
            _ => 5
        };

    private static string AnimationKey(AssetLibraryAnimation animation)
        => !string.IsNullOrWhiteSpace(animation.Output)
            ? NormalizeLibraryKey(animation.Output)
            : animation.Name;

    private static string ColumnExpr(SqliteConnection connection, string table, string column, string fallback)
        => HasColumn(connection, table, column) ? column : fallback;

    private static bool HasTable(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() != null;
    }

    private static void RequireTable(SqliteConnection connection, string table)
    {
        if (!HasTable(connection, table))
            throw new InvalidDataException($"library_index.db 缺少必要表: {table}");
    }

    private static bool HasColumn(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(ReadString(reader, 1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";

    private static int FirstNonZero(params int[] values)
        => values.FirstOrDefault(x => x != 0);

    private static string? ReadString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal));

    private static int ReadInt32(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));

    private static long ReadInt64(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? 0 : Convert.ToInt64(reader.GetValue(ordinal));

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

    private readonly record struct TextureLinkRow(string Shared, string Sha256, long SizeBytes, bool HardLinked, string LinkError);
    private readonly record struct MaterialSidecarRow(long SizeBytes, int TextureSlotCount, int ColorCount, int ScalarCount, int SwitchCount, string BlendMode, string ShadingModel);
    private readonly record struct ModelValidationRow(string Status, int MaterialCount, int TextureCount, int SkinCount, int BoneCount);
    private readonly record struct ModelCoverageRow(string ValidationStatus, bool HasSkin, int MaterialCount, int AnimationCandidateCount, string SkeletonPath, string SkeletonName, string Confidence, int BoneCount);
}
