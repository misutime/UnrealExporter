using System.Collections.Concurrent;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace UE5LibraryBrowser;

internal sealed class ViewerSafeGltfCache
{
    private static readonly ConcurrentDictionary<string, bool> TextureAlphaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _cacheRoot;

    public ViewerSafeGltfCache(string libraryRoot)
    {
        _cacheRoot = Path.Combine(libraryRoot, ".asset_browser_cache", "viewer_safe_models");
        Directory.CreateDirectory(_cacheRoot);
    }

    public string GetViewerSafeModelPath(string modelPath)
    {
        if (!File.Exists(modelPath) || !modelPath.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            return modelPath;

        var info = new FileInfo(modelPath);
        var cachePath = Path.Combine(_cacheRoot, $"{Hash("viewer-safe-v4|" + modelPath)}_{info.LastWriteTimeUtc.Ticks}.glb");
        if (File.Exists(cachePath))
            return cachePath;

        try
        {
            var data = File.ReadAllBytes(modelPath);
            if (!TryReadGlb(data, out var json, out var binChunk))
                return modelPath;

            var changed = RewriteVertexColorsToWhite(json, binChunk);
            changed |= ApplyAlphaModes(json, modelPath);
            if (!changed)
                return modelPath;

            RewriteImageUris(json, modelPath, cachePath);
            WriteGlb(cachePath, json, binChunk);
            return cachePath;
        }
        catch
        {
            return modelPath;
        }
    }

    private static bool RewriteVertexColorsToWhite(JsonNode json, byte[] binChunk)
    {
        var changed = false;
        if (json["meshes"] is not JsonArray meshes)
            return false;

        foreach (var mesh in meshes.OfType<JsonObject>())
        {
            if (mesh["primitives"] is not JsonArray primitives)
                continue;

            foreach (var primitive in primitives.OfType<JsonObject>())
            {
                if (primitive["attributes"] is not JsonObject attributes)
                    continue;
                if (attributes["COLOR_0"]?.GetValue<int>() is not { } accessorIndex)
                    continue;
                if (WriteWhiteColorAccessor(json, binChunk, accessorIndex))
                    changed = true;
            }
        }

        return changed;
    }

    private static bool ApplyAlphaModes(JsonNode json, string originalModelPath)
    {
        if (json["materials"] is not JsonArray materials)
            return false;

        var changed = false;
        foreach (var material in materials.OfType<JsonObject>())
        {
            var alphaMode = material["alphaMode"]?.GetValue<string>();
            if (MaterialBaseColorTextureHasMeaningfulAlpha(json, material, originalModelPath) &&
                !string.Equals(alphaMode, "BLEND", StringComparison.OrdinalIgnoreCase))
            {
                material["alphaMode"] = "BLEND";
                material.Remove("alphaCutoff");
                changed = true;
            }

            if (material["pbrMetallicRoughness"] is JsonObject pbr &&
                pbr["baseColorFactor"] is JsonArray baseColorFactor &&
                baseColorFactor.Count >= 4 &&
                baseColorFactor[3]?.GetValue<double>() < 1.0 &&
                material["alphaMode"] is null)
            {
                material["alphaMode"] = "BLEND";
                changed = true;
            }
        }

        return changed;
    }

    private static bool MaterialBaseColorTextureHasMeaningfulAlpha(JsonNode json, JsonObject material, string originalModelPath)
    {
        if (!TryGetMaterialBaseColorImagePath(json, material, originalModelPath, out var imagePath))
            return false;

        return TextureAlphaCache.GetOrAdd(imagePath, TextureHasMeaningfulAlpha);
    }

    private static bool TryGetMaterialBaseColorImagePath(JsonNode json, JsonObject material, string originalModelPath, out string imagePath)
    {
        imagePath = string.Empty;
        if (material["pbrMetallicRoughness"] is not JsonObject pbr ||
            pbr["baseColorTexture"] is not JsonObject baseColorTexture ||
            baseColorTexture["index"]?.GetValue<int>() is not { } textureIndex)
            return false;
        if (json["textures"] is not JsonArray textures ||
            textureIndex < 0 ||
            textureIndex >= textures.Count ||
            textures[textureIndex] is not JsonObject texture)
            return false;
        if (json["images"] is not JsonArray images ||
            texture["source"]?.GetValue<int>() is not { } sourceIndex ||
            sourceIndex < 0 ||
            sourceIndex >= images.Count ||
            images[sourceIndex] is not JsonObject image)
            return false;

        var uri = image["uri"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(uri) || IsNonFileUri(uri))
            return false;

        var originalDirectory = Path.GetDirectoryName(originalModelPath);
        if (string.IsNullOrWhiteSpace(originalDirectory))
            return false;

        var nativeUri = Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar);
        imagePath = Path.GetFullPath(Path.Combine(originalDirectory, nativeUri));
        return File.Exists(imagePath);
    }

    private static bool TextureHasMeaningfulAlpha(string imagePath)
    {
        try
        {
            using var bitmap = new Bitmap(imagePath);
            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).A <= 245)
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

