using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Objects.UObject;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace UnrealExporter;

internal static class UESourceIndexBuilder
{
    public static void Build(AbstractFileProvider provider, ConfigObj config)
    {
        var outputRoot = Path.GetFullPath(config.OutputDir);
        Directory.CreateDirectory(outputRoot);
        var dbPath = Path.Combine(outputRoot, "ue_source_index.db");
        if (File.Exists(dbPath))
            File.Delete(dbPath);

        var packagePatterns = BuildPackagePatterns(config);
        var packageFiles = provider.Files.Values
            .Where(x => x.IsUePackage)
            .Where(x => packagePatterns.Length == 0 || packagePatterns.Any(pattern => pattern.IsMatch(x.Path)))
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (config.SourceIndexLimit > 0)
            packageFiles = packageFiles.Take(config.SourceIndexLimit).ToArray();

        Console.WriteLine($"UE source index: files={provider.Files.Count}, packagesToInspect={packageFiles.Length}");

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "PRAGMA synchronous = NORMAL;");
        using var transaction = connection.BeginTransaction();
        CreateSchema(connection, transaction);

        foreach (var file in provider.Files.Values.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            InsertSourceFile(connection, transaction, file);

        var inspected = 0;
        foreach (var file in packageFiles)
        {
            inspected++;
            try
            {
                var exports = provider.LoadPackage(file).GetExports().ToArray();
                foreach (var obj in exports)
                    InsertSourceObject(connection, transaction, file, obj);
            }
            catch (Exception ex)
            {
                InsertError(connection, transaction, file.Path, ex.Message);
            }

            if (inspected % 500 == 0)
                Console.WriteLine($"UE source index inspected {inspected}/{packageFiles.Length}");
        }

        transaction.Commit();
        Console.WriteLine($"UE source index written: {dbPath}");
    }

