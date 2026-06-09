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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    public static void Run(string libraryRoot, bool dedupeTextures)
    {
        if (string.IsNullOrWhiteSpace(libraryRoot))
            throw new ArgumentException("Library root is required.", nameof(libraryRoot));

        var root = Path.GetFullPath(libraryRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Library root not found: {root}");

        Console.WriteLine($"UE Library postprocess root: {root}");
        var glbFiles = Directory.EnumerateFiles(root, "*.glb", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var materialJsonFiles = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(IsLikelyMaterialJson)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Console.WriteLine($"Scanning {glbFiles.Length} GLB model(s), {materialJsonFiles.Length} material JSON file(s).");
        var materialIndex = LoadMaterialIndex(root, materialJsonFiles);
        var reports = new List<ModelValidationEntry>(glbFiles.Length);
        var catalogRows = new List<JObject>(glbFiles.Length + materialIndex.Count);

        foreach (var glbPath in glbFiles)
        {
            var report = InspectModel(root, glbPath, materialIndex);
            reports.Add(report);
            catalogRows.Add(BuildModelCatalogRow(report));
            WriteAssetReadme(root, report);
        }

        foreach (var material in materialIndex.Values.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
            catalogRows.Add(BuildMaterialCatalogRow(material));

        var textureLinks = dedupeTextures ? DeduplicateTextureFilesCore(root) : [];
        foreach (var texture in textureLinks.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
            catalogRows.Add(BuildTextureCatalogRow(texture));

        var mergedCatalogRows = WriteAssetCatalog(root, catalogRows);
        var modelAnimationRelations = WriteModelAnimationRelations(root, mergedCatalogRows);
        WriteModelValidation(root, reports);
        WriteSkeletonIndex(root, reports);
        WriteLibraryIndexDb(root, mergedCatalogRows, reports, textureLinks, modelAnimationRelations);
        WriteLibraryReadme(root, reports, materialIndex.Values);

        Console.WriteLine($"UE Library postprocess finished: {root}");
    }

    private static bool IsLikelyMaterialJson(string path)
    {
        try
        {
            using var reader = File.OpenText(path);
            using var jsonReader = new JsonTextReader(reader);
            var token = JToken.ReadFrom(jsonReader);
            return token is JObject obj && obj["Parameters"] is JObject;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, MaterialInfo> LoadMaterialIndex(string root, string[] materialJsonFiles)
    {
        var result = new Dictionary<string, MaterialInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in materialJsonFiles)
        {
            try
            {
                var obj = JObject.Parse(File.ReadAllText(path));
                var textures = obj["Textures"] as JObject;
                var parameters = obj["Parameters"] as JObject;
                var colors = parameters?["Colors"] as JObject;
                var scalars = parameters?["Scalars"] as JObject;
                var switches = parameters?["Switches"] as JObject;
                var name = Path.GetFileNameWithoutExtension(path);
                var info = new MaterialInfo
                {
                    Name = name,
                    Path = path,
                    RelativePath = MakeRelative(root, path),
                    TextureSlotCount = textures?.Properties().Count() ?? 0,
                    ColorCount = colors?.Properties().Count() ?? 0,
                    ScalarCount = scalars?.Properties().Count() ?? 0,
                    SwitchCount = switches?.Properties().Count() ?? 0,
                    BlendMode = parameters?["BlendMode"]?.ToString(),
                    ShadingModel = parameters?["ShadingModel"]?.ToString(),
                    RawJson = obj,
                };
                if (!result.ContainsKey(name))
                    result[name] = info;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARN: material JSON skipped {path} ({ex.Message})");
            }
        }

        return result;
    }

    private static ModelValidationEntry InspectModel(
        string root,
        string glbPath,
        Dictionary<string, MaterialInfo> materialIndex)
    {
        var notes = new List<string>();
        JObject gltf;
        byte[] binData;
        try
        {
            (gltf, binData) = ReadGlb(glbPath);
        }
        catch (Exception ex)
        {
            return new ModelValidationEntry
            {
                Status = "error",
                Path = glbPath,
                RelativePath = MakeRelative(root, glbPath),
                Name = Path.GetFileNameWithoutExtension(glbPath),
                Notes = [$"GLB parse failed: {ex.Message}"],
            };
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

        return new ModelValidationEntry
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
    }

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
            sourceType = report.SkinCount > 0 ? "SkeletalOrSkinnedMeshGLB" : "StaticMeshGLB",
            source = report.RelativePath,
            output = report.RelativePath,
            format = "glb",
            meshCount = report.MeshCount,
            materialCount = report.MaterialCount,
            textureCount = report.ImageCount,
            boneCount = report.BoneCount,
            skinCount = report.SkinCount,
            animationCount = report.AnimationCount,
            skeletonHash = report.SkeletonHash,
            materialNames = report.MaterialNames,
            materialSidecars = report.MatchedMaterialSidecars,
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

    private static List<JObject> WriteAssetCatalog(string root, List<JObject> rows)
    {
        var path = Path.Combine(root, "asset_catalog.jsonl");
        var mergedRows = MergeExistingCatalogRows(root, path, rows);
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        foreach (var row in mergedRows)
            writer.WriteLine(row.ToString(Formatting.None));
        return mergedRows;
    }

    private static List<JObject> MergeExistingCatalogRows(string root, string catalogPath, List<JObject> generatedRows)
    {
        var result = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(catalogPath))
        {
            foreach (var line in File.ReadLines(catalogPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var row = JObject.Parse(line);
                    result[BuildCatalogKey(root, row)] = row;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WARN: old catalog row skipped ({ex.Message})");
                }
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
            .OrderBy(x => (string?)x["kind"], StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => (string?)x["output"] ?? (string?)x["source"], StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private static JObject WriteModelAnimationRelations(string root, List<JObject> catalogRows)
    {
        var models = catalogRows
            .Where(x => string.Equals((string?)x["kind"], "Model", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace((string?)x["skeletonPath"]))
            .ToArray();
        var animations = catalogRows
            .Where(x => string.Equals((string?)x["kind"], "Animation", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace((string?)x["skeletonPath"]))
            .ToArray();

        var animationGroups = animations
            .GroupBy(x => (string)x["skeletonPath"]!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

        var relations = new JArray();
        foreach (var model in models.OrderBy(x => (string?)x["output"], StringComparer.OrdinalIgnoreCase))
        {
            var skeletonPath = (string)model["skeletonPath"]!;
            animationGroups.TryGetValue(skeletonPath, out var matchedAnimations);
            relations.Add(JObject.FromObject(new
            {
                model = model["output"],
                modelName = model["name"],
                modelSource = model["source"],
                skeletonPath,
                skeletonName = model["skeletonName"],
                confidence = matchedAnimations is { Length: > 0 } ? "ExplicitSkeleton" : "NoMatchingAnimationExported",
                animations = (matchedAnimations ?? [])
                    .OrderBy(x => (string?)x["output"], StringComparer.OrdinalIgnoreCase)
                    .Select(x => new
                    {
                        name = x["name"],
                        source = x["source"],
                        output = x["output"],
                        status = x["status"],
                        duration = x["duration"],
                        frameCount = x["frameCount"],
                        trackCount = x["trackCount"],
                    })
                    .ToArray(),
            }));
        }

        var summary = new JObject
        {
            ["generatedAt"] = DateTime.UtcNow.ToString("O"),
            ["rule"] = "只按 UE Skeleton 原始引用匹配模型和动画；不按目录名、角色名或文件名前缀硬猜。",
            ["totals"] = JObject.FromObject(new
            {
                models = models.Length,
                animations = animations.Length,
                matchedModels = relations.Count(x => ((JArray)x["animations"]!).Count > 0),
            }),
            ["relations"] = relations,
        };

        File.WriteAllText(Path.Combine(root, "model_animations.json"), summary.ToString(Formatting.Indented));
        return summary;
    }

    private static void WriteModelValidation(string root, List<ModelValidationEntry> reports)
    {
        var summary = new
        {
            generatedAt = DateTime.UtcNow.ToString("O"),
            rule = "验证 GLB 静态结构、材质、贴图和 skin。动画正确性需要后续 UE 动画索引和预览验证。",
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
                    animations = x.AnimationCount,
                },
                skeletonHash = x.SkeletonHash,
                bbox = x.BBox,
                notes = x.Notes,
            }),
        };
        File.WriteAllText(
            Path.Combine(root, "model_validation.json"),
            JsonConvert.SerializeObject(summary, Formatting.Indented),
            Encoding.UTF8);
    }

    private static void WriteLibraryIndexDb(
        string root,
        List<JObject> catalogRows,
        List<ModelValidationEntry> reports,
        List<TextureLinkInfo> textureLinks,
        JObject modelAnimationRelations)
    {
        var dbPath = Path.Combine(root, "library_index.db");
        if (File.Exists(dbPath))
            File.Delete(dbPath);

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
                bbox_json TEXT,
                notes_json TEXT NOT NULL
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
            CREATE TABLE relation_animations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                relation_id INTEGER NOT NULL,
                name TEXT,
                source TEXT,
                output TEXT,
                status TEXT,
                duration REAL,
                frame_count INTEGER,
                track_count INTEGER,
                FOREIGN KEY (relation_id) REFERENCES model_animation_relations(id)
            );
            """);
        Execute(connection, transaction, "CREATE INDEX idx_assets_kind ON assets(kind, resource_kind);");
        Execute(connection, transaction, "CREATE INDEX idx_assets_skeleton ON assets(skeleton_path);");
        Execute(connection, transaction, "CREATE INDEX idx_texture_hash ON texture_links(sha256);");
        Execute(connection, transaction, "CREATE INDEX idx_relations_skeleton ON model_animation_relations(skeleton_path);");

        foreach (var row in catalogRows)
            InsertAsset(connection, transaction, row);

        foreach (var link in textureLinks)
            InsertTextureLink(connection, transaction, link);

        foreach (var report in reports)
            InsertModelValidation(connection, transaction, report);

        InsertModelAnimationRelations(connection, transaction, modelAnimationRelations);

        transaction.Commit();
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

    private static void InsertModelValidation(SqliteConnection connection, SqliteTransaction transaction, ModelValidationEntry report)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO model_validation (
                path, name, resource_kind, status, mesh_count, material_count, texture_count,
                skin_count, bone_count, animation_count, skeleton_hash, bbox_json, notes_json
            )
            VALUES (
                $path, $name, $resourceKind, $status, $meshCount, $materialCount, $textureCount,
                $skinCount, $boneCount, $animationCount, $skeletonHash, $bboxJson, $notesJson
            );
            """;
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
        Add(command, "$bboxJson", report.BBox == null ? null : JsonConvert.SerializeObject(report.BBox));
        Add(command, "$notesJson", JsonConvert.SerializeObject(report.Notes));
        command.ExecuteNonQuery();
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
            Add(command, "$rawJson", relationObj.ToString(Formatting.None));
            var relationId = (long)command.ExecuteScalar()!;

            foreach (var animation in animations.OfType<JObject>())
                InsertRelationAnimation(connection, transaction, relationId, animation);
        }
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
                relation_id, name, source, output, status, duration, frame_count, track_count
            )
            VALUES (
                $relationId, $name, $source, $output, $status, $duration, $frameCount, $trackCount
            );
            """;
        Add(command, "$relationId", relationId);
        Add(command, "$name", (string?)animation["name"]);
        Add(command, "$source", (string?)animation["source"]);
        Add(command, "$output", (string?)animation["output"]);
        Add(command, "$status", (string?)animation["status"]);
        Add(command, "$duration", (double?)animation["duration"]);
        Add(command, "$frameCount", (int?)animation["frameCount"]);
        Add(command, "$trackCount", (int?)animation["trackCount"]);
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

    private static void WriteSkeletonIndex(string root, List<ModelValidationEntry> reports)
    {
        var skeletons = reports
            .Where(x => !string.IsNullOrWhiteSpace(x.SkeletonHash))
            .GroupBy(x => x.SkeletonHash!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Count())
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                skeletonId = group.Key,
                modelCount = group.Count(),
                boneCount = group.First().BoneCount,
                relationBasis = "glTF skin joints exported from UE SkeletalMesh",
                models = group
                    .OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .Select(x => new
                    {
                        name = x.Name,
                        output = x.RelativePath,
                        resourceKind = x.ResourceKind,
                    })
                    .ToArray(),
                boneNames = group.First().BoneNames.Take(256).ToArray(),
                boneNamesTruncated = group.First().BoneNames.Length > 256,
            })
            .ToArray();

        File.WriteAllText(
            Path.Combine(root, "skeletons.json"),
            JsonConvert.SerializeObject(new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                rule = "骨架分组来自已导出 GLB skin joints。后续应由 UE 源索引补充 USkeleton 路径和动画关系。",
                skeletonCount = skeletons.Length,
                skeletons,
            }, Formatting.Indented),
            Encoding.UTF8);
    }

    private static void WriteLibraryReadme(
        string root,
        List<ModelValidationEntry> reports,
        IEnumerable<MaterialInfo> materials)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# UE Asset Library");
        sb.AppendLine();
        sb.AppendLine("这份目录由 UnrealExporter 导出主链路和素材库索引步骤生成。当前阶段重点验证 GLB、材质 JSON、贴图硬链接、骨骼和动画 Skeleton 关系。");
        sb.AppendLine();
        sb.AppendLine("## 统计");
        sb.AppendLine();
        sb.AppendLine($"- 模型: `{reports.Count}`");
        sb.AppendLine($"- 带 skin/骨骼模型: `{reports.Count(x => x.SkinCount > 0)}`");
        sb.AppendLine($"- 材质 JSON: `{materials.Count()}`");
        sb.AppendLine($"- GLB 内动画: `{reports.Count(x => x.AnimationCount > 0)}`");
        sb.AppendLine();
        sb.AppendLine("## 索引文件");
        sb.AppendLine();
        sb.AppendLine("| 文件 | 用途 |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine("| `asset_catalog.jsonl` | 模型、材质、贴图、动画主索引，一行一个资产。 |");
        sb.AppendLine("| `library_index.db` | 已导出素材库的 SQLite 索引，便于筛选模型、动画、贴图和关系。 |");
        sb.AppendLine("| `ue_source_index.db` | 启用源索引时生成，记录完整源文件表、已检查对象和 UE 原始 Skeleton/Material 关系。 |");
        sb.AppendLine("| `export_manifest.jsonl` | 实际导出文件与 UE 源包/对象的对应关系。 |");
        sb.AppendLine("| `animation_bindings.jsonl` | 动画源对象、Skeleton、帧数、track 和导出状态。 |");
        sb.AppendLine("| `model_animations.json` | 只按 UE Skeleton 原始引用生成的模型动画匹配。 |");
        sb.AppendLine("| `model_validation.json` | GLB 静态结构、材质、贴图、skin 验证报告。 |");
        sb.AppendLine("| `skeletons.json` | 按 GLB skin joints 生成的骨架分组。 |");
        sb.AppendLine("| `texture_links.jsonl` | 原贴图文件、共享贴图、sha256 和硬链接状态。 |");
        sb.AppendLine("| `Textures/_Shared` | 启用硬链接去重后生成的共享贴图库。 |");
        sb.AppendLine();
        sb.AppendLine("## 下一步");
        sb.AppendLine();
        sb.AppendLine("- 建立 SQLite UE 源索引，批量记录 `UAnimSequence`、`USkeleton`、`SkeletalMesh`、材质与贴图的原生关系。");
        sb.AppendLine("- 增加动画采样预览验证，检查 track 覆盖率、骨架兼容和播放姿态。");
        sb.AppendLine("- 生成模型 + 动画的 glTF 预览并写 `preview_validation.json`。");
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
        sb.AppendLine("机器索引以素材库根目录的 `asset_catalog.jsonl`、`model_validation.json`、`skeletons.json` 为准。");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    public static void DeduplicateTextureFiles(string root)
    {
        DeduplicateTextureFilesCore(root);
    }

    private static List<TextureLinkInfo> DeduplicateTextureFilesCore(string root)
    {
        var textureFiles = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(x => TextureExtensions.Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase))
            .Where(x => !MakeRelative(root, x).Replace('\\', '/').StartsWith("Textures/_Shared/", StringComparison.OrdinalIgnoreCase))
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

        WriteTextureLinks(root, links);
        File.WriteAllText(
            Path.Combine(root, "texture_dedupe_summary.json"),
            JsonConvert.SerializeObject(new
            {
                generatedAt = DateTime.UtcNow.ToString("O"),
                rule = "重复 PNG/HDR 统一复制到 Textures/_Shared，再把重复文件替换为硬链接。GLB 内嵌贴图暂不改写。",
                scanned = textureFiles.Length,
                unique = byHash.Count,
                copiedToShared = copied,
                hardLinkedFiles = linked,
                note = "所有原 PNG/HDR 文件都会尽量替换为指向 Textures/_Shared 的硬链接；GLB 内嵌贴图暂不改写。",
            }, Formatting.Indented),
            Encoding.UTF8);
        Console.WriteLine($"Texture dedupe finished: scanned={textureFiles.Length}, unique={byHash.Count}, linked={linked}");
        return links;
    }

    private static void WriteTextureLinks(string root, List<TextureLinkInfo> links)
    {
        var path = Path.Combine(root, "texture_links.jsonl");
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        foreach (var link in links.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteLine(JsonConvert.SerializeObject(new
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
        if (text.Contains("/characters/") || text.Contains("/character/"))
            return "Character";
        if (text.Contains("/vehicle") || text.Contains("/vehicles/"))
            return "Vehicle";
        if (text.Contains("/weapon") || text.Contains("/weapons/") || text.Contains("/gadgets/"))
            return "Weapon";
        if (text.Contains("/environment/") || text.Contains("/scenery/") || text.Contains("/building/") || text.Contains("/plants/"))
            return "Environment";
        if (text.Contains("/props/") || text.Contains("/prop/") || text.Contains("/collectable"))
            return "Prop";
        return "Unknown";
    }

    private static string MakeRelative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string HashFile(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
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
        public object? BBox { get; set; }
        public string[] Notes { get; set; } = [];
    }
}
