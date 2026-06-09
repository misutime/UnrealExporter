using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Component;
using CUE4Parse.UE4.Assets.Exports.Component.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Component.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Engine;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Exports.WorldPartition;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Objects.Engine;
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
        DeleteSqliteOutput(dbPath);

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
                var package = provider.LoadPackage(file);
                InsertPackageObjectMaps(connection, transaction, file.Path, package);
                var exports = package.GetExports().ToArray();
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
        FinalizeSqliteOutput(connection);
        Console.WriteLine($"UE source index written: {dbPath}");
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
                socket_count INTEGER,
                duration REAL,
                frame_count INTEGER,
                track_count INTEGER,
                notify_count INTEGER,
                curve_count INTEGER,
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
            CREATE TABLE package_object_maps (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
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
            CREATE TABLE material_texture_slots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
                material_object_path TEXT,
                material_name TEXT,
                slot_name TEXT,
                texture_path TEXT,
                texture_name TEXT,
                texture_object_path TEXT,
                relation_source TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE skeleton_bones (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
                owner_object_path TEXT,
                owner_type TEXT NOT NULL,
                skeleton_path TEXT,
                bone_index INTEGER NOT NULL,
                bone_name TEXT NOT NULL,
                parent_index INTEGER NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE mesh_sockets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
                owner_object_path TEXT,
                owner_type TEXT NOT NULL,
                socket_index INTEGER NOT NULL,
                socket_name TEXT,
                bone_name TEXT,
                socket_object_path TEXT,
                location_x REAL NOT NULL,
                location_y REAL NOT NULL,
                location_z REAL NOT NULL,
                rotation_pitch REAL NOT NULL,
                rotation_yaw REAL NOT NULL,
                rotation_roll REAL NOT NULL,
                scale_x REAL NOT NULL,
                scale_y REAL NOT NULL,
                scale_z REAL NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE component_asset_relations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
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
                socket_name TEXT,
                parent_component_path TEXT,
                location_x REAL,
                location_y REAL,
                location_z REAL,
                rotation_pitch REAL,
                rotation_yaw REAL,
                rotation_roll REAL,
                scale_x REAL,
                scale_y REAL,
                scale_z REAL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE animation_tracks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
                animation_object_path TEXT,
                skeleton_path TEXT,
                track_index INTEGER NOT NULL,
                bone_index INTEGER NOT NULL,
                bone_name TEXT
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE animation_segments (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
                animation_object_path TEXT,
                skeleton_path TEXT,
                segment_index INTEGER NOT NULL,
                slot_name TEXT,
                referenced_animation_path TEXT,
                referenced_animation_name TEXT,
                start_pos REAL NOT NULL,
                anim_start_time REAL NOT NULL,
                anim_end_time REAL NOT NULL,
                play_rate REAL NOT NULL,
                looping_count INTEGER NOT NULL,
                length REAL NOT NULL,
                relation_source TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE animation_sections (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
                animation_object_path TEXT,
                section_index INTEGER NOT NULL,
                section_name TEXT,
                next_section_name TEXT,
                slot_index INTEGER NOT NULL,
                segment_index INTEGER NOT NULL,
                segment_begin_time REAL NOT NULL,
                link_method TEXT,
                cached_link_method TEXT
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE animation_notifies (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
                animation_object_path TEXT,
                notify_index INTEGER NOT NULL,
                notify_name TEXT,
                notify_object_path TEXT,
                notify_state_object_path TEXT,
                link_value REAL NOT NULL,
                duration REAL NOT NULL,
                track_index INTEGER NOT NULL,
                trigger_chance REAL NOT NULL,
                montage_tick_type TEXT,
                link_method TEXT,
                segment_index INTEGER NOT NULL,
                slot_index INTEGER NOT NULL
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE animation_curves (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
                animation_object_path TEXT,
                curve_index INTEGER NOT NULL,
                curve_name TEXT,
                curve_type_flags INTEGER NOT NULL,
                key_count INTEGER NOT NULL,
                min_time REAL,
                max_time REAL,
                min_value REAL,
                max_value REAL
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
        Execute(connection, transaction, "CREATE INDEX idx_package_object_maps_source ON package_object_maps(source_path, map_type);");
        Execute(connection, transaction, "CREATE INDEX idx_package_object_maps_object ON package_object_maps(object_path);");
        Execute(connection, transaction, "CREATE INDEX idx_package_object_maps_class ON package_object_maps(class_name, class_path);");
        Execute(connection, transaction, "CREATE INDEX idx_material_texture_slots_material ON material_texture_slots(material_object_path);");
        Execute(connection, transaction, "CREATE INDEX idx_material_texture_slots_texture ON material_texture_slots(texture_object_path);");
        Execute(connection, transaction, "CREATE INDEX idx_material_texture_slots_slot ON material_texture_slots(slot_name);");
        Execute(connection, transaction, "CREATE INDEX idx_skeleton_bones_owner ON skeleton_bones(owner_object_path);");
        Execute(connection, transaction, "CREATE INDEX idx_skeleton_bones_skeleton ON skeleton_bones(skeleton_path);");
        Execute(connection, transaction, "CREATE INDEX idx_mesh_sockets_owner ON mesh_sockets(owner_object_path);");
        Execute(connection, transaction, "CREATE INDEX idx_mesh_sockets_name ON mesh_sockets(socket_name);");
        Execute(connection, transaction, "CREATE INDEX idx_component_asset_relations_owner ON component_asset_relations(owner_object_path);");
        Execute(connection, transaction, "CREATE INDEX idx_component_asset_relations_component ON component_asset_relations(component_object_path);");
        Execute(connection, transaction, "CREATE INDEX idx_component_asset_relations_target ON component_asset_relations(relation_type, target_path);");
        Execute(connection, transaction, "CREATE INDEX idx_animation_tracks_animation ON animation_tracks(animation_object_path);");
        Execute(connection, transaction, "CREATE INDEX idx_animation_tracks_skeleton ON animation_tracks(skeleton_path);");
        Execute(connection, transaction, "CREATE INDEX idx_animation_segments_animation ON animation_segments(animation_object_path);");
        Execute(connection, transaction, "CREATE INDEX idx_animation_segments_reference ON animation_segments(referenced_animation_path);");
        Execute(connection, transaction, "CREATE INDEX idx_animation_sections_animation ON animation_sections(animation_object_path);");
        Execute(connection, transaction, "CREATE INDEX idx_animation_notifies_animation ON animation_notifies(animation_object_path);");
        Execute(connection, transaction, "CREATE INDEX idx_animation_notifies_name ON animation_notifies(notify_name);");
        Execute(connection, transaction, "CREATE INDEX idx_animation_curves_animation ON animation_curves(animation_object_path);");
        Execute(connection, transaction, "CREATE INDEX idx_animation_curves_name ON animation_curves(curve_name);");
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

    private static void InsertPackageObjectMaps(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        IPackage package)
    {
        switch (package)
        {
            case Package legacyPackage:
                InsertLegacyPackageObjectMaps(connection, transaction, sourcePath, legacyPackage);
                break;
            case IoPackage ioPackage:
                InsertIoPackageObjectMaps(connection, transaction, sourcePath, ioPackage);
                break;
        }
    }

    private static void InsertLegacyPackageObjectMaps(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        Package package)
    {
        for (var index = 0; index < package.ImportMap.Length; index++)
        {
            var import = package.ImportMap[index];
            var objectIndex = new FPackageIndex(package, -(index + 1));
            var resolved = SafeResolvePackageIndex(package, objectIndex);
            var raw = new
            {
                source = sourcePath,
                package = package.Name,
                mapType = "Import",
                mapIndex = index,
                import.ObjectName,
                import.ClassPackage,
                import.ClassName,
                import.PackageName,
                import.ImportOptional,
                objectPath = resolved?.GetPathName(),
                outerPath = resolved?.Outer?.GetPathName(),
            };

            InsertPackageObjectMapRow(
                connection,
                transaction,
                sourcePath,
                package.Name,
                "Import",
                index,
                import.ObjectName.Text,
                resolved?.GetPathName(),
                import.ClassName.Text,
                import.ClassPackage.Text,
                resolved?.Outer?.GetPathName(),
                null,
                null,
                import.PackageName.Text,
                null,
                import.ImportOptional,
                null,
                null,
                null,
                raw);
        }

        for (var index = 0; index < package.ExportMap.Length; index++)
        {
            var export = package.ExportMap[index];
            var objectIndex = new FPackageIndex(package, index + 1);
            var resolved = SafeResolvePackageIndex(package, objectIndex);
            var raw = new
            {
                source = sourcePath,
                package = package.Name,
                mapType = "Export",
                mapIndex = index,
                export.ObjectName,
                export.ClassName,
                export.ObjectFlags,
                export.SerialSize,
                export.SerialOffset,
                export.IsAsset,
                publicExportHash = export.GetPublicExportHash(),
                objectPath = resolved?.GetPathName(),
                classPath = SafeResolvePackageIndexPath(package, export.ClassIndex),
                outerPath = SafeResolvePackageIndexPath(package, export.OuterIndex),
                superPath = SafeResolvePackageIndexPath(package, export.SuperIndex),
                templatePath = SafeResolvePackageIndexPath(package, export.TemplateIndex),
            };

            InsertPackageObjectMapRow(
                connection,
                transaction,
                sourcePath,
                package.Name,
                "Export",
                index,
                export.ObjectName.Text,
                resolved?.GetPathName(),
                export.ClassName,
                SafeResolvePackageIndexPath(package, export.ClassIndex),
                SafeResolvePackageIndexPath(package, export.OuterIndex),
                SafeResolvePackageIndexPath(package, export.SuperIndex),
                SafeResolvePackageIndexPath(package, export.TemplateIndex),
                null,
                export.IsAsset,
                null,
                $"0x{export.ObjectFlags:X}",
                export.SerialSize,
                export.GetPublicExportHash().ToString("X16"),
                raw);
        }
    }

    private static void InsertIoPackageObjectMaps(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        IoPackage package)
    {
        for (var index = 0; index < package.ImportMap.Length; index++)
        {
            var importIndex = package.ImportMap[index];
            var resolved = SafeResolveObjectIndex(package, importIndex);
            FPackageImportReference? packageImport = importIndex.IsPackageImport ? importIndex.AsPackageImportRef : null;
            var raw = new
            {
                source = sourcePath,
                package = package.Name,
                mapType = "Import",
                mapIndex = index,
                type = importIndex.Type.ToString(),
                value = importIndex.Value,
                objectPath = resolved?.GetPathName(),
                classPath = resolved?.Class?.GetPathName(),
                outerPath = resolved?.Outer?.GetPathName(),
                importedPackageIndex = packageImport?.ImportedPackageIndex,
                importedPublicExportHashIndex = packageImport?.ImportedPublicExportHashIndex,
            };

            InsertPackageObjectMapRow(
                connection,
                transaction,
                sourcePath,
                package.Name,
                "Import",
                index,
                resolved?.Name.Text ?? importIndex.Value.ToString(),
                resolved?.GetPathName(),
                resolved?.Class?.Name.Text,
                resolved?.Class?.GetPathName(),
                resolved?.Outer?.GetPathName(),
                resolved?.Super?.GetPathName(),
                null,
                packageImport?.ImportedPackageIndex.ToString(),
                null,
                null,
                null,
                null,
                null,
                raw);
        }

        for (var index = 0; index < package.ExportMap.Length; index++)
        {
            var export = package.ExportMap[index];
            var objectName = package.CreateFNameFromMappedName(export.ObjectName).Text;
            var resolved = SafeResolveObjectIndex(package, new FPackageObjectIndex((ulong)index));
            var raw = new
            {
                source = sourcePath,
                package = package.Name,
                mapType = "Export",
                mapIndex = index,
                objectName,
                objectFlags = export.ObjectFlags.ToString(),
                export.CookedSerialSize,
                export.CookedSerialOffset,
                export.PublicExportHash,
                objectPath = resolved?.GetPathName(),
                classPath = SafeResolveObjectIndexPath(package, export.ClassIndex),
                outerPath = SafeResolveObjectIndexPath(package, export.OuterIndex),
                superPath = SafeResolveObjectIndexPath(package, export.SuperIndex),
                templatePath = SafeResolveObjectIndexPath(package, export.TemplateIndex),
            };

            InsertPackageObjectMapRow(
                connection,
                transaction,
                sourcePath,
                package.Name,
                "Export",
                index,
                objectName,
                resolved?.GetPathName(),
                SafeResolveObjectIndex(package, export.ClassIndex)?.Name.Text,
                SafeResolveObjectIndexPath(package, export.ClassIndex),
                SafeResolveObjectIndexPath(package, export.OuterIndex),
                SafeResolveObjectIndexPath(package, export.SuperIndex),
                SafeResolveObjectIndexPath(package, export.TemplateIndex),
                null,
                export.ObjectFlags.HasFlag(EObjectFlags.RF_Public),
                null,
                export.ObjectFlags.ToString(),
                (long)export.CookedSerialSize,
                export.PublicExportHash.ToString("X16"),
                raw);
        }
    }

    private static ResolvedObject? SafeResolvePackageIndex(IPackage package, FPackageIndex? index)
    {
        try
        {
            return package.ResolvePackageIndex(index);
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeResolvePackageIndexPath(IPackage package, FPackageIndex? index)
        => SafeResolvePackageIndex(package, index)?.GetPathName();

    private static ResolvedObject? SafeResolveObjectIndex(IoPackage package, FPackageObjectIndex index)
    {
        try
        {
            return package.ResolveObjectIndex(index);
        }
        catch
        {
            return null;
        }
    }

    private static string? SafeResolveObjectIndexPath(IoPackage package, FPackageObjectIndex index)
        => SafeResolveObjectIndex(package, index)?.GetPathName();

    private static void InsertPackageObjectMapRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        string packageName,
        string mapType,
        int mapIndex,
        string? objectName,
        string? objectPath,
        string? className,
        string? classPath,
        string? outerPath,
        string? superPath,
        string? templatePath,
        string? targetPackage,
        bool? isAsset,
        bool? isOptional,
        string? objectFlags,
        long? serialSize,
        string? publicExportHash,
        object raw)
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
        Add(command, "$sourcePath", sourcePath);
        Add(command, "$packageName", packageName);
        Add(command, "$mapType", mapType);
        Add(command, "$mapIndex", mapIndex);
        Add(command, "$objectName", objectName);
        Add(command, "$objectPath", objectPath);
        Add(command, "$className", className);
        Add(command, "$classPath", classPath);
        Add(command, "$outerPath", outerPath);
        Add(command, "$superPath", superPath);
        Add(command, "$templatePath", templatePath);
        Add(command, "$targetPackage", targetPackage);
        Add(command, "$isAsset", isAsset.HasValue ? isAsset.Value ? 1 : 0 : null);
        Add(command, "$isOptional", isOptional.HasValue ? isOptional.Value ? 1 : 0 : null);
        Add(command, "$objectFlags", objectFlags);
        Add(command, "$serialSize", serialSize);
        Add(command, "$publicExportHash", publicExportHash);
        Add(command, "$rawJson", JsonConvert.SerializeObject(raw));
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
            socketCount = CountSockets(obj),
            notifyCount = sequenceBase?.Notifies.Length,
            curveCount = animSequence?.CompressedCurveData?.FloatCurves?.Length,
        };

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO source_objects (
                source_path, object_type, export_type, name, object_path,
                skeleton_path, skeleton_name, skeleton_guid, bone_count, material_count,
                morph_target_count, socket_count, duration, frame_count, track_count, notify_count, curve_count, compression, raw_json
            )
            VALUES (
                $sourcePath, $objectType, $exportType, $name, $objectPath,
                $skeletonPath, $skeletonName, $skeletonGuid, $boneCount, $materialCount,
                $morphTargetCount, $socketCount, $duration, $frameCount, $trackCount, $notifyCount, $curveCount, $compression, $rawJson
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
        Add(command, "$socketCount", CountSockets(obj));
        Add(command, "$duration", sequenceBase?.SequenceLength);
        Add(command, "$frameCount", animSequence?.NumFrames);
        Add(command, "$trackCount", animSequence?.GetNumTracks());
        Add(command, "$notifyCount", sequenceBase?.Notifies.Length);
        Add(command, "$curveCount", animSequence?.CompressedCurveData?.FloatCurves?.Length);
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

        if (obj is UMaterialInterface materialInterface)
            InsertMaterialTextureSlots(connection, transaction, file.Path, materialInterface);

        if (skeletalMesh != null)
            InsertSkeletonBones(connection, transaction, file.Path, skeletalMesh.GetPathName(), "SkeletalMesh", skeletonPath, skeletalMesh.ReferenceSkeleton.FinalRefBoneInfo);

        if (obj is USkeleton skeleton)
            InsertSkeletonBones(connection, transaction, file.Path, skeleton.GetPathName(), "Skeleton", skeleton.GetPathName(), skeleton.ReferenceSkeleton.FinalRefBoneInfo);

        if (staticMesh != null)
            InsertStaticMeshSockets(connection, transaction, file.Path, staticMesh.GetPathName(), "StaticMesh", staticMesh.Sockets);

        if (skeletalMesh != null)
            InsertSkeletalMeshSockets(connection, transaction, file.Path, skeletalMesh.GetPathName(), "SkeletalMesh", skeletalMesh.Sockets);

        if (obj is USkeleton socketSkeleton)
            InsertSkeletalMeshSockets(connection, transaction, file.Path, socketSkeleton.GetPathName(), "Skeleton", socketSkeleton.Sockets);

        if (animSequence != null)
            InsertAnimationTracks(connection, transaction, file.Path, animSequence, skeletonPath);

        if (obj is UAnimMontage montage)
            InsertMontageSegments(connection, transaction, file.Path, montage, skeletonPath);

        if (obj is UAnimComposite composite)
            InsertCompositeSegments(connection, transaction, file.Path, composite, skeletonPath);

        if (sequenceBase != null)
            InsertAnimationNotifies(connection, transaction, file.Path, sequenceBase);

        if (animSequence != null)
            InsertAnimationCurves(connection, transaction, file.Path, animSequence);

        if (obj is UBlueprintGeneratedClass blueprintClass)
            InsertBlueprintComponentRelations(connection, transaction, file.Path, blueprintClass);

        if (obj is UWorld world)
            InsertWorldRelations(connection, transaction, file.Path, world);

        if (obj is ULevel level)
            InsertLevelRelations(connection, transaction, file.Path, level);

        if (obj is ULevelStreaming levelStreaming)
            InsertLevelStreamingRelations(connection, transaction, file.Path, levelStreaming);

        if (obj is AWorldSettings worldSettings)
            InsertWorldSettingsRelations(connection, transaction, file.Path, worldSettings);

        if (obj is UWorldPartition worldPartition)
            InsertWorldPartitionRelations(connection, transaction, file.Path, worldPartition);

        if (obj is UWorldPartitionRuntimeCell runtimeCell)
            InsertWorldPartitionRuntimeCellRelations(connection, transaction, file.Path, runtimeCell);

        if (obj is AActor actor)
            InsertActorComponentRelations(connection, transaction, file.Path, actor, "ActorComponent");

        if (obj is USceneComponent sceneComponent)
        {
            var ownerPath = GetComponentOwnerObjectPath(sceneComponent);
            InsertComponentAssetRelations(connection, transaction, file.Path, ownerPath, "ComponentOuter", sceneComponent, null, "ExportedComponent");
        }

        if (obj is AActor)
            InsertObjectPropertyAssetRelations(connection, transaction, file.Path, obj, "ActorProperty");
        else if (ShouldScanBlueprintPropertyReferences(obj))
            InsertObjectPropertyAssetRelations(connection, transaction, file.Path, obj, "BlueprintProperty");
    }

    private static void InsertBlueprintComponentRelations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        UBlueprintGeneratedClass blueprintClass)
    {
        var ownerPath = blueprintClass.GetPathName();
        var seenComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var componentIndex in blueprintClass.ComponentTemplates.Where(x => x is { IsNull: false }))
        {
            if (!TryLoadPackageIndex(componentIndex, out USceneComponent? component))
                continue;

            InsertComponentAssetRelations(
                connection,
                transaction,
                sourcePath,
                ownerPath,
                blueprintClass.GetType().Name,
                component,
                null,
                "BlueprintComponentTemplate",
                seenComponents);
        }

        if (TryLoadPackageIndex(blueprintClass.SimpleConstructionScript, out USimpleConstructionScript? script))
        {
            foreach (var node in GetAllSCSNodesSafe(script))
            {
                if (!TryLoadPackageIndex(node.GetComponentTemplateAsIndex(), out USceneComponent? component))
                    continue;

                var variableName = node.InternalVariableName.Text;
                InsertComponentAssetRelations(
                    connection,
                    transaction,
                    sourcePath,
                    ownerPath,
                    blueprintClass.GetType().Name,
                    component,
                    variableName,
                    "SimpleConstructionScript",
                    seenComponents);
            }
        }

        if (!TryLoadPackageIndex(blueprintClass.InheritableComponentHandler, out UInheritableComponentHandler? inheritable))
            return;

        foreach (var record in inheritable.GetRecords())
        {
            if (!TryLoadPackageIndex(record.ComponentTemplate, out USceneComponent? component))
                continue;

            InsertComponentAssetRelations(
                connection,
                transaction,
                sourcePath,
                ownerPath,
                blueprintClass.GetType().Name,
                component,
                record.ComponentKey.SCSVariableName.Text,
                "InheritableComponentOverride",
                seenComponents);
        }
    }

    private static string GetComponentOwnerObjectPath(USceneComponent component)
    {
        var componentPath = component.GetPathName();
        var subObjectSeparator = componentPath.LastIndexOf(':');
        if (subObjectSeparator > 0)
            return componentPath[..subObjectSeparator];

        var dotSeparator = componentPath.LastIndexOf('.');
        if (dotSeparator > 0)
            return componentPath[..dotSeparator];

        return componentPath;
    }

    private static void InsertWorldRelations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        UWorld world)
    {
        InsertPackageIndexObjectRelation(connection, transaction, sourcePath, world, world.PersistentLevel, "WorldPersistentLevel", "Level", "PersistentLevel");

        if (world.ExtraReferencedObjects != null)
        {
            for (var index = 0; index < world.ExtraReferencedObjects.Length; index++)
            {
                InsertPackageIndexObjectRelation(
                    connection,
                    transaction,
                    sourcePath,
                    world,
                    world.ExtraReferencedObjects[index],
                    "WorldExtraReference",
                    "WorldReference",
                    $"ExtraReferencedObjects[{index}]");
            }
        }

        if (world.StreamingLevels == null)
            return;

        for (var index = 0; index < world.StreamingLevels.Length; index++)
        {
            InsertPackageIndexObjectRelation(
                connection,
                transaction,
                sourcePath,
                world,
                world.StreamingLevels[index],
                "WorldStreamingLevel",
                "LevelStreaming",
                $"StreamingLevels[{index}]");
        }
    }

    private static void InsertLevelRelations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        ULevel level)
    {
        InsertPackageIndexObjectRelation(connection, transaction, sourcePath, level, level.WorldSettings, "LevelWorldSettings", "WorldSettings", "WorldSettings");
        InsertPackageIndexObjectRelation(connection, transaction, sourcePath, level, level.WorldDataLayers, "LevelWorldDataLayers", "WorldDataLayers", "WorldDataLayers");
        InsertSoftObjectPathRelation(connection, transaction, sourcePath, level, level.WorldPartitionRuntimeCell, "LevelWorldPartitionRuntimeCell", "WorldPartitionRuntimeCell", "WorldPartitionRuntimeCell");
        InsertPackageIndexObjectRelation(connection, transaction, sourcePath, level, level.Model, "LevelModel", "Model", "Model");
        InsertPackageIndexObjectRelation(connection, transaction, sourcePath, level, level.LevelScriptActor, "LevelScriptActor", "LevelScriptActor", "LevelScriptActor");

        if (level.ModelComponents != null)
        {
            for (var index = 0; index < level.ModelComponents.Length; index++)
            {
                InsertPackageIndexObjectRelation(
                    connection,
                    transaction,
                    sourcePath,
                    level,
                    level.ModelComponents[index],
                    "LevelModelComponent",
                    "ModelComponent",
                    $"ModelComponents[{index}]");
            }
        }

        if (level.Actors == null)
            return;

        for (var actorIndex = 0; actorIndex < level.Actors.Length; actorIndex++)
        {
            InsertPackageIndexObjectRelation(
                connection,
                transaction,
                sourcePath,
                level,
                level.Actors[actorIndex],
                "LevelActor",
                "Actor",
                $"Actors[{actorIndex}]");
        }
    }

    private static void InsertLevelStreamingRelations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        ULevelStreaming levelStreaming)
    {
        InsertSoftObjectPathRelation(
            connection,
            transaction,
            sourcePath,
            levelStreaming,
            levelStreaming.WorldAsset,
            "LevelStreamingWorldAsset",
            "WorldAsset",
            "WorldAsset");
    }

    private static void InsertWorldSettingsRelations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        AWorldSettings worldSettings)
    {
        InsertPackageIndexObjectRelation(
            connection,
            transaction,
            sourcePath,
            worldSettings,
            worldSettings.WorldPartition,
            "WorldSettingsPartition",
            "WorldPartition",
            "WorldPartition");
    }

    private static void InsertWorldPartitionRelations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        UWorldPartition worldPartition)
    {
        InsertPackageIndexObjectRelation(
            connection,
            transaction,
            sourcePath,
            worldPartition,
            worldPartition.RuntimeHash,
            "WorldPartitionRuntimeHash",
            "RuntimeHash",
            "RuntimeHash");
        InsertPackageIndexObjectRelation(
            connection,
            transaction,
            sourcePath,
            worldPartition,
            worldPartition.StreamingPolicy,
            "WorldPartitionStreamingPolicy",
            "StreamingPolicy",
            "StreamingPolicy");
    }

    private static void InsertWorldPartitionRuntimeCellRelations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        UWorldPartitionRuntimeCell runtimeCell)
    {
        InsertPackageIndexObjectRelation(
            connection,
            transaction,
            sourcePath,
            runtimeCell,
            runtimeCell.RuntimeCellData,
            "WorldPartitionRuntimeCellData",
            "RuntimeCellData",
            "RuntimeCellData");

        if (runtimeCell is UWorldPartitionRuntimeLevelStreamingCell levelStreamingCell)
        {
            InsertPackageIndexObjectRelation(
                connection,
                transaction,
                sourcePath,
                runtimeCell,
                levelStreamingCell.LevelStreaming,
                "WorldPartitionLevelStreamingCell",
                "LevelStreaming",
                "LevelStreaming");
        }
    }

    private static void InsertActorComponentRelations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        AActor actor,
        string relationSource)
    {
        var ownerPath = actor.GetPathName();
        var seenComponents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in EnumerateComponentReferences(actor.Properties, "Property"))
        {
            if (!seenComponents.Add($"{reference.PropertyPath}:{reference.Component.GetPathName()}"))
                continue;

            InsertComponentAssetRelations(
                connection,
                transaction,
                sourcePath,
                ownerPath,
                actor.GetType().Name,
                reference.Component,
                reference.PropertyPath,
                relationSource,
                seenComponents: null);
        }
    }

    private static void InsertComponentAssetRelations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        string ownerObjectPath,
        string ownerType,
        USceneComponent component,
        string? componentVariableName,
        string relationSource,
        HashSet<string>? seenComponents = null)
    {
        var componentPath = component.GetPathName();
        if (seenComponents != null && !seenComponents.Add($"{relationSource}:{componentPath}:{componentVariableName}"))
            return;

        var transform = component.GetRelativeTransform();
        var parentPath = GetAttachParentPathSafe(component);
        var socketName = component.GetOrDefault<FName?>("AttachSocketName")?.Text;
        var componentName = component.Name;

        InsertComponentAssetRelation(
            connection,
            transaction,
            sourcePath,
            ownerObjectPath,
            ownerType,
            componentPath,
            component.GetType().Name,
            componentName,
            componentVariableName,
            relationSource,
            "Component",
            componentPath,
            componentName,
            socketName,
            parentPath,
            transform);

        foreach (var relation in BuildComponentAssetTargets(component))
        {
            InsertRelation(connection, transaction, sourcePath, ownerObjectPath, relation.RelationType, relation.TargetPath, relation.TargetName);
            InsertComponentAssetRelation(
                connection,
                transaction,
                sourcePath,
                ownerObjectPath,
                ownerType,
                componentPath,
                component.GetType().Name,
                componentName,
                componentVariableName,
                relationSource,
                relation.RelationType,
                relation.TargetPath,
                relation.TargetName,
                socketName,
                parentPath,
                transform);
        }
    }

    private static IEnumerable<ComponentAssetTarget> BuildComponentAssetTargets(USceneComponent component)
    {
        if (component is UStaticMeshComponent staticMeshComponent)
        {
            var staticMesh = staticMeshComponent.GetStaticMesh();
            if (!staticMesh.IsNull)
                yield return ComponentAssetTarget.FromPackageIndex("StaticMesh", staticMesh);
        }

        if (component is USkeletalMeshComponent skeletalMeshComponent)
        {
            var skeletalMesh = skeletalMeshComponent.GetSkeletalMesh();
            if (!skeletalMesh.IsNull)
                yield return ComponentAssetTarget.FromPackageIndex("SkeletalMesh", skeletalMesh);

            if (skeletalMeshComponent.AnimationData is { } animationData && !animationData.AnimToPlay.IsNull)
                yield return ComponentAssetTarget.FromPackageIndex("Animation", animationData.AnimToPlay);

            foreach (var propertyName in new[] { "AnimClass", "AnimBlueprintGeneratedClass" })
            {
                var animClass = skeletalMeshComponent.GetOrDefault(propertyName, new FPackageIndex());
                if (!animClass.IsNull)
                    yield return ComponentAssetTarget.FromPackageIndex(propertyName, animClass);
            }
        }

        foreach (var material in GetComponentMaterials(component))
            yield return ComponentAssetTarget.FromPackageIndex("Material", material);
    }

    private static IEnumerable<FPackageIndex> GetComponentMaterials(USceneComponent component)
    {
        foreach (var propertyName in new[] { "OverrideMaterials", "Materials" })
        {
            var materials = component.GetOrDefault<FPackageIndex[]>(propertyName, []);
            foreach (var material in materials.Where(x => !x.IsNull))
                yield return material;
        }
    }

    private static bool TryLoadPackageIndex<T>(FPackageIndex? packageIndex, [NotNullWhen(true)] out T? loaded)
        where T : UObject
    {
        loaded = null;
        if (packageIndex is not { IsNull: false })
            return false;

        try
        {
            loaded = packageIndex.Load<T>();
            return loaded != null;
        }
        catch
        {
            // cooked 蓝图里有些组件模板会缺类或缺外部包；跳过单个引用，避免整包源索引失败。
            return false;
        }
    }

    private static void InsertPackageIndexObjectRelation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        UObject owner,
        FPackageIndex? targetIndex,
        string relationSource,
        string relationType,
        string? relationName)
    {
        if (targetIndex is not { IsNull: false })
            return;

        var targetPath = GetPackageIndexPath(targetIndex);
        var targetName = targetIndex.Name;
        var targetType = relationType;
        try
        {
            if (targetIndex.Load<UObject>() is { } loaded)
            {
                targetPath = loaded.GetPathName();
                targetName = loaded.Name;
                targetType = loaded.GetType().Name;
            }
        }
        catch
        {
            // 外部 Actor/流式关卡可能缺包或按需加载失败；保留 PPtr 名称，不能让整包索引中断。
        }

        InsertRelation(connection, transaction, sourcePath, owner.GetPathName(), relationType, targetPath, targetName);
        InsertComponentAssetRelation(
            connection,
            transaction,
            sourcePath,
            owner.GetPathName(),
            owner.GetType().Name,
            targetPath ?? targetName ?? owner.GetPathName(),
            targetType,
            targetName,
            relationName,
            relationSource,
            relationType,
            targetPath,
            targetName,
            null,
            null,
            CUE4Parse.UE4.Objects.Core.Math.FTransform.Identity);
    }

    private static void InsertSoftObjectPathRelation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        UObject owner,
        FSoftObjectPath? softPath,
        string relationSource,
        string relationType,
        string? relationName)
    {
        if (softPath is not { } value)
            return;

        var pathText = value.ToString();
        if (string.IsNullOrWhiteSpace(pathText))
            return;

        InsertRelation(connection, transaction, sourcePath, owner.GetPathName(), relationType, pathText, value.AssetPathName.Text);
        InsertComponentAssetRelation(
            connection,
            transaction,
            sourcePath,
            owner.GetPathName(),
            owner.GetType().Name,
            pathText,
            relationType,
            value.AssetPathName.Text,
            relationName,
            relationSource,
            relationType,
            pathText,
            value.AssetPathName.Text,
            null,
            null,
            CUE4Parse.UE4.Objects.Core.Math.FTransform.Identity);
    }

    private static IEnumerable<USCS_Node> GetAllSCSNodesSafe(USimpleConstructionScript script)
    {
        try
        {
            return script.GetAllNodesRecursive();
        }
        catch
        {
            return [];
        }
    }

    private static string? GetAttachParentPathSafe(USceneComponent component)
    {
        try
        {
            return component.GetAttachParent()?.GetPathName();
        }
        catch
        {
            return null;
        }
    }

    private static bool ShouldScanBlueprintPropertyReferences(UObject obj)
        => obj is UBlueprintGeneratedClass
           || obj.Flags.HasFlag(EObjectFlags.RF_ClassDefaultObject)
           || obj.Name.StartsWith("Default__", StringComparison.OrdinalIgnoreCase);

    private static void InsertObjectPropertyAssetRelations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        UObject obj,
        string relationSource)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in EnumeratePropertyAssetTargets(obj.Properties, "Property"))
        {
            if (!seen.Add($"{reference.PropertyPath}:{reference.Target.RelationType}:{reference.Target.TargetPath}"))
                continue;

            InsertRelation(connection, transaction, sourcePath, obj.GetPathName(), reference.Target.RelationType, reference.Target.TargetPath, reference.Target.TargetName);
            InsertComponentAssetRelation(
                connection,
                transaction,
                sourcePath,
                obj.GetPathName(),
                obj.GetType().Name,
                obj.GetPathName(),
                obj.GetType().Name,
                obj.Name,
                reference.PropertyPath,
                relationSource,
                reference.Target.RelationType,
                reference.Target.TargetPath,
                reference.Target.TargetName,
                null,
                null,
                CUE4Parse.UE4.Objects.Core.Math.FTransform.Identity);
        }
    }

    private static IEnumerable<PropertyAssetTarget> EnumeratePropertyAssetTargets(IEnumerable<FPropertyTag> properties, string pathPrefix)
    {
        foreach (var property in properties)
        {
            var propertyPath = $"{pathPrefix}.{property.Name.Text}";
            foreach (var target in EnumeratePropertyAssetTargets(property.Tag, propertyPath))
                yield return target;
        }
    }

    private static IEnumerable<ComponentReference> EnumerateComponentReferences(IEnumerable<FPropertyTag> properties, string pathPrefix)
    {
        foreach (var property in properties)
        {
            var propertyPath = $"{pathPrefix}.{property.Name.Text}";
            foreach (var target in EnumerateComponentReferences(property.Tag, propertyPath))
                yield return target;
        }
    }

    private static IEnumerable<ComponentReference> EnumerateComponentReferences(FPropertyTagType? tag, string propertyPath)
    {
        if (tag == null)
            yield break;

        foreach (var target in EnumerateComponentReferences(tag.GenericValue, propertyPath))
            yield return target;
    }

    private static IEnumerable<ComponentReference> EnumerateComponentReferences(object? value, string propertyPath)
    {
        switch (value)
        {
            case null:
                yield break;
            case FPackageIndex packageIndex when TryLoadPackageIndex(packageIndex, out USceneComponent? component):
                yield return new ComponentReference(propertyPath, component);
                yield break;
            case FStructFallback fallback:
                foreach (var nested in EnumerateComponentReferences(fallback.Properties, propertyPath))
                    yield return nested;
                yield break;
            case UScriptArray array:
                for (var index = 0; index < array.Properties.Count; index++)
                {
                    foreach (var nested in EnumerateComponentReferences(array.Properties[index], $"{propertyPath}[{index}]"))
                        yield return nested;
                }
                yield break;
            case UScriptMap map:
                var pairIndex = 0;
                foreach (var pair in map.Properties)
                {
                    foreach (var nested in EnumerateComponentReferences(pair.Key, $"{propertyPath}{{{pairIndex}}}.Key"))
                        yield return nested;
                    foreach (var nested in EnumerateComponentReferences(pair.Value, $"{propertyPath}{{{pairIndex}}}.Value"))
                        yield return nested;
                    pairIndex++;
                }
                yield break;
            case IEnumerable<FPackageIndex> packageIndexes:
                var packageIndexPosition = 0;
                foreach (var packageIndex in packageIndexes)
                {
                    if (TryLoadPackageIndex(packageIndex, out USceneComponent? component))
                        yield return new ComponentReference($"{propertyPath}[{packageIndexPosition}]", component);
                    packageIndexPosition++;
                }
                yield break;
        }
    }

    private static IEnumerable<PropertyAssetTarget> EnumeratePropertyAssetTargets(FPropertyTagType? tag, string propertyPath)
    {
        if (tag == null)
            yield break;

        foreach (var target in EnumeratePropertyAssetTargets(tag.GenericValue, propertyPath))
            yield return target;
    }

    private static IEnumerable<PropertyAssetTarget> EnumeratePropertyAssetTargets(object? value, string propertyPath)
    {
        switch (value)
        {
            case null:
                yield break;
            case FPackageIndex packageIndex when TryBuildUsefulAssetTarget(packageIndex, out var target):
                yield return new PropertyAssetTarget(propertyPath, target);
                yield break;
            case FStructFallback fallback:
                foreach (var nested in EnumeratePropertyAssetTargets(fallback.Properties, propertyPath))
                    yield return nested;
                yield break;
            case UScriptArray array:
                for (var index = 0; index < array.Properties.Count; index++)
                {
                    foreach (var nested in EnumeratePropertyAssetTargets(array.Properties[index], $"{propertyPath}[{index}]"))
                        yield return nested;
                }
                yield break;
            case UScriptMap map:
                var pairIndex = 0;
                foreach (var pair in map.Properties)
                {
                    foreach (var nested in EnumeratePropertyAssetTargets(pair.Key, $"{propertyPath}{{{pairIndex}}}.Key"))
                        yield return nested;
                    foreach (var nested in EnumeratePropertyAssetTargets(pair.Value, $"{propertyPath}{{{pairIndex}}}.Value"))
                        yield return nested;
                    pairIndex++;
                }
                yield break;
            case IEnumerable<FPackageIndex> packageIndexes:
                var packageIndexPosition = 0;
                foreach (var packageIndex in packageIndexes)
                {
                    if (TryBuildUsefulAssetTarget(packageIndex, out var target))
                        yield return new PropertyAssetTarget($"{propertyPath}[{packageIndexPosition}]", target);
                    packageIndexPosition++;
                }
                yield break;
        }
    }

    private static bool TryBuildUsefulAssetTarget(FPackageIndex packageIndex, out ComponentAssetTarget target)
    {
        target = default;
        if (packageIndex.IsNull)
            return false;

        UObject? loaded = null;
        try
        {
            loaded = packageIndex.Load<UObject>();
        }
        catch
        {
            // 这里不吞导出错误，只跳过单个属性引用；坏 PPtr 不应让整个源索引中断。
        }

        var relationType = loaded switch
        {
            UStaticMesh => "StaticMesh",
            USkeletalMesh => "SkeletalMesh",
            UMaterialInterface => "Material",
            UTexture => "Texture",
            UAnimationAsset => "Animation",
            USkeleton => "Skeleton",
            _ => ClassifyLoadedAssetReference(loaded)
        };

        if (relationType == null)
            return false;

        target = new ComponentAssetTarget(relationType, GetPackageIndexPath(packageIndex), loaded?.Name ?? packageIndex.Name);
        return true;
    }

    private static string? ClassifyLoadedAssetReference(UObject? loaded)
    {
        var exportType = loaded?.ExportType ?? loaded?.GetType().Name;
        if (string.IsNullOrWhiteSpace(exportType))
            return null;

        if (exportType.Contains("AnimBlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase))
            return "AnimClass";
        if (exportType.Contains("BlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase))
            return "BlueprintClass";

        return null;
    }

    private static void InsertComponentAssetRelation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        string ownerObjectPath,
        string ownerType,
        string componentObjectPath,
        string componentType,
        string? componentName,
        string? componentVariableName,
        string relationSource,
        string relationType,
        string? targetPath,
        string? targetName,
        string? socketName,
        string? parentComponentPath,
        CUE4Parse.UE4.Objects.Core.Math.FTransform transform)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO component_asset_relations (
                source_path, owner_object_path, owner_type,
                component_object_path, component_type, component_name, component_variable_name,
                relation_source, relation_type, target_path, target_name,
                socket_name, parent_component_path,
                location_x, location_y, location_z,
                rotation_pitch, rotation_yaw, rotation_roll,
                scale_x, scale_y, scale_z
            )
            VALUES (
                $sourcePath, $ownerObjectPath, $ownerType,
                $componentObjectPath, $componentType, $componentName, $componentVariableName,
                $relationSource, $relationType, $targetPath, $targetName,
                $socketName, $parentComponentPath,
                $locationX, $locationY, $locationZ,
                $rotationPitch, $rotationYaw, $rotationRoll,
                $scaleX, $scaleY, $scaleZ
            );
            """;
        Add(command, "$sourcePath", sourcePath);
        Add(command, "$ownerObjectPath", ownerObjectPath);
        Add(command, "$ownerType", ownerType);
        Add(command, "$componentObjectPath", componentObjectPath);
        Add(command, "$componentType", componentType);
        Add(command, "$componentName", componentName);
        Add(command, "$componentVariableName", componentVariableName);
        Add(command, "$relationSource", relationSource);
        Add(command, "$relationType", relationType);
        Add(command, "$targetPath", targetPath);
        Add(command, "$targetName", targetName);
        Add(command, "$socketName", socketName);
        Add(command, "$parentComponentPath", parentComponentPath);
        Add(command, "$locationX", transform.Translation.X);
        Add(command, "$locationY", transform.Translation.Y);
        Add(command, "$locationZ", transform.Translation.Z);
        Add(command, "$rotationPitch", transform.Rotation.Rotator().Pitch);
        Add(command, "$rotationYaw", transform.Rotation.Rotator().Yaw);
        Add(command, "$rotationRoll", transform.Rotation.Rotator().Roll);
        Add(command, "$scaleX", transform.Scale3D.X);
        Add(command, "$scaleY", transform.Scale3D.Y);
        Add(command, "$scaleZ", transform.Scale3D.Z);
        command.ExecuteNonQuery();
    }

    private readonly record struct ComponentAssetTarget(string RelationType, string? TargetPath, string? TargetName)
    {
        public static ComponentAssetTarget FromPackageIndex(string relationType, FPackageIndex packageIndex)
        {
            var loaded = packageIndex.Load<UObject>();
            return new ComponentAssetTarget(relationType, GetPackageIndexPath(packageIndex), loaded?.Name ?? packageIndex.Name);
        }
    }

    private readonly record struct ComponentReference(string PropertyPath, USceneComponent Component);

    private readonly record struct PropertyAssetTarget(string PropertyPath, ComponentAssetTarget Target);

    private static void InsertMontageSegments(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        UAnimMontage montage,
        string? skeletonPath)
    {
        var segmentIndex = 0;
        foreach (var slotTrack in montage.SlotAnimTracks)
        {
            foreach (var segment in slotTrack.AnimTrack.AnimSegments)
            {
                InsertAnimationSegment(
                    connection,
                    transaction,
                    sourcePath,
                    montage.GetPathName(),
                    skeletonPath,
                    segmentIndex++,
                    slotTrack.SlotName.Text,
                    segment,
                    "MontageSlot");
            }
        }

        for (var sectionIndex = 0; sectionIndex < montage.CompositeSections.Length; sectionIndex++)
            InsertAnimationSection(connection, transaction, sourcePath, montage.GetPathName(), sectionIndex, montage.CompositeSections[sectionIndex]);
    }

    private static void InsertCompositeSegments(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        UAnimComposite composite,
        string? skeletonPath)
    {
        for (var segmentIndex = 0; segmentIndex < composite.AnimationTrack.AnimSegments.Length; segmentIndex++)
        {
            InsertAnimationSegment(
                connection,
                transaction,
                sourcePath,
                composite.GetPathName(),
                skeletonPath,
                segmentIndex,
                null,
                composite.AnimationTrack.AnimSegments[segmentIndex],
                "CompositeTrack");
        }
    }

    private static void InsertAnimationSegment(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        string animationObjectPath,
        string? skeletonPath,
        int segmentIndex,
        string? slotName,
        FAnimSegment segment,
        string relationSource)
    {
        var referencedAnimationPath = GetPackageIndexPath(segment.AnimReference);
        var referencedAnimation = segment.AnimReference.Load<UAnimSequenceBase>();
        if (!string.IsNullOrWhiteSpace(referencedAnimationPath))
            InsertRelation(connection, transaction, sourcePath, animationObjectPath, "AnimationSegment", referencedAnimationPath, referencedAnimation?.Name);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO animation_segments (
                source_path, animation_object_path, skeleton_path, segment_index, slot_name,
                referenced_animation_path, referenced_animation_name,
                start_pos, anim_start_time, anim_end_time, play_rate, looping_count, length,
                relation_source
            )
            VALUES (
                $sourcePath, $animationObjectPath, $skeletonPath, $segmentIndex, $slotName,
                $referencedAnimationPath, $referencedAnimationName,
                $startPos, $animStartTime, $animEndTime, $playRate, $loopingCount, $length,
                $relationSource
            );
            """;
        Add(command, "$sourcePath", sourcePath);
        Add(command, "$animationObjectPath", animationObjectPath);
        Add(command, "$skeletonPath", skeletonPath);
        Add(command, "$segmentIndex", segmentIndex);
        Add(command, "$slotName", slotName);
        Add(command, "$referencedAnimationPath", referencedAnimationPath);
        Add(command, "$referencedAnimationName", referencedAnimation?.Name);
        Add(command, "$startPos", segment.StartPos);
        Add(command, "$animStartTime", segment.AnimStartTime);
        Add(command, "$animEndTime", segment.AnimEndTime);
        Add(command, "$playRate", segment.AnimPlayRate);
        Add(command, "$loopingCount", segment.LoopingCount);
        Add(command, "$length", segment.GetLength());
        Add(command, "$relationSource", relationSource);
        command.ExecuteNonQuery();
    }

    private static void InsertAnimationSection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        string animationObjectPath,
        int sectionIndex,
        FCompositeSection section)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO animation_sections (
                source_path, animation_object_path, section_index, section_name, next_section_name,
                slot_index, segment_index, segment_begin_time, link_method, cached_link_method
            )
            VALUES (
                $sourcePath, $animationObjectPath, $sectionIndex, $sectionName, $nextSectionName,
                $slotIndex, $segmentIndex, $segmentBeginTime, $linkMethod, $cachedLinkMethod
            );
            """;
        Add(command, "$sourcePath", sourcePath);
        Add(command, "$animationObjectPath", animationObjectPath);
        Add(command, "$sectionIndex", sectionIndex);
        Add(command, "$sectionName", section.SectionName.Text);
        Add(command, "$nextSectionName", section.NextSectionName.Text);
        Add(command, "$slotIndex", section.SlotIndex);
        Add(command, "$segmentIndex", section.SegmentIndex);
        Add(command, "$segmentBeginTime", section.SegmentBeginTime);
        Add(command, "$linkMethod", section.LinkMethod.ToString());
        Add(command, "$cachedLinkMethod", section.CachedLinkMethod.ToString());
        command.ExecuteNonQuery();
    }

    private static void InsertSkeletonBones(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        string ownerObjectPath,
        string ownerType,
        string? skeletonPath,
        IReadOnlyList<FMeshBoneInfo> bones)
    {
        for (var index = 0; index < bones.Count; index++)
        {
            var bone = bones[index];
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO skeleton_bones (
                    source_path, owner_object_path, owner_type, skeleton_path,
                    bone_index, bone_name, parent_index
                )
                VALUES (
                    $sourcePath, $ownerObjectPath, $ownerType, $skeletonPath,
                    $boneIndex, $boneName, $parentIndex
                );
                """;
            Add(command, "$sourcePath", sourcePath);
            Add(command, "$ownerObjectPath", ownerObjectPath);
            Add(command, "$ownerType", ownerType);
            Add(command, "$skeletonPath", skeletonPath);
            Add(command, "$boneIndex", index);
            Add(command, "$boneName", bone.Name.Text);
            Add(command, "$parentIndex", bone.ParentIndex);
            command.ExecuteNonQuery();
        }
    }

    private static int CountSockets(UObject obj)
        => obj switch
        {
            UStaticMesh staticMesh => staticMesh.Sockets.Length,
            USkeletalMesh skeletalMesh => skeletalMesh.Sockets.Length,
            USkeleton skeleton => skeleton.Sockets.Length,
            _ => 0,
        };

    private static void InsertStaticMeshSockets(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        string ownerObjectPath,
        string ownerType,
        FPackageIndex[] sockets)
    {
        for (var socketIndex = 0; socketIndex < sockets.Length; socketIndex++)
        {
            var socket = sockets[socketIndex].Load<UStaticMeshSocket>();
            if (socket == null)
                continue;

            InsertMeshSocket(
                connection,
                transaction,
                sourcePath,
                ownerObjectPath,
                ownerType,
                socketIndex,
                socket.SocketName.Text,
                null,
                GetPackageIndexPath(sockets[socketIndex]),
                socket.RelativeLocation,
                socket.RelativeRotation,
                socket.RelativeScale);
        }
    }

    private static void InsertSkeletalMeshSockets(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        string ownerObjectPath,
        string ownerType,
        FPackageIndex[] sockets)
    {
        for (var socketIndex = 0; socketIndex < sockets.Length; socketIndex++)
        {
            var socket = sockets[socketIndex].Load<USkeletalMeshSocket>();
            if (socket == null)
                continue;

            InsertMeshSocket(
                connection,
                transaction,
                sourcePath,
                ownerObjectPath,
                ownerType,
                socketIndex,
                socket.SocketName.Text,
                socket.BoneName.Text,
                GetPackageIndexPath(sockets[socketIndex]),
                socket.RelativeLocation,
                socket.RelativeRotation,
                socket.RelativeScale);
        }
    }

    private static void InsertMeshSocket(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        string ownerObjectPath,
        string ownerType,
        int socketIndex,
        string socketName,
        string? boneName,
        string? socketObjectPath,
        CUE4Parse.UE4.Objects.Core.Math.FVector location,
        CUE4Parse.UE4.Objects.Core.Math.FRotator rotation,
        CUE4Parse.UE4.Objects.Core.Math.FVector scale)
    {
        if (!string.IsNullOrWhiteSpace(socketObjectPath))
            InsertRelation(connection, transaction, sourcePath, ownerObjectPath, "Socket", socketObjectPath, socketName);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mesh_sockets (
                source_path, owner_object_path, owner_type, socket_index,
                socket_name, bone_name, socket_object_path,
                location_x, location_y, location_z,
                rotation_pitch, rotation_yaw, rotation_roll,
                scale_x, scale_y, scale_z
            )
            VALUES (
                $sourcePath, $ownerObjectPath, $ownerType, $socketIndex,
                $socketName, $boneName, $socketObjectPath,
                $locationX, $locationY, $locationZ,
                $rotationPitch, $rotationYaw, $rotationRoll,
                $scaleX, $scaleY, $scaleZ
            );
            """;
        Add(command, "$sourcePath", sourcePath);
        Add(command, "$ownerObjectPath", ownerObjectPath);
        Add(command, "$ownerType", ownerType);
        Add(command, "$socketIndex", socketIndex);
        Add(command, "$socketName", socketName);
        Add(command, "$boneName", boneName);
        Add(command, "$socketObjectPath", socketObjectPath);
        Add(command, "$locationX", location.X);
        Add(command, "$locationY", location.Y);
        Add(command, "$locationZ", location.Z);
        Add(command, "$rotationPitch", rotation.Pitch);
        Add(command, "$rotationYaw", rotation.Yaw);
        Add(command, "$rotationRoll", rotation.Roll);
        Add(command, "$scaleX", scale.X);
        Add(command, "$scaleY", scale.Y);
        Add(command, "$scaleZ", scale.Z);
        command.ExecuteNonQuery();
    }

    private static void InsertAnimationTracks(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        UAnimSequence sequence,
        string? skeletonPath)
    {
        var skeletonBones = sequence.Skeleton.Load<USkeleton>()?.ReferenceSkeleton.FinalRefBoneInfo ?? [];
        var trackMap = sequence.GetTrackMap();
        for (var trackIndex = 0; trackIndex < trackMap.Length; trackIndex++)
        {
            var boneIndex = trackMap[trackIndex].BoneTreeIndex;
            var boneName = boneIndex >= 0 && boneIndex < skeletonBones.Length
                ? skeletonBones[boneIndex].Name.Text
                : null;

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO animation_tracks (
                    source_path, animation_object_path, skeleton_path,
                    track_index, bone_index, bone_name
                )
                VALUES (
                    $sourcePath, $animationObjectPath, $skeletonPath,
                    $trackIndex, $boneIndex, $boneName
                );
                """;
            Add(command, "$sourcePath", sourcePath);
            Add(command, "$animationObjectPath", sequence.GetPathName());
            Add(command, "$skeletonPath", skeletonPath);
            Add(command, "$trackIndex", trackIndex);
            Add(command, "$boneIndex", boneIndex);
            Add(command, "$boneName", boneName);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertAnimationNotifies(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        UAnimSequenceBase sequence)
    {
        for (var notifyIndex = 0; notifyIndex < sequence.Notifies.Length; notifyIndex++)
        {
            var notify = sequence.Notifies[notifyIndex];
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO animation_notifies (
                    source_path, animation_object_path, notify_index, notify_name,
                    notify_object_path, notify_state_object_path, link_value, duration,
                    track_index, trigger_chance, montage_tick_type, link_method,
                    segment_index, slot_index
                )
                VALUES (
                    $sourcePath, $animationObjectPath, $notifyIndex, $notifyName,
                    $notifyObjectPath, $notifyStateObjectPath, $linkValue, $duration,
                    $trackIndex, $triggerChance, $montageTickType, $linkMethod,
                    $segmentIndex, $slotIndex
                );
                """;
            Add(command, "$sourcePath", sourcePath);
            Add(command, "$animationObjectPath", sequence.GetPathName());
            Add(command, "$notifyIndex", notifyIndex);
            Add(command, "$notifyName", notify.NotifyName.Text);
            Add(command, "$notifyObjectPath", GetPackageIndexPath(notify.Notify));
            Add(command, "$notifyStateObjectPath", GetPackageIndexPath(notify.NotifyStateClass));
            Add(command, "$linkValue", notify.LinkValue);
            Add(command, "$duration", notify.Duration);
            Add(command, "$trackIndex", notify.TrackIndex);
            Add(command, "$triggerChance", notify.NotifyTriggerChance);
            Add(command, "$montageTickType", notify.MontageTickType.ToString());
            Add(command, "$linkMethod", notify.LinkMethod.ToString());
            Add(command, "$segmentIndex", notify.SegmentIndex);
            Add(command, "$slotIndex", notify.SlotIndex);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertAnimationCurves(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        UAnimSequence sequence)
    {
        var curves = sequence.CompressedCurveData?.FloatCurves ?? [];
        for (var curveIndex = 0; curveIndex < curves.Length; curveIndex++)
        {
            var curve = curves[curveIndex];
            var keys = curve.FloatCurve.Keys ?? [];
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO animation_curves (
                    source_path, animation_object_path, curve_index, curve_name,
                    curve_type_flags, key_count, min_time, max_time, min_value, max_value
                )
                VALUES (
                    $sourcePath, $animationObjectPath, $curveIndex, $curveName,
                    $curveTypeFlags, $keyCount, $minTime, $maxTime, $minValue, $maxValue
                );
                """;
            Add(command, "$sourcePath", sourcePath);
            Add(command, "$animationObjectPath", sequence.GetPathName());
            Add(command, "$curveIndex", curveIndex);
            Add(command, "$curveName", curve.CurveName.Text);
            Add(command, "$curveTypeFlags", curve.CurveTypeFlags);
            Add(command, "$keyCount", keys.Length);
            Add(command, "$minTime", keys.Length == 0 ? null : keys.Min(x => x.Time));
            Add(command, "$maxTime", keys.Length == 0 ? null : keys.Max(x => x.Time));
            Add(command, "$minValue", keys.Length == 0 ? null : keys.Min(x => x.Value));
            Add(command, "$maxValue", keys.Length == 0 ? null : keys.Max(x => x.Value));
            command.ExecuteNonQuery();
        }
    }

    private static void InsertMaterialTextureSlots(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourcePath,
        UMaterialInterface material)
    {
        var materialObjectPath = material.GetPathName();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (material is UMaterialInstanceConstant materialInstance)
        {
            foreach (var parameter in materialInstance.TextureParameterValues)
            {
                var texture = parameter.ParameterValue.Load<UTexture>();
                if (texture == null)
                    continue;

                InsertMaterialTextureSlot(
                    connection,
                    transaction,
                    seen,
                    sourcePath,
                    materialObjectPath,
                    material.Name,
                    parameter.Name,
                    GetPackageIndexPath(parameter.ParameterValue),
                    texture,
                    "DirectParameter");
            }
        }

        try
        {
            var parameters = new CMaterialParams2();
            material.GetParams(parameters, EMaterialFormat.AllLayers);
            foreach (var (slotName, textureMaterial) in parameters.Textures)
            {
                if (textureMaterial is not UTexture texture)
                    continue;

                InsertMaterialTextureSlot(
                    connection,
                    transaction,
                    seen,
                    sourcePath,
                    materialObjectPath,
                    material.Name,
                    slotName,
                    texture.GetPathName(),
                    texture,
                    "ResolvedParams");
            }
        }
        catch (Exception ex)
        {
            InsertError(connection, transaction, sourcePath, $"Material texture slots failed: {materialObjectPath}: {ex.Message}");
        }

        if (material is not UMaterial baseMaterial)
            return;

        foreach (var texture in baseMaterial.ReferencedTextures.Where(x => x != null))
        {
            InsertMaterialTextureSlot(
                connection,
                transaction,
                seen,
                sourcePath,
                materialObjectPath,
                material.Name,
                texture.Name,
                texture.GetPathName(),
                texture,
                "ReferencedTexture");
        }
    }

    private static void InsertMaterialTextureSlot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HashSet<string> seen,
        string sourcePath,
        string materialObjectPath,
        string materialName,
        string slotName,
        string? texturePath,
        UTexture texture,
        string relationSource)
    {
        var textureObjectPath = texture.GetPathName();
        var key = $"{slotName}\n{textureObjectPath}\n{relationSource}";
        if (!seen.Add(key))
            return;

        InsertRelation(connection, transaction, sourcePath, materialObjectPath, "Texture", textureObjectPath, texture.Name);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO material_texture_slots (
                source_path, material_object_path, material_name, slot_name,
                texture_path, texture_name, texture_object_path, relation_source
            )
            VALUES (
                $sourcePath, $materialObjectPath, $materialName, $slotName,
                $texturePath, $textureName, $textureObjectPath, $relationSource
            );
            """;
        Add(command, "$sourcePath", sourcePath);
        Add(command, "$materialObjectPath", materialObjectPath);
        Add(command, "$materialName", materialName);
        Add(command, "$slotName", slotName);
        Add(command, "$texturePath", texturePath);
        Add(command, "$textureName", texture.Name);
        Add(command, "$textureObjectPath", textureObjectPath);
        Add(command, "$relationSource", relationSource);
        command.ExecuteNonQuery();
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