    private static Regex[] BuildPackagePatterns(ConfigObj config)
    {
        var patterns = new List<string>();
        if (config.SourceIndexRegex is { Count: > 0 })
        {
            patterns.AddRange(config.SourceIndexRegex.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        else if (config.Export is { Count: > 0 })
        {
            foreach (var item in config.Export)
            {
                var separator = item.LastIndexOf(':');
                if (separator > 0)
                    patterns.Add(item[..separator]);
            }
        }

        return patterns
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(x => new Regex("^" + x + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled))
            .ToArray();
    }

    private static void CreateSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, """
            CREATE TABLE source_files (
                path TEXT PRIMARY KEY,
                directory TEXT,
                name TEXT,
                extension TEXT,
                size INTEGER NOT NULL,
                is_package INTEGER NOT NULL,
                is_encrypted INTEGER NOT NULL,
                compression TEXT
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE source_objects (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
                object_type TEXT NOT NULL,
                export_type TEXT,
                name TEXT,
                object_path TEXT,
                skeleton_path TEXT,
                skeleton_name TEXT,
                skeleton_guid TEXT,
                bone_count INTEGER,
                material_count INTEGER,
                morph_target_count INTEGER,
                duration REAL,
                frame_count INTEGER,
                track_count INTEGER,
                compression TEXT,
                raw_json TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE source_relations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
                object_path TEXT,
                relation_type TEXT NOT NULL,
                target_path TEXT,
                target_name TEXT
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE source_index_errors (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
                error TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, "CREATE INDEX idx_source_objects_type ON source_objects(object_type);");
        Execute(connection, transaction, "CREATE INDEX idx_source_objects_skeleton ON source_objects(skeleton_path);");
        Execute(connection, transaction, "CREATE INDEX idx_source_relations_type ON source_relations(relation_type, target_path);");
    }

    private static void InsertSourceFile(SqliteConnection connection, SqliteTransaction transaction, GameFile file)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO source_files (
                path, directory, name, extension, size, is_package, is_encrypted, compression
            )
            VALUES (
                $path, $directory, $name, $extension, $size, $isPackage, $isEncrypted, $compression
            );
            """;
        Add(command, "$path", file.Path);
        Add(command, "$directory", file.Directory);
        Add(command, "$name", file.Name);
        Add(command, "$extension", file.Extension);
        Add(command, "$size", file.Size);
        Add(command, "$isPackage", file.IsUePackage ? 1 : 0);
        Add(command, "$isEncrypted", file.IsEncrypted ? 1 : 0);
        Add(command, "$compression", file.CompressionMethod.ToString());
        command.ExecuteNonQuery();
    }

    private static void InsertSourceObject(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GameFile file,
        UObject obj)
    {
        var skeletalMesh = obj as USkeletalMesh;
        var staticMesh = obj as UStaticMesh;
        var animationAsset = obj as UAnimationAsset;
        var animSequence = obj as UAnimSequence;
        var sequenceBase = obj as UAnimSequenceBase;
        var skeletonPath = GetPackageIndexPath(skeletalMesh?.Skeleton ?? animationAsset?.Skeleton);
        var skeletonName = skeletalMesh?.Skeleton?.Name ?? animationAsset?.Skeleton.Name;
        var materialCount = skeletalMesh?.SkeletalMaterials?.Length ?? staticMesh?.Materials?.Length;
        var raw = new
        {
            source = file.Path,
            objectType = obj.GetType().Name,
            exportType = obj.ExportType,
            name = obj.Name,
            objectPath = obj.GetPathName(),
            skeletonPath,
            skeletonName,
        };

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO source_objects (
                source_path, object_type, export_type, name, object_path,
                skeleton_path, skeleton_name, skeleton_guid, bone_count, material_count,
                morph_target_count, duration, frame_count, track_count, compression, raw_json
            )
            VALUES (
                $sourcePath, $objectType, $exportType, $name, $objectPath,
                $skeletonPath, $skeletonName, $skeletonGuid, $boneCount, $materialCount,
                $morphTargetCount, $duration, $frameCount, $trackCount, $compression, $rawJson
            );
            """;
        Add(command, "$sourcePath", file.Path);
        Add(command, "$objectType", obj.GetType().Name);
        Add(command, "$exportType", obj.ExportType);
        Add(command, "$name", obj.Name);
        Add(command, "$objectPath", obj.GetPathName());
        Add(command, "$skeletonPath", skeletonPath);
        Add(command, "$skeletonName", skeletonName);
        Add(command, "$skeletonGuid", animationAsset?.SkeletonGuid.ToString());
        Add(command, "$boneCount", skeletalMesh?.ReferenceSkeleton?.FinalRefBoneInfo?.Length);
        Add(command, "$materialCount", materialCount);
        Add(command, "$morphTargetCount", skeletalMesh?.MorphTargets?.Length);
        Add(command, "$duration", sequenceBase?.SequenceLength);
        Add(command, "$frameCount", animSequence?.NumFrames);
        Add(command, "$trackCount", animSequence?.GetNumTracks());
        Add(command, "$compression", animSequence?.CompressedDataStructure?.GetType().Name);
        Add(command, "$rawJson", JsonConvert.SerializeObject(raw));
        command.ExecuteNonQuery();

        if (!string.IsNullOrWhiteSpace(skeletonPath))
            InsertRelation(connection, transaction, file.Path, obj.GetPathName(), "Skeleton", skeletonPath, skeletonName);

        if (skeletalMesh != null)
        {
            foreach (var material in skeletalMesh.Materials.Where(x => x != null))
                InsertRelation(connection, transaction, file.Path, obj.GetPathName(), "Material", material!.GetPathName(), material.Name.Text);
        }
        else if (staticMesh != null)
        {
            foreach (var material in staticMesh.Materials.Where(x => x != null))
                InsertRelation(connection, transaction, file.Path, obj.GetPathName(), "Material", material!.GetPathName(), material.Name.Text);
        }
    }

    private static void InsertRelation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        string objectPath,
        string relationType,
        string? targetPath,
        string? targetName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO source_relations (
                source_path, object_path, relation_type, target_path, target_name
            )
            VALUES (
                $sourcePath, $objectPath, $relationType, $targetPath, $targetName
            );
            """;
        Add(command, "$sourcePath", sourcePath);
        Add(command, "$objectPath", objectPath);
        Add(command, "$relationType", relationType);
        Add(command, "$targetPath", targetPath);
        Add(command, "$targetName", targetName);
        command.ExecuteNonQuery();
    }

    private static void InsertError(SqliteConnection connection, SqliteTransaction transaction, string sourcePath, string error)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO source_index_errors (source_path, error) VALUES ($sourcePath, $error);";
        Add(command, "$sourcePath", sourcePath);
        Add(command, "$error", error);
        command.ExecuteNonQuery();
    }

    private static string? GetPackageIndexPath(FPackageIndex? index)
    {
        if (index == null || index.IsNull)
            return null;

        return index.ResolvedObjectNoCache?.GetPathName() ?? index.Name;
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
}
