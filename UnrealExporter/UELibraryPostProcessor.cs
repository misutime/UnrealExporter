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
        var modelFiles = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(IsSupportedGltfModel)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var materialJsonFiles = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Where(IsLikelyMaterialJson)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Console.WriteLine($"Scanning {modelFiles.Length} glTF model(s), {materialJsonFiles.Length} material JSON file(s).");
        var materialIndex = LoadMaterialIndex(root, materialJsonFiles);
        var reports = new List<ModelValidationEntry>(modelFiles.Length);
        var catalogRows = new List<JObject>(modelFiles.Length + materialIndex.Count);

        foreach (var glbPath in modelFiles)
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
        var sourceIndex = LoadSourceIndex(root);
        var materialTextureSlots = WriteMaterialTextureSlotLinks(root, materialIndex, textureLinks, sourceIndex);
        var sharedGltfTextureLinks = RewriteGltfSharedTextureUris(root, reports, materialTextureSlots);
        var componentAssetRelations = WriteComponentAssetRelations(root, mergedCatalogRows, sourceIndex);
        var packageObjectMaps = WritePackageObjectMaps(root, sourceIndex);
        var animationValidation = WriteAnimationValidation(root, mergedCatalogRows, sourceIndex);
        var modelAnimationRelations = WriteModelAnimationRelations(root, mergedCatalogRows, animationValidation);
        WriteModelValidation(root, reports);
        var skeletonGroups = WriteSkeletonIndex(root, reports, mergedCatalogRows, sourceIndex);
        WriteLibraryIndexDb(root, mergedCatalogRows, reports, textureLinks, materialTextureSlots, sharedGltfTextureLinks, componentAssetRelations, packageObjectMaps, skeletonGroups, modelAnimationRelations, animationValidation);
        WriteLibraryReadme(root, reports, materialIndex.Values, componentAssetRelations, packageObjectMaps);

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

    private static bool IsSupportedGltfModel(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".glb", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".gltf", StringComparison.OrdinalIgnoreCase);
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
            (gltf, binData) = ReadGltfModel(glbPath);
        }
        catch (Exception ex)
        {
            return new ModelValidationEntry
            {
                Status = "error",
                Path = glbPath,
                RelativePath = MakeRelative(root, glbPath),
                Name = Path.GetFileNameWithoutExtension(glbPath),
                Notes = [$"glTF parse failed: {ex.Message}"],
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

    private static SourceIndexSnapshot LoadSourceIndex(string root)
    {
        var dbPath = Path.Combine(root, "ue_source_index.db");
        var snapshot = new SourceIndexSnapshot { Path = dbPath, Available = File.Exists(dbPath) };
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
            snapshot.MaterialTextureSlots = LoadSourceMaterialTextureSlots(connection);
            snapshot.ComponentAssetRelations = LoadSourceComponentAssetRelations(connection);
            snapshot.PackageObjectMaps = LoadSourcePackageObjectMaps(connection);
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

    private static SourceMaterialTextureSlot[] LoadSourceMaterialTextureSlots(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
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
                RelationSource = GetString(reader, 7) ?? "",
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
            FROM component_asset_relations
            ORDER BY owner_object_path, component_object_path, relation_type, target_path;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<SourceComponentAssetRelation>();
        while (reader.Read())
        {
            result.Add(new SourceComponentAssetRelation
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
            });
        }

        return result.ToArray();
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

    private static MaterialTextureSlotLink[] WriteMaterialTextureSlotLinks(
        string root,
        Dictionary<string, MaterialInfo> materialIndex,
        List<TextureLinkInfo> textureLinks,
        SourceIndexSnapshot sourceIndex)
    {
        var links = BuildMaterialTextureSlotLinks(materialIndex, textureLinks, sourceIndex);
        var path = Path.Combine(root, "material_texture_slots.jsonl");
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        foreach (var link in links.OrderBy(x => x.MaterialName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.SlotName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.TextureObjectPath, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteLine(JsonConvert.SerializeObject(new
            {
                kind = "MaterialTextureSlot",
                link.MaterialName,
                link.MaterialPath,
                link.MaterialObjectPath,
                link.SlotName,
                link.TextureName,
                link.TextureObjectPath,
                link.TexturePath,
                link.ExportedTexture,
                link.SharedTexture,
                link.Sha256,
                link.HardLinked,
                link.MatchStatus,
                link.MatchReason,
                link.RelationSource,
            }));
        }

        return links;
    }

    private static MaterialTextureSlotLink[] BuildMaterialTextureSlotLinks(
        Dictionary<string, MaterialInfo> materialIndex,
        List<TextureLinkInfo> textureLinks,
        SourceIndexSnapshot sourceIndex)
    {
        if (!sourceIndex.Available || sourceIndex.MaterialTextureSlots.Length == 0)
            return [];

        var result = new List<MaterialTextureSlotLink>();
        foreach (var slot in sourceIndex.MaterialTextureSlots)
        {
            var material = FindMaterialInfo(materialIndex, slot.MaterialName);
            var textureLink = FindTextureLink(textureLinks, slot);
            result.Add(new MaterialTextureSlotLink
            {
                MaterialName = slot.MaterialName ?? "",
                MaterialPath = material?.RelativePath,
                MaterialObjectPath = slot.MaterialObjectPath,
                SlotName = slot.SlotName ?? "",
                TextureName = slot.TextureName,
                TextureObjectPath = slot.TextureObjectPath,
                TexturePath = slot.TexturePath,
                ExportedTexture = textureLink?.RelativePath,
                SharedTexture = textureLink?.SharedRelativePath,
                Sha256 = textureLink?.Hash,
                HardLinked = textureLink?.HardLinked,
                MatchStatus = textureLink == null ? "missingExportedTexture" : "matched",
                MatchReason = textureLink == null
                    ? "源索引记录了材质贴图槽，但当前导出目录中没有找到对应 PNG/HDR。"
                    : "通过 UE texture object path / texture name 匹配到已导出贴图，并关联共享贴图。",
                RelationSource = slot.RelationSource,
            });
        }

        return result.ToArray();
    }

    private static ComponentAssetRelationLink[] WriteComponentAssetRelations(
        string root,
        List<JObject> catalogRows,
        SourceIndexSnapshot sourceIndex)
    {
        var links = BuildComponentAssetRelationLinks(root, catalogRows, sourceIndex);
        var path = Path.Combine(root, "component_asset_relations.jsonl");
        using (var writer = new StreamWriter(path, false, Encoding.UTF8))
        {
            foreach (var link in links.OrderBy(x => x.OwnerObjectPath, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.ComponentObjectPath, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.RelationType, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(x => x.TargetPath, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteLine(JsonConvert.SerializeObject(new
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
                }));
            }
        }

        WriteComponentGroups(root, links);
        return links;
    }

    private static SourcePackageObjectMap[] WritePackageObjectMaps(string root, SourceIndexSnapshot sourceIndex)
    {
        var rows = sourceIndex.Available ? sourceIndex.PackageObjectMaps : [];
        var path = Path.Combine(root, "package_object_maps.jsonl");
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
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

        var byObjectPath = exportedAssets
            .Where(x => !string.IsNullOrWhiteSpace((string?)x["objectPath"]))
            .GroupBy(x => (string)x["objectPath"]!, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() == 1)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var result = new List<ComponentAssetRelationLink>(sourceIndex.ComponentAssetRelations.Length);
        foreach (var relation in sourceIndex.ComponentAssetRelations)
        {
            var matched = FindExportedAssetForTarget(root, relation, exportedAssets, byObjectPath);
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
                MatchStatus = matched == null ? "missingExportedAsset" : "matched",
                MatchReason = matched == null
                    ? "源索引记录了 UE 组件/蓝图资源关系，但当前导出目录没有找到对应资产。"
                    : "通过 UE object path 或包路径后缀匹配到已导出素材。",
                SocketName = relation.SocketName,
                ParentComponentPath = relation.ParentComponentPath,
                Transform = BuildTransformObject(relation),
            });
        }

        return result.ToArray();
    }

    private static JObject? FindExportedAssetForTarget(
        string root,
        SourceComponentAssetRelation relation,
        JObject[] exportedAssets,
        Dictionary<string, JObject> byObjectPath)
    {
        if (!string.IsNullOrWhiteSpace(relation.TargetPath) &&
            byObjectPath.TryGetValue(relation.TargetPath, out var byPath))
            return byPath;

        if (string.IsNullOrWhiteSpace(relation.TargetPath))
            return null;

        var packageSuffix = BuildPackageSuffix(relation.TargetPath);
        if (string.IsNullOrWhiteSpace(packageSuffix))
            return null;

        var matches = exportedAssets
            .Where(x => AssetRelativeWithoutExtension(root, (string?)x["output"] ?? (string?)x["source"])
                .EndsWith(packageSuffix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
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

    private static void WriteComponentGroups(string root, ComponentAssetRelationLink[] links)
    {
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
                    missingModelReferenceCount = group.Count(x => IsModelRelation(x.RelationType) && x.MatchStatus != "matched"),
                    animationReferenceCount = group.Count(x => IsAnimationRelation(x.RelationType)),
                    exportedAnimationReferenceCount = group.Count(x => IsAnimationRelation(x.RelationType) && x.MatchStatus == "matched"),
                    missingAnimationReferenceCount = group.Count(x => IsAnimationRelation(x.RelationType) && x.MatchStatus != "matched"),
                    materialReferenceCount = group.Count(x => string.Equals(x.RelationType, "Material", StringComparison.OrdinalIgnoreCase)),
                    exportedMaterialReferenceCount = group.Count(x => string.Equals(x.RelationType, "Material", StringComparison.OrdinalIgnoreCase) && x.MatchStatus == "matched"),
                    missingMaterialReferenceCount = group.Count(x => string.Equals(x.RelationType, "Material", StringComparison.OrdinalIgnoreCase) && x.MatchStatus != "matched"),
                    missingReferenceCount = group.Count(x => IsTrackedAssetRelation(x.RelationType) && x.MatchStatus != "matched"),
                    relationSources = group.Select(x => x.RelationSource)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
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
                        .Where(x => IsTrackedAssetRelation(x.RelationType) && x.MatchStatus != "matched")
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
        => relationType.Equals("Animation", StringComparison.OrdinalIgnoreCase)
           || relationType.Equals("AnimClass", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrackedAssetRelation(string relationType)
        => IsModelRelation(relationType)
           || IsAnimationRelation(relationType)
           || relationType.Equals("Material", StringComparison.OrdinalIgnoreCase)
           || relationType.Equals("Texture", StringComparison.OrdinalIgnoreCase)
           || relationType.Equals("Skeleton", StringComparison.OrdinalIgnoreCase)
           || relationType.Equals("AnimBlueprintGeneratedClass", StringComparison.OrdinalIgnoreCase)
           || relationType.Equals("BlueprintClass", StringComparison.OrdinalIgnoreCase);

    private static SharedGltfTextureLink[] RewriteGltfSharedTextureUris(
        string root,
        List<ModelValidationEntry> reports,
        MaterialTextureSlotLink[] materialTextureSlots)
    {
        var rows = new List<SharedGltfTextureLink>();
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
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        foreach (var row in rows.OrderBy(x => x.Model, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.MaterialName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Semantic, StringComparer.OrdinalIgnoreCase))
            writer.WriteLine(JsonConvert.SerializeObject(row));

        return rows.ToArray();
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

    private static TextureLinkInfo? FindTextureLink(List<TextureLinkInfo> textureLinks, SourceMaterialTextureSlot slot)
    {
        var objectPath = slot.TextureObjectPath ?? slot.TexturePath;
        if (!string.IsNullOrWhiteSpace(objectPath))
        {
            var packageSuffix = BuildPackageSuffix(objectPath);
            if (!string.IsNullOrWhiteSpace(packageSuffix))
            {
                var exactSuffixMatches = textureLinks
                    .Where(x => TextureRelativeWithoutExtension(x.RelativePath).EndsWith(packageSuffix, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (exactSuffixMatches.Length == 1)
                    return exactSuffixMatches[0];
            }
        }

        if (string.IsNullOrWhiteSpace(slot.TextureName))
            return null;

        var nameMatches = textureLinks
            .Where(x => string.Equals(Path.GetFileNameWithoutExtension(x.RelativePath), slot.TextureName, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return nameMatches.Length == 1 ? nameMatches[0] : null;
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
            return packagePath.TrimStart('/');

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
        SourceIndexSnapshot sourceIndex)
    {
        var validations = BuildAnimationValidations(catalogRows, sourceIndex);
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
            ["rule"] = "只验证 UE Skeleton 原始引用一致的模型动画候选；再检查动画 track 骨骼是否被模型骨架覆盖，以及重叠骨骼父子层级是否兼容。",
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
            }),
            ["validations"] = JArray.FromObject(validations.Select(x => new
            {
                status = x.Status,
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
                matchedTrackBones = x.MatchedTrackBones,
                missingTrackBoneCount = x.MissingTrackBones.Length,
                missingTrackBones = x.MissingTrackBones.Take(64).ToArray(),
                trackCoverage = x.TrackCoverage,
                hierarchyCompatible = x.HierarchyCompatible,
                hierarchyMismatchCount = x.HierarchyMismatches.Length,
                hierarchyMismatches = x.HierarchyMismatches.Take(32).ToArray(),
            })),
        };

        File.WriteAllText(Path.Combine(root, "animation_validation.json"), json.ToString(Formatting.Indented), Encoding.UTF8);
        return summary;
    }

    private static AnimationValidationEntry[] BuildAnimationValidations(
        List<JObject> catalogRows,
        SourceIndexSnapshot sourceIndex)
    {
        var models = catalogRows
            .Where(x => string.Equals((string?)x["kind"], "Model", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace((string?)x["skeletonPath"]))
            .ToArray();
        var animations = catalogRows
            .Where(x => string.Equals((string?)x["kind"], "Animation", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace((string?)x["skeletonPath"]))
            .GroupBy(x => (string)x["skeletonPath"]!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

        var result = new List<AnimationValidationEntry>();
        foreach (var model in models)
        {
            var skeletonPath = (string)model["skeletonPath"]!;
            if (!animations.TryGetValue(skeletonPath, out var matchedAnimations))
                continue;

            foreach (var animation in matchedAnimations)
                result.Add(ValidateAnimationPair(model, animation, sourceIndex));
        }

        return result
            .OrderBy(x => x.ModelOutput, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.AnimationOutput, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static AnimationValidationEntry ValidateAnimationPair(
        JObject model,
        JObject animation,
        SourceIndexSnapshot sourceIndex)
    {
        var skeletonPath = (string?)model["skeletonPath"];
        var modelBones = FindModelBones(model, skeletonPath, sourceIndex);
        var animationTracks = FindAnimationTracks(animation, sourceIndex);
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
        var hierarchyMismatches = CompareHierarchy(modelBones, animationTracks, sourceIndex);
        var status = "ok";
        var reason = "Skeleton 路径一致，动画 track 骨骼被模型骨架覆盖，重叠骨骼层级兼容。";

        if (!sourceIndex.Available)
        {
            status = "warning";
            reason = "缺少 ue_source_index.db，无法验证骨骼覆盖和动画 track。";
        }
        else if (modelBones.Bones.Length == 0)
        {
            status = "warning";
            reason = "源索引中没有找到模型骨骼，暂时只能依赖 UE Skeleton 路径。";
        }
        else if (animationTracks.Length == 0)
        {
            status = "warning";
            reason = "源索引中没有找到动画 track，暂时只能依赖 UE Skeleton 路径。";
        }
        else if (missingTrackBones.Length > 0)
        {
            status = "error";
            reason = "动画 track 引用了模型骨架中不存在的骨骼。";
        }
        else if (hierarchyMismatches.Length > 0)
        {
            status = "warning";
            reason = "动画和模型的部分重叠骨骼父级不一致，需要人工复核。";
        }

        return new AnimationValidationEntry
        {
            PairKey = BuildPairKey(model, animation),
            Status = status,
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
            MatchedTrackBones = matchedTrackBones,
            MissingTrackBones = missingTrackBones,
            TrackCoverage = trackCoverage,
            HierarchyCompatible = hierarchyMismatches.Length == 0,
            HierarchyMismatches = hierarchyMismatches,
        };
    }

    private static ModelBoneLookup FindModelBones(JObject model, string? skeletonPath, SourceIndexSnapshot sourceIndex)
    {
        var objectPath = (string?)model["objectPath"];
        if (!string.IsNullOrWhiteSpace(objectPath) && sourceIndex.BonesByOwner.TryGetValue(objectPath, out var byOwner))
            return new ModelBoneLookup(byOwner);

        if (!string.IsNullOrWhiteSpace(skeletonPath) && sourceIndex.BonesBySkeleton.TryGetValue(skeletonPath, out var bySkeleton))
        {
            var singleOwner = PickSingleBoneOwner(bySkeleton, "SkeletalMesh") ?? PickSingleBoneOwner(bySkeleton, null);
            return new ModelBoneLookup(singleOwner ?? []);
        }

        return new ModelBoneLookup([]);
    }

    private static SourceAnimationTrack[] FindAnimationTracks(JObject animation, SourceIndexSnapshot sourceIndex)
    {
        var objectPath = (string?)animation["objectPath"];
        if (!string.IsNullOrWhiteSpace(objectPath) && sourceIndex.TracksByAnimation.TryGetValue(objectPath, out var byObjectPath))
            return byObjectPath;

        return [];
    }

    private static string[] CompareHierarchy(
        ModelBoneLookup modelBones,
        SourceAnimationTrack[] animationTracks,
        SourceIndexSnapshot sourceIndex)
    {
        if (modelBones.Bones.Length == 0 || animationTracks.Length == 0)
            return [];

        var skeletonPath = animationTracks.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.SkeletonPath))?.SkeletonPath;
        if (string.IsNullOrWhiteSpace(skeletonPath) || !sourceIndex.BonesBySkeleton.TryGetValue(skeletonPath, out var skeletonBones))
            return [];

        var skeletonLookup = new ModelBoneLookup(PickSingleBoneOwner(skeletonBones, "Skeleton") ?? PickSingleBoneOwner(skeletonBones, null) ?? []);
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
        AnimationValidationSummary animationValidation)
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
            var relationAnimations = (matchedAnimations ?? [])
                .OrderBy(x => (string?)x["output"], StringComparer.OrdinalIgnoreCase)
                .Select(x =>
                {
                    animationValidation.ByPairKey.TryGetValue(BuildPairKey(model, x), out var validation);
                    return new
                    {
                        name = x["name"],
                        source = x["source"],
                        output = x["output"],
                        status = x["status"],
                        duration = x["duration"],
                        frameCount = x["frameCount"],
                        trackCount = x["trackCount"],
                        validationStatus = validation?.Status,
                        validationReason = validation?.Reason,
                        trackCoverage = validation?.TrackCoverage,
                        hierarchyCompatible = validation?.HierarchyCompatible,
                        missingTrackBones = validation?.MissingTrackBones.Take(32).ToArray(),
                    };
                })
                .ToArray();

            relations.Add(JObject.FromObject(new
            {
                model = model["output"],
                modelName = model["name"],
                modelSource = model["source"],
                skeletonPath,
                skeletonName = model["skeletonName"],
                confidence = relationAnimations.Length > 0 ? "ExplicitSkeleton" : "NoMatchingAnimationExported",
                animations = relationAnimations,
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
        MaterialTextureSlotLink[] materialTextureSlots,
        SharedGltfTextureLink[] sharedGltfTextureLinks,
        ComponentAssetRelationLink[] componentAssetRelations,
        SourcePackageObjectMap[] packageObjectMaps,
        JArray skeletonGroups,
        JObject modelAnimationRelations,
        AnimationValidationSummary animationValidation)
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
            CREATE TABLE material_texture_slots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                material_name TEXT NOT NULL,
                material_path TEXT,
                material_object_path TEXT,
                slot_name TEXT NOT NULL,
                texture_name TEXT,
                texture_object_path TEXT,
                texture_path TEXT,
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
                duration REAL,
                frame_count INTEGER,
                track_count INTEGER,
                FOREIGN KEY (relation_id) REFERENCES model_animation_relations(id)
            );
            """);
        Execute(connection, transaction, """
            CREATE TABLE animation_validation (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                model TEXT NOT NULL,
                animation TEXT NOT NULL,
                skeleton_path TEXT,
                status TEXT NOT NULL,
                reason TEXT,
                model_bone_count INTEGER NOT NULL,
                animation_track_count INTEGER NOT NULL,
                matched_track_bones INTEGER NOT NULL,
                track_coverage REAL NOT NULL,
                hierarchy_compatible INTEGER NOT NULL,
                missing_track_bones_json TEXT NOT NULL,
                hierarchy_mismatches_json TEXT NOT NULL,
                raw_json TEXT NOT NULL
            );
            """);
        Execute(connection, transaction, "CREATE INDEX idx_assets_kind ON assets(kind, resource_kind);");
        Execute(connection, transaction, "CREATE INDEX idx_assets_skeleton ON assets(skeleton_path);");
        Execute(connection, transaction, "CREATE INDEX idx_texture_hash ON texture_links(sha256);");
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
        Execute(connection, transaction, "CREATE INDEX idx_skeleton_groups_path ON skeleton_groups(skeleton_path);");
        Execute(connection, transaction, "CREATE INDEX idx_skeleton_groups_counts ON skeleton_groups(model_count, animation_count);");
        Execute(connection, transaction, "CREATE INDEX idx_relations_skeleton ON model_animation_relations(skeleton_path);");
        Execute(connection, transaction, "CREATE INDEX idx_animation_validation_pair ON animation_validation(model, animation);");
        Execute(connection, transaction, "CREATE INDEX idx_animation_validation_status ON animation_validation(status);");

        foreach (var row in catalogRows)
            InsertAsset(connection, transaction, row);

        foreach (var link in textureLinks)
            InsertTextureLink(connection, transaction, link);

        foreach (var report in reports)
            InsertModelValidation(connection, transaction, report);

        foreach (var slot in materialTextureSlots)
            InsertMaterialTextureSlot(connection, transaction, slot);

        foreach (var link in sharedGltfTextureLinks)
            InsertSharedGltfTextureLink(connection, transaction, link);

        foreach (var link in componentAssetRelations)
            InsertComponentAssetRelation(connection, transaction, link);

        InsertComponentGroups(connection, transaction, componentAssetRelations);

        foreach (var row in packageObjectMaps)
            InsertPackageObjectMap(connection, transaction, row);

        InsertSkeletonGroups(connection, transaction, skeletonGroups);
        InsertModelAnimationRelations(connection, transaction, modelAnimationRelations);
        InsertAnimationValidation(connection, transaction, animationValidation);

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
                texture_name, texture_object_path, texture_path,
                exported_texture, shared_texture, sha256, hard_linked,
                match_status, match_reason, relation_source
            )
            VALUES (
                $materialName, $materialPath, $materialObjectPath, $slotName,
                $textureName, $textureObjectPath, $texturePath,
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
        Add(command, "$exportedTexture", slot.ExportedTexture);
        Add(command, "$sharedTexture", slot.SharedTexture);
        Add(command, "$sha256", slot.Sha256);
        Add(command, "$hardLinked", slot.HardLinked == null ? null : slot.HardLinked.Value ? 1 : 0);
        Add(command, "$matchStatus", slot.MatchStatus);
        Add(command, "$matchReason", slot.MatchReason);
        Add(command, "$relationSource", slot.RelationSource);
        command.ExecuteNonQuery();
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

    private static void InsertComponentGroups(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ComponentAssetRelationLink[] links)
    {
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
                validation.Reason,
                model = validation.ModelOutput,
                animation = validation.AnimationOutput,
                validation.SkeletonPath,
                validation.ModelBoneCount,
                validation.AnimationTrackCount,
                validation.MatchedTrackBones,
                validation.TrackCoverage,
                validation.HierarchyCompatible,
                validation.MissingTrackBones,
                validation.HierarchyMismatches,
            });

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO animation_validation (
                    model, animation, skeleton_path, status, reason,
                    model_bone_count, animation_track_count, matched_track_bones,
                    track_coverage, hierarchy_compatible,
                    missing_track_bones_json, hierarchy_mismatches_json, raw_json
                )
                VALUES (
                    $model, $animation, $skeletonPath, $status, $reason,
                    $modelBoneCount, $animationTrackCount, $matchedTrackBones,
                    $trackCoverage, $hierarchyCompatible,
                    $missingTrackBonesJson, $hierarchyMismatchesJson, $rawJson
                );
                """;
            Add(command, "$model", validation.ModelOutput);
            Add(command, "$animation", validation.AnimationOutput);
            Add(command, "$skeletonPath", validation.SkeletonPath);
            Add(command, "$status", validation.Status);
            Add(command, "$reason", validation.Reason);
            Add(command, "$modelBoneCount", validation.ModelBoneCount);
            Add(command, "$animationTrackCount", validation.AnimationTrackCount);
            Add(command, "$matchedTrackBones", validation.MatchedTrackBones);
            Add(command, "$trackCoverage", validation.TrackCoverage);
            Add(command, "$hierarchyCompatible", validation.HierarchyCompatible ? 1 : 0);
            Add(command, "$missingTrackBonesJson", JsonConvert.SerializeObject(validation.MissingTrackBones));
            Add(command, "$hierarchyMismatchesJson", JsonConvert.SerializeObject(validation.HierarchyMismatches));
            Add(command, "$rawJson", rawJson.ToString(Formatting.None));
            command.ExecuteNonQuery();
        }
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
        SourceIndexSnapshot sourceIndex)
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

        File.WriteAllText(
            Path.Combine(root, "skeletons.json"),
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

        return skeletonArray;
    }

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
        sb.AppendLine($"- UE 包 Import/Export 记录: `{packageObjectMaps.Length}`");
        sb.AppendLine();
        sb.AppendLine("## 索引文件");
        sb.AppendLine();
        sb.AppendLine("| 文件 | 用途 |");
        sb.AppendLine("| --- | --- |");
        sb.AppendLine("| `asset_catalog.jsonl` | 模型、材质、贴图、动画主索引，一行一个资产。 |");
        sb.AppendLine("| `library_index.db` | 已导出素材库的 SQLite 索引，便于筛选模型、动画、贴图和关系。 |");
        sb.AppendLine("| `ue_source_index.db` | 启用源索引时生成，记录完整源文件表、已检查对象、Import/Export、Skeleton/Material/Texture/Blueprint/Component 关系、骨骼层级、动画 track 和 Montage/Composite segment。 |");
        sb.AppendLine("| `export_manifest.jsonl` | 实际导出文件与 UE 源包/对象的对应关系。 |");
        sb.AppendLine("| `animation_bindings.jsonl` | 动画源对象、Skeleton、帧数、track 和导出状态。 |");
        sb.AppendLine("| `model_animations.json` | 只按 UE Skeleton 原始引用生成的模型动画匹配，并回填动画验证结果。 |");
        sb.AppendLine("| `animation_validation.json` | 基于源索引检查模型动画候选的 track 覆盖率和骨骼层级兼容性。 |");
        sb.AppendLine("| `model_validation.json` | GLB/glTF 静态结构、材质、贴图、skin 验证报告。 |");
        sb.AppendLine("| `skeletons.json` | 按 GLB/glTF skin joints 生成的骨架分组，并合并 UE Skeleton、源骨架对象和同 Skeleton 动画。 |");
        sb.AppendLine("| `texture_links.jsonl` | 原贴图文件、共享贴图、sha256 和硬链接状态。 |");
        sb.AppendLine("| `material_texture_slots.jsonl` | 材质 slot 到 UE 贴图、导出贴图和共享贴图的对应关系。 |");
        sb.AppendLine("| `shared_texture_gltf_links.jsonl` | 文本 glTF image URI 改写到共享贴图的记录。 |");
        sb.AppendLine("| `component_asset_relations.jsonl` | 蓝图、组件、默认对象到模型/材质/动画/Skeleton 的显式 UE 关系。 |");
        sb.AppendLine("| `component_groups.json` | 按 owner 蓝图/组件聚合的组合模型与任务素材关系摘要。 |");
        sb.AppendLine("| `package_object_maps.jsonl` | UE 包 ImportMap/ExportMap 原始依赖和导出对象记录。 |");
        sb.AppendLine("| `Textures/_Shared` | 启用硬链接去重后生成的共享贴图库。 |");
        sb.AppendLine();
        sb.AppendLine("## 下一步");
        sb.AppendLine();
        sb.AppendLine("- 增加动画采样预览验证，检查播放姿态、bbox 变化和异常骨骼变换。");
        sb.AppendLine("- 扩展 Montage/Composite segment 报告，保留 slot、section 和 segment 时间范围。");
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
                rule = "重复 PNG/HDR 统一复制到 Textures/_Shared，再把重复文件替换为硬链接；文本 glTF 会按 UE 材质槽尽量引用共享贴图。",
                scanned = textureFiles.Length,
                unique = byHash.Count,
                copiedToShared = copied,
                hardLinkedFiles = linked,
                note = "所有原 PNG/HDR 文件都会尽量替换为指向 Textures/_Shared 的硬链接；GLB 保持独立预览，文本 glTF 可通过 shared_texture_gltf_links.jsonl 追踪共享贴图改写。",
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

    private sealed class SourceIndexSnapshot
    {
        public string Path { get; set; } = string.Empty;
        public bool Available { get; set; }
        public string? Error { get; set; }
        public Dictionary<string, SourceBone[]> BonesByOwner { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SourceBone[]> BonesBySkeleton { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SourceAnimationTrack[]> TracksByAnimation { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public SourceMaterialTextureSlot[] MaterialTextureSlots { get; set; } = [];
        public SourceComponentAssetRelation[] ComponentAssetRelations { get; set; } = [];
        public SourcePackageObjectMap[] PackageObjectMaps { get; set; } = [];
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

    private sealed class MaterialTextureSlotLink
    {
        public string MaterialName { get; set; } = string.Empty;
        public string? MaterialPath { get; set; }
        public string? MaterialObjectPath { get; set; }
        public string SlotName { get; set; } = string.Empty;
        public string? TextureName { get; set; }
        public string? TextureObjectPath { get; set; }
        public string? TexturePath { get; set; }
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

    private sealed class AnimationValidationEntry
    {
        public string PairKey { get; set; } = string.Empty;
        public string Status { get; set; } = "unknown";
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
        public int MatchedTrackBones { get; set; }
        public string[] MissingTrackBones { get; set; } = [];
        public double TrackCoverage { get; set; }
        public bool HierarchyCompatible { get; set; }
        public string[] HierarchyMismatches { get; set; } = [];
    }
}