    private static bool WriteWhiteColorAccessor(JsonNode json, byte[] binChunk, int accessorIndex)
    {
        if (json["accessors"] is not JsonArray accessors || accessorIndex < 0 || accessorIndex >= accessors.Count)
            return false;
        if (json["bufferViews"] is not JsonArray bufferViews)
            return false;
        if (accessors[accessorIndex] is not JsonObject accessor)
            return false;

        var bufferViewIndex = accessor["bufferView"]?.GetValue<int>() ?? -1;
        if (bufferViewIndex < 0 || bufferViewIndex >= bufferViews.Count || bufferViews[bufferViewIndex] is not JsonObject bufferView)
            return false;

        var componentType = accessor["componentType"]?.GetValue<int>() ?? 0;
        var componentCount = GetTypeComponentCount(accessor["type"]?.GetValue<string>() ?? "");
        var count = accessor["count"]?.GetValue<int>() ?? 0;
        var componentSize = GetComponentSize(componentType);
        if (componentCount <= 0 || count <= 0 || componentSize <= 0)
            return false;

        var viewOffset = bufferView["byteOffset"]?.GetValue<int>() ?? 0;
        var accessorOffset = accessor["byteOffset"]?.GetValue<int>() ?? 0;
        var stride = bufferView["byteStride"]?.GetValue<int>() ?? componentCount * componentSize;
        var elementSize = componentCount * componentSize;
        var start = viewOffset + accessorOffset;
        if (start < 0 || start + elementSize > binChunk.Length)
            return false;

        var changed = false;
        Span<byte> white = stackalloc byte[elementSize];
        FillWhite(white, componentType, componentCount);

        for (var i = 0; i < count; i++)
        {
            var offset = start + i * stride;
            if (offset < 0 || offset + elementSize > binChunk.Length)
                return changed;

            var target = binChunk.AsSpan(offset, elementSize);
            if (!target.SequenceEqual(white))
            {
                white.CopyTo(target);
                changed = true;
            }
        }

        accessor.Remove("min");
        accessor.Remove("max");
        return changed;
    }

    private static int GetTypeComponentCount(string type)
        => type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            _ => 0
        };

    private static int GetComponentSize(int componentType)
        => componentType switch
        {
            5120 or 5121 => 1,
            5122 or 5123 => 2,
            5125 or 5126 => 4,
            _ => 0
        };

    private static void FillWhite(Span<byte> target, int componentType, int componentCount)
    {
        for (var i = 0; i < componentCount; i++)
        {
            var offset = i * GetComponentSize(componentType);
            switch (componentType)
            {
                case 5120:
                    target[offset] = 127;
                    break;
                case 5121:
                    target[offset] = 255;
                    break;
                case 5122:
                    BitConverter.TryWriteBytes(target[offset..], (short)32767);
                    break;
                case 5123:
                    BitConverter.TryWriteBytes(target[offset..], (ushort)65535);
                    break;
                case 5125:
                    BitConverter.TryWriteBytes(target[offset..], 1u);
                    break;
                case 5126:
                    BitConverter.TryWriteBytes(target[offset..], 1f);
                    break;
            }
        }
    }

    private static void RewriteImageUris(JsonNode json, string originalModelPath, string cachePath)
    {
        if (json["images"] is not JsonArray images)
            return;

        var originalDirectory = Path.GetDirectoryName(originalModelPath);
        var cacheDirectory = Path.GetDirectoryName(cachePath);
        if (string.IsNullOrWhiteSpace(originalDirectory) || string.IsNullOrWhiteSpace(cacheDirectory))
            return;

        foreach (var image in images.OfType<JsonObject>())
        {
            var uri = image["uri"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(uri) || IsNonFileUri(uri))
                continue;

            var nativeUri = uri.Replace('/', Path.DirectorySeparatorChar);
            var absolute = Path.GetFullPath(Path.Combine(originalDirectory, Uri.UnescapeDataString(nativeUri)));
            if (!File.Exists(absolute))
                continue;

            var relative = Path.GetRelativePath(cacheDirectory, absolute)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            image["uri"] = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        }
    }

    private static bool IsNonFileUri(string uri)
        => uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadGlb(byte[] data, out JsonNode json, out byte[] binChunk)
    {
        json = new JsonObject();
        binChunk = [];

        if (data.Length < 20 || Encoding.ASCII.GetString(data, 0, 4) != "glTF")
            return false;

        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var chunkLength = BitConverter.ToInt32(data, offset);
            var chunkType = BitConverter.ToUInt32(data, offset + 4);
            offset += 8;
            if (chunkLength < 0 || offset + chunkLength > data.Length)
                return false;

            var chunk = data.AsSpan(offset, chunkLength).ToArray();
            offset += chunkLength;

            if (chunkType == 0x4E4F534A)
            {
                var text = Encoding.UTF8.GetString(chunk).TrimEnd('\0', ' ', '\r', '\n', '\t');
                json = JsonNode.Parse(text) ?? new JsonObject();
            }
            else if (chunkType == 0x004E4942)
            {
                binChunk = chunk;
            }
        }

        return json is JsonObject && binChunk.Length > 0;
    }

    private static void WriteGlb(string path, JsonNode json, byte[] binChunk)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        Pad(ref jsonBytes, 0x20);
        Pad(ref binChunk, 0x00);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Encoding.ASCII.GetBytes("glTF"));
        writer.Write(2);
        writer.Write(12 + 8 + jsonBytes.Length + 8 + binChunk.Length);
        writer.Write(jsonBytes.Length);
        writer.Write(Encoding.ASCII.GetBytes("JSON"));
        writer.Write(jsonBytes);
        writer.Write(binChunk.Length);
        writer.Write(Encoding.ASCII.GetBytes("BIN\0"));
        writer.Write(binChunk);
        File.WriteAllBytes(path, stream.ToArray());
    }

    private static void Pad(ref byte[] data, byte padding)
    {
        var original = data.Length;
        var padded = (data.Length + 3) & ~3;
        if (padded == data.Length)
            return;
        Array.Resize(ref data, padded);
        for (var i = original; i < data.Length; i++)
            data[i] = padding;
    }

    private static string Hash(string value)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(value.ToLowerInvariant()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
