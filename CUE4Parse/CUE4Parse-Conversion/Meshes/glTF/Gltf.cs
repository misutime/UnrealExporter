using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using CUE4Parse_Conversion.Materials;
using CUE4Parse_Conversion.Meshes.PSK;
using CUE4Parse_Conversion.Textures;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Meshes;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Writers;
using CUE4Parse.Utils;
using Newtonsoft.Json;
using SkiaSharp;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.IO;
using SharpGLTF.Materials;
using SharpGLTF.Memory;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace CUE4Parse_Conversion.Meshes.glTF
{
    using VERTEX = VertexPositionNormalTangent;
    public class Gltf
    {
        public readonly ModelRoot Model;

        public Gltf(string name, CStaticMeshLod lod, List<MaterialExporter2>? materialExports, ExporterOptions options)
        {
            var mesh = new MeshBuilder<VERTEX, VertexColorXTextureX, VertexEmpty>(name);

            for (var i = 0; i < lod.Sections.Value.Length; i++)
            {
                ExportStaticMeshSections(i, lod, lod.Sections.Value[i], materialExports, mesh, options);
            }

            var sceneBuilder = new SceneBuilder();
            sceneBuilder.AddRigidMesh(mesh, Matrix4x4.Identity);
            Model = sceneBuilder.ToGltf2();
        }

        public Gltf(string name, CSkelMeshLod lod, List<CSkelMeshBone> bones, List<MaterialExporter2>? materialExports, ExporterOptions options, FPackageIndex[]? morphTargets = null, int lodIndex = -1)
        {
            var mesh = new MeshBuilder<VERTEX, VertexColorXTextureX, VertexJoints4>(name);

            for (var i = 0; i < lod.Sections.Value.Length; i++)
            {
                ExportSkelMeshSections(i, lod, lod.Sections.Value[i], materialExports, mesh, options);
            }

            if (morphTargets != null)
            {
                var targetNames = "{\"targetNames\": [";
                for (var i = 0; i < morphTargets.Length; i++)
                {
                    var morphTarget = morphTargets[i].Load<UMorphTarget>();
                    if (morphTarget == null || morphTarget.MorphLODModels == null || morphTarget.MorphLODModels.Length < lodIndex || lodIndex == -1)
                        continue;
                    var morphBuilder = mesh.UseMorphTarget(i);
                    var morphModel = morphTarget.MorphLODModels[lodIndex];

                    targetNames += $"\"{morphTarget.Name}\"";
                    targetNames += i != morphTargets.Length-1 ? "," : "";

                    var verts = morphBuilder.Vertices.ToArray();
                    for (int j = 0; j < morphModel.Vertices.Length; j++) // morphModel.NumBaseMeshVerts can be different from verts.Length
                    {
                        var delta = morphModel.Vertices[j];
                        var vert = lod.Verts[delta.SourceIdx];
                        var srcVert = new VertexPositionNormalTangent(SwapYZ(vert.Position*0.01f),SwapYZAndNormalize((FVector)vert.Normal) , SwapYZAndNormalize((Vector4)vert.Tangent));
                        var index = FindVert(srcVert, verts);
                        if (index == -1)  continue;

                        morphBuilder.SetVertexDelta(morphBuilder.Vertices.ElementAt(index), new VertexGeometryDelta(SwapYZ(delta.PositionDelta*0.01f), Vector3.Zero, SwapYZAndNormalize(delta.TangentZDelta)));
                    }
                }

                targetNames += "]}";
                mesh.Extras = (JsonContent) targetNames;
            }

            var sceneBuilder = new SceneBuilder();
            var armatureNodeBuilder = new NodeBuilder(name+".ao");

            var armature = CreateGltfSkeleton(bones, armatureNodeBuilder);
            sceneBuilder.AddSkinnedMesh(mesh, Matrix4x4.Identity, armature);

            Model = sceneBuilder.ToGltf2();
        }

        private static int FindVert(VertexPositionNormalTangent a, VertexPositionNormalTangent[] b)
        {
            for (int i = 0; i < b.Length; i++)
            {
                if (b[i].GetPosition() == a.GetPosition()) // not a good idea but i don't see any other way
                    return i;
            }
            return -1;
        }

        public ArraySegment<byte> SaveAsWavefront()
        {
            throw new NotImplementedException();
        }

        public void Save(EMeshFormat meshFormat, FArchiveWriter Ar)
        {
            switch (meshFormat)
            {
                case EMeshFormat.Gltf2:
                    Ar.Write(Model.WriteGLB());
                    break;
                case EMeshFormat.OBJ:
                    Ar.Write(SaveAsWavefront()); // this can be supported after new release of SharpGltf
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(meshFormat), meshFormat, null);
            }
        }

        public static NodeBuilder[] CreateGltfSkeleton(List<CSkelMeshBone> skeleton, NodeBuilder armatureNode) // TODO optimize
        {
            var result = new List<NodeBuilder>();

            for (var i = 0; i < skeleton.Count; i++)
            {
                var root = skeleton[i];
                if (root.ParentIndex != -1) continue;

                var rootCopy = (CSkelMeshBone)root.Clone(); // we don't want to modify the original skeleton
                // rootCopy.Orientation = FQuat.Conjugate(root.Orientation);
                result.AddRange(CreateBonesRecursive(rootCopy, armatureNode, skeleton, i));
            }

            return result.ToArray();
        }

        private static List<NodeBuilder> CreateBonesRecursive(CSkelMeshBone bone, NodeBuilder parent, List<CSkelMeshBone> skeleton, int index)
        {
            var res = new List<NodeBuilder>();

            var bonePos = SwapYZ(bone.Position*0.01f);
            var boneRot = SwapYZ(bone.Orientation);
            var node = parent.CreateNode(bone.Name.ToString())
                .WithLocalRotation(boneRot.ToQuaternion())
                .WithLocalTranslation(bonePos);

            res.Add(node);

            var numBones = skeleton.Count;
            for (int j = 0; j < numBones; j++)
            {
                if (index == j) continue;
                var bone2 = skeleton[j];
                if (bone2.ParentIndex == index)
                {
                    res.AddRange(CreateBonesRecursive(bone2, node, skeleton, j));
                }
            }
            return res;
        }

        public static void ExportSkelMeshSections(int index, CSkelMeshLod lod, CMeshSection sect, List<MaterialExporter2>? materialExports, MeshBuilder<VERTEX, VertexColorXTextureX, VertexJoints4> mesh, ExporterOptions options)
        {
            var mat = BuildMaterial(index, sect, materialExports, options);

            var prim = mesh.UsePrimitive(mat);
            for (int j = 0; j < sect.NumFaces; j++)
            {
                var wedgeIndex = new uint[3];
                for (var k = 0; k < wedgeIndex.Length; k++)
                {
                    wedgeIndex[k] = lod.Indices.Value[sect.FirstIndex + j * 3 + k];
                }

                var vert1 = lod.Verts[wedgeIndex[0]];
                var vert2 = lod.Verts[wedgeIndex[1]];
                var vert3 = lod.Verts[wedgeIndex[2]];

                var (v1, v2, v3) = PrepareTris(vert1, vert2, vert3);
                var (c1, c2, c3) = PrepareUVsAndTexCoords(lod, vert1, vert2, vert3, wedgeIndex);
                var (jv1, jv2, jv3) = PrepareVertexJoints(vert1, vert2, vert3);

                prim.AddTriangle((v1, c1, jv1), (v2, c2, jv2), (v3, c3, jv3));
            }
        }

        public static void ExportStaticMeshSections(int index, CStaticMeshLod lod, CMeshSection sect, List<MaterialExporter2>? materialExports, MeshBuilder<VERTEX, VertexColorXTextureX, VertexEmpty> mesh, ExporterOptions options)
        {
            var mat = BuildMaterial(index, sect, materialExports, options);

            var prim = mesh.UsePrimitive(mat);
            for (int j = 0; j < sect.NumFaces; j++)
            {
                var wedgeIndex = new uint[3];
                for (var k = 0; k < wedgeIndex.Length; k++)
                {
                    wedgeIndex[k] = lod.Indices.Value[sect.FirstIndex + j * 3 + k];
                }

                var vert1 = lod.Verts[wedgeIndex[0]];
                var vert2 = lod.Verts[wedgeIndex[1]];
                var vert3 = lod.Verts[wedgeIndex[2]];

                var (v1, v2, v3) = PrepareTris(vert1, vert2, vert3);
                var (c1, c2, c3) = PrepareUVsAndTexCoords(lod, vert1, vert2, vert3, wedgeIndex);

                prim.AddTriangle((v1, c1), (v2, c2), (v3, c3));
            }
        }

        private static MaterialBuilder BuildMaterial(int index, CMeshSection sect, List<MaterialExporter2>? materialExports, ExporterOptions options)
        {
            var materialName = sect.MaterialName ?? $"material_{index}";
            var mat = new MaterialBuilder()
                .WithMetallicRoughnessShader()
                .WithBaseColor(Vector4.One)
                .WithDoubleSide(true);
            mat.Name = materialName;

            if (sect.Material?.Load<UMaterialInterface>() is not { } unrealMaterial)
            {
                mat.Extras = JsonContent.Parse(JsonConvert.SerializeObject(new
                {
                    unrealMaterial = materialName,
                    textureSlots = new Dictionary<string, string>()
                }), default);
                return mat;
            }

            mat.Name = unrealMaterial.Name;
            materialExports?.Add(new MaterialExporter2(unrealMaterial, options));

            var parameters = new CMaterialParams2();
            unrealMaterial.GetParams(parameters, options.MaterialFormat);

            if (parameters.BlendMode == EBlendMode.BLEND_Masked)
                mat.WithAlpha(SharpGLTF.Materials.AlphaMode.MASK, 0.333f);
            else if (parameters.IsTranslucent)
                mat.WithAlpha(SharpGLTF.Materials.AlphaMode.BLEND, 0.01f);

            var baseColorFactor = TryGetBaseColorFactor(parameters, out var colorFactor)
                ? colorFactor
                : Vector4.One;

            if (TryGetTextureImage(parameters, options, out var diffuse,
                    "BaseColor",
                    "Base Color",
                    "DiffuseTex",
                    "Diffuse",
                    "Albedo",
                    CMaterialParams2.FallbackDiffuse))
                mat.WithBaseColor(diffuse, baseColorFactor);
            else if (parameters.BlendMode == EBlendMode.BLEND_Masked &&
                     TryGetAlphaMaskImage(parameters, options, out var alphaMask))
                mat.WithBaseColor(alphaMask, baseColorFactor);
            else if (baseColorFactor != Vector4.One)
                mat.WithBaseColor(baseColorFactor);

            if (TryGetTextureImage(parameters, options, out var normal,
                    "Normal",
                    "NormalTex",
                    "Unique_Normal",
                    "DetailNormal",
                    CMaterialParams2.FallbackNormals))
                mat.WithNormal(normal, 1.0f);

            if (TryGetTextureImage(parameters, options, out var masks, CMaterialParams2.FallbackSpecularMasks))
            {
                mat.WithMetallicRoughness(masks, null, null);
                mat.WithOcclusion(masks, 1.0f);
            }

            if (HasUsefulEmissiveTexture(parameters) &&
                TryGetTextureImage(parameters, options, out var emissive, CMaterialParams2.FallbackEmissive))
                mat.WithEmissive(emissive, Vector3.One);

            var textureSlots = parameters.Textures.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.GetPathName());

            mat.Extras = JsonContent.Parse(JsonConvert.SerializeObject(new
            {
                unrealMaterial = unrealMaterial.GetPathName(),
                blendMode = parameters.BlendMode.ToString(),
                shadingModel = parameters.ShadingModel.ToString(),
                textureSlots
            }), default);

            return mat;
        }

        private static bool HasUsefulEmissiveTexture(CMaterialParams2 parameters)
        {
            if (!parameters.TryGetTexture2d(out var texture, CMaterialParams2.FallbackEmissive))
                return false;

            var path = texture.GetPathName();
            return !IsDefaultColorTexture(path);
        }

        private static bool TryGetBaseColorFactor(CMaterialParams2 parameters, out Vector4 color)
        {
            color = Vector4.One;
            if (!parameters.TryGetLinearColor(out var linearColor,
                    "BaseColor",
                    "Base Color",
                    "Color",
                    "DiffuseColor",
                    "BaseColor Tint"))
                return false;

            var srgb = linearColor.ToSRGB();
            color = new Vector4(srgb.R, srgb.G, srgb.B, srgb.A);
            return true;
        }

        private static bool TryGetAlphaMaskImage(CMaterialParams2 parameters, ExporterOptions options, out MemoryImage image)
        {
            image = default;

            try
            {
                if (!parameters.TryGetTexture2d(out var texture,
                        "Opacity Mask",
                        "OpacityMask",
                        "AlphaMask",
                        "Alpha",
                        "Mask") ||
                    texture is not UTexture2D texture2d)
                    return false;

                var decodedTexture = texture2d.Decode(options.Platform);
                if (decodedTexture is null)
                    return false;

                using var source = decodedTexture.ToSkBitmap();
                using var rgba = new SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                for (var y = 0; y < source.Height; y++)
                {
                    for (var x = 0; x < source.Width; x++)
                    {
                        var pixel = source.GetPixel(x, y);
                        var alpha = (byte) Math.Max(pixel.Alpha, Math.Max(pixel.Red, Math.Max(pixel.Green, pixel.Blue)));
                        rgba.SetPixel(x, y, new SKColor(255, 255, 255, alpha));
                    }
                }

                using var data = SKImage.FromBitmap(rgba).Encode(SKEncodedImageFormat.Png, 100);
                var bytes = data.ToArray();
                if (bytes.Length == 0)
                    return false;

                image = new MemoryImage(bytes);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetTextureImage(CMaterialParams2 parameters, ExporterOptions options, out MemoryImage image, params string[] textureSlots)
        {
            image = default;

            try
            {
                foreach (var textureSlot in textureSlots)
                {
                    if (!parameters.TryGetTexture2d(out var texture, textureSlot) || texture is not UTexture2D texture2d)
                        continue;

                    if (IsDefaultNormalTexture(texture.GetPathName()) &&
                        textureSlot.Contains("Normal", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var decodedTexture = texture2d.Decode(options.Platform);
                    if (decodedTexture is null)
                        continue;

                    var imageData = decodedTexture.Encode(ETextureFormat.Png, options.ExportHdrTexturesAsHdr, out var ext);
                    if (!ext.Equals("png", StringComparison.OrdinalIgnoreCase) || imageData.Length == 0)
                        continue;

                    image = new MemoryImage(imageData);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool IsDefaultColorTexture(string path)
        {
            return path.Contains("/T_White.", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith("/T_White", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("/T_sRGB_White.", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith("/T_sRGB_White", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("/T_Linear_White.", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith("/T_Linear_White", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("/T_sRGB_Black.", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith("/T_sRGB_Black", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDefaultNormalTexture(string path)
        {
            return path.Contains("/DefaultNormal", StringComparison.OrdinalIgnoreCase) ||
                   path.Contains("/FlatNormal", StringComparison.OrdinalIgnoreCase);
        }

        public static VertexJoints4 PrepareVertexJoint(CSkelMeshVertex vert)
        {
            var bindings = new List<(int, float)>();

            foreach (var influence in vert.Influences)
            {
                bindings.Add((influence.Bone, influence.Weight));
            }

            return new VertexJoints4(bindings.ToArray());
        }

        public static (VertexJoints4, VertexJoints4, VertexJoints4) PrepareVertexJoints(CSkelMeshVertex vert1, CSkelMeshVertex vert2, CSkelMeshVertex vert3)
        {
            var jv1 = PrepareVertexJoint(vert1);
            var jv2 = PrepareVertexJoint(vert2);
            var jv3 = PrepareVertexJoint(vert3);

            return (jv1, jv2, jv3);
        }

        public static (VertexColorXTextureX, VertexColorXTextureX, VertexColorXTextureX) PrepareUVsAndTexCoords(
            CBaseMeshLod lod, CMeshVertex vert1, CMeshVertex vert2, CMeshVertex vert3, uint[] indices)
        {
            return PrepareUVsAndTexCoords(GetViewerSafeVertexColors(lod), vert1, vert2, vert3,
                lod.ExtraUV.Value, indices);
        }

        public static (VertexColorXTextureX, VertexColorXTextureX, VertexColorXTextureX) PrepareUVsAndTexCoords(
            FColor[] colors, CMeshVertex vert1, CMeshVertex vert2, CMeshVertex vert3, FMeshUVFloat[][] uvs, uint[] indices)
        {
            var (uvs1, uvs2, uvs3) = PrepareUVs(vert1, vert2, vert3, uvs, indices);
            var c1 = new VertexColorXTextureX((Vector4)NormalizeVertexColor(colors[indices[0]]), uvs1);
            var c2 = new VertexColorXTextureX((Vector4)NormalizeVertexColor(colors[indices[1]]), uvs2);
            var c3 = new VertexColorXTextureX((Vector4)NormalizeVertexColor(colors[indices[2]]), uvs3);
            return (c1, c2, c3);
        }

        private static FColor[] GetViewerSafeVertexColors(CBaseMeshLod lod)
        {
            if (lod.VertexColors is { Length: > 0 } colors && colors.Any(c => c.R != 0 || c.G != 0 || c.B != 0 || c.A != 0))
                return colors;

            return Enumerable.Repeat(new FColor(255, 255, 255, 255), lod.NumVerts).ToArray();
        }

        private static FColor NormalizeVertexColor(FColor color)
        {
            if (color.R == 0 && color.G == 0 && color.B == 0 && color.A == 0)
                return new FColor(255, 255, 255, 255);

            return color.A == 0 ? new FColor(color.R, color.G, color.B, 255) : color;
        }

        private static (List<Vector2>, List<Vector2>, List<Vector2>) PrepareUVs(CMeshVertex vert1, CMeshVertex vert2, CMeshVertex vert3, FMeshUVFloat[][] uvs, uint[] indices)
        {
            var uvs1 = new List<Vector2>() { (Vector2)vert1.UV };
            var uvs2 = new List<Vector2>() { (Vector2)vert2.UV };
            var uvs3 = new List<Vector2>() { (Vector2)vert3.UV };
            foreach (var uv in uvs)
            {
                uvs1.Add((Vector2)uv[indices[0]]);
                uvs2.Add((Vector2)uv[indices[1]]);
                uvs3.Add((Vector2)uv[indices[2]]);
            }

            return (uvs1, uvs2, uvs3);
        }

        private static (VERTEX, VERTEX, VERTEX) PrepareTris(CMeshVertex vert1, CMeshVertex vert2, CMeshVertex vert3)
        {
            var v1 = new VertexPositionNormalTangent(SwapYZ(vert1.Position*0.01f),SwapYZAndNormalize((FVector)vert1.Normal) , SwapYZAndNormalize((Vector4)vert1.Tangent));
            var v2 = new VertexPositionNormalTangent(SwapYZ(vert2.Position*0.01f), SwapYZAndNormalize((FVector)vert2.Normal), SwapYZAndNormalize((Vector4)vert2.Tangent));
            var v3 = new VertexPositionNormalTangent(SwapYZ(vert3.Position*0.01f), SwapYZAndNormalize((FVector)vert3.Normal), SwapYZAndNormalize((Vector4)vert3.Tangent));

            return (v1, v2, v3);
        }

        public static FVector SwapYZAndNormalize(FVector vec)
        {
            var res = SwapYZ(vec);
            res.Normalize();
            return res;
        }

        public static FVector SwapYZ(FVector vec)
        {
            var res = new FVector(vec.X, vec.Z, vec.Y);
            return res;
        }

        // Swapping Y/Z is a handedness-changing basis reflection; quaternion W must flip too.
        public static FQuat SwapYZ(FQuat quat) => new (quat.X, quat.Z, quat.Y, -quat.W);

        public static Vector4 SwapYZAndNormalize(Vector4 vec)
        {
          return Vector4.Normalize(new Vector4(vec.X, vec.Z, vec.Y, vec.W));
        }
    }
}
