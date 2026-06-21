using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SharpGLTF.Schema2;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;
using GltfImage = SharpGLTF.Schema2.Image;
using GltfTextureWrapMode = SharpGLTF.Schema2.TextureWrapMode;
using Matrix4 = OpenTK.Mathematics.Matrix4;
using OpenTkNativeWindow = OpenTK.Windowing.Desktop.NativeWindow;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace UE5LibraryBrowser
{
    internal sealed class PersistentGltfThumbnailRenderer : IDisposable
    {
        private const int Size = 256;
        private readonly OpenTkNativeWindow _window;
        private readonly int _program;
        private readonly int _uniformViewProjection;
        private readonly int _vao;
        private readonly int _vbo;
        private readonly int _fbo;
        private readonly int _colorTexture;
        private readonly int _depthBuffer;
        private bool _disposed;

        public PersistentGltfThumbnailRenderer()
        {
            var settings = new NativeWindowSettings
            {
                ClientSize = new Vector2i(Size, Size),
                StartVisible = false,
                APIVersion = new Version(3, 3),
                Profile = ContextProfile.Core,
                Title = "UE5LibraryBrowser Thumbnail Renderer"
            };
            _window = new OpenTkNativeWindow(settings);
            _window.MakeCurrent();

            _program = CreateProgram();
            _uniformViewProjection = GL.GetUniformLocation(_program, "uViewProjection");
            _vao = GL.GenVertexArray();
            _vbo = GL.GenBuffer();
            CreateFramebuffer(out _fbo, out _colorTexture, out _depthBuffer);

            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
            GL.CullFace(TriangleFace.Back);
        }

        public void RenderToFile(string gltfPath, string outputPath)
        {
            ThrowIfDisposed();
            _window.MakeCurrent();

            var mesh = GltfThumbnailMesh.Load(gltfPath);
            if (mesh.Vertices.Length == 0)
            {
                throw new InvalidDataException("glTF 没有可渲染三角面。");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            var temp = outputPath + ".tmp.png";
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
            GL.Viewport(0, 0, Size, Size);
            GL.ClearColor(0.184f, 0.204f, 0.220f, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            GL.UseProgram(_program);
            var viewProjection = BuildCamera(mesh.Min, mesh.Max);
            GL.UniformMatrix4(_uniformViewProjection, false, ref viewProjection);

            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, mesh.Vertices.Length * ThumbnailVertex.Stride, mesh.Vertices, BufferUsageHint.StreamDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, ThumbnailVertex.Stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, ThumbnailVertex.Stride, 12);
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, ThumbnailVertex.Stride, 24);
            GL.DrawArrays(OpenTK.Graphics.OpenGL4.PrimitiveType.Triangles, 0, mesh.Vertices.Length);

            using var bitmap = ReadFramebuffer();
            bitmap.Save(temp, ImageFormat.Png);
            File.Move(temp, outputPath, true);
        }

        private static Matrix4 BuildCamera(Vector3 min, Vector3 max)
        {
            var center = (min + max) * 0.5f;
            var extent = Vector3.Max(max - min, new Vector3(0.01f));
            var radius = MathF.Max(extent.Length() * 0.5f, 0.05f);
            var direction = System.Numerics.Vector3.Normalize(new Vector3(1.35f, 0.95f, 1.45f));
            var eye = center + direction * radius * 3.2f;

            var up = new OpenTK.Mathematics.Vector3(0, 1, 0);
            var view = Matrix4.LookAt(ToOpenTk(eye), ToOpenTk(center), up);
            var orthoSize = MathF.Max(MathF.Max(extent.X, extent.Y), extent.Z) * 1.35f;
            orthoSize = MathF.Max(orthoSize, 0.5f);
            var projection = Matrix4.CreateOrthographic(orthoSize, orthoSize, 0.01f, MathF.Max(radius * 8.0f, 10.0f));
            return view * projection;
        }

        private static OpenTK.Mathematics.Vector3 ToOpenTk(Vector3 value)
        {
            return new OpenTK.Mathematics.Vector3(value.X, value.Y, value.Z);
        }

        private static void CreateFramebuffer(out int framebuffer, out int colorTexture, out int depthBuffer)
        {
            framebuffer = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);

            colorTexture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, colorTexture);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, Size, Size, 0, OpenTK.Graphics.OpenGL4.PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colorTexture, 0);

            depthBuffer = GL.GenRenderbuffer();
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depthBuffer);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, Size, Size);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depthBuffer);

            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                throw new InvalidOperationException($"OpenGL framebuffer 创建失败: {status}");
            }
        }

        private static int CreateProgram()
        {
            var vertex = CompileShader(ShaderType.VertexShader, @"
#version 330 core
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec4 aColor;
uniform mat4 uViewProjection;
out vec3 vNormal;
out vec4 vColor;
void main()
{
    gl_Position = uViewProjection * vec4(aPosition, 1.0);
    vNormal = normalize(aNormal);
    vColor = aColor;
}");

            var fragment = CompileShader(ShaderType.FragmentShader, @"
#version 330 core
in vec3 vNormal;
in vec4 vColor;
out vec4 FragColor;
void main()
{
    vec3 lightA = normalize(vec3(0.45, 0.75, 0.55));
    vec3 lightB = normalize(vec3(-0.65, 0.30, 0.70));
    float diffuse = max(dot(normalize(vNormal), lightA), 0.0) * 0.72
                  + max(dot(normalize(vNormal), lightB), 0.0) * 0.24;
    vec3 color = vColor.rgb * (0.34 + diffuse);
    FragColor = vec4(color, max(vColor.a, 0.2));
}");

            var program = GL.CreateProgram();
            GL.AttachShader(program, vertex);
            GL.AttachShader(program, fragment);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out var ok);
            GL.DeleteShader(vertex);
            GL.DeleteShader(fragment);
            if (ok == 0)
            {
                throw new InvalidOperationException(GL.GetProgramInfoLog(program));
            }

            return program;
        }

        private static int CompileShader(ShaderType type, string source)
        {
            var shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out var ok);
            if (ok == 0)
            {
                throw new InvalidOperationException(GL.GetShaderInfoLog(shader));
            }

            return shader;
        }

        private static Bitmap ReadFramebuffer()
        {
            var bytes = new byte[Size * Size * 4];
            GL.ReadPixels(0, 0, Size, Size, OpenTK.Graphics.OpenGL4.PixelFormat.Bgra, PixelType.UnsignedByte, bytes);

            var bitmap = new Bitmap(Size, Size, DrawingPixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, Size, Size), ImageLockMode.WriteOnly, DrawingPixelFormat.Format32bppArgb);
            try
            {
                for (var y = 0; y < Size; y++)
                {
                    var sourceOffset = (Size - 1 - y) * Size * 4;
                    var target = data.Scan0 + y * data.Stride;
                    System.Runtime.InteropServices.Marshal.Copy(bytes, sourceOffset, target, Size * 4);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return bitmap;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(PersistentGltfThumbnailRenderer));
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _window.MakeCurrent();
            GL.DeleteBuffer(_vbo);
            GL.DeleteVertexArray(_vao);
            GL.DeleteProgram(_program);
            GL.DeleteTexture(_colorTexture);
            GL.DeleteRenderbuffer(_depthBuffer);
            GL.DeleteFramebuffer(_fbo);
            _window.Dispose();
        }

        private readonly struct ThumbnailVertex
        {
            public const int Stride = 40;

            public ThumbnailVertex(Vector3 position, Vector3 normal, Vector4 color)
            {
                X = position.X;
                Y = position.Y;
                Z = position.Z;
                Nx = normal.X;
                Ny = normal.Y;
                Nz = normal.Z;
                R = color.X;
                G = color.Y;
                B = color.Z;
                A = color.W;
            }

            public readonly float X;
            public readonly float Y;
            public readonly float Z;
            public readonly float Nx;
            public readonly float Ny;
            public readonly float Nz;
            public readonly float R;
            public readonly float G;
            public readonly float B;
            public readonly float A;
        }

        private sealed class GltfThumbnailMesh
        {
            public ThumbnailVertex[] Vertices { get; private init; } = Array.Empty<ThumbnailVertex>();
            public Vector3 Min { get; private init; }
            public Vector3 Max { get; private init; }

            public static GltfThumbnailMesh Load(string path)
            {
                var model = ModelRoot.Load(path, new ReadSettings
                {
                    // 导出器历史版本可能写出 f3d 可容忍的空数组；缩略图浏览只读取几何，不在这里做严格 glTF 验证。
                    Validation = SharpGLTF.Validation.ValidationMode.Skip,
                    JsonPreprocessor = RemoveEmptyArrayProperties
                });
                var scene = model.DefaultScene ?? model.LogicalScenes.FirstOrDefault();
                if (scene == null)
                {
                    return new GltfThumbnailMesh();
                }

                var vertices = new List<ThumbnailVertex>();
                var min = new Vector3(float.PositiveInfinity);
                var max = new Vector3(float.NegativeInfinity);
                var materialCache = new Dictionary<Material, MaterialPreview>();

                try
                {
                    foreach (var node in scene.VisualChildren)
                    {
                        AddNode(node, vertices, ref min, ref max, materialCache);
                    }
                }
                finally
                {
                    foreach (var preview in materialCache.Values)
                    {
                        preview.Texture?.Dispose();
                    }
                }

                if (vertices.Count == 0)
                {
                    min = Vector3.Zero;
                    max = Vector3.One;
                }

                return new GltfThumbnailMesh
                {
                    Vertices = vertices.ToArray(),
                    Min = min,
                    Max = max
                };
            }

            private static string RemoveEmptyArrayProperties(string json)
            {
                if (!string.IsNullOrEmpty(json) && json[0] == '\uFEFF')
                    json = json[1..];

                var root = JsonNode.Parse(json);
                if (root == null)
                {
                    return json;
                }

                RemoveEmptyArrayProperties(root);
                return root.ToJsonString();
            }

            private static void RemoveEmptyArrayProperties(JsonNode node)
            {
                if (node is JsonObject obj)
                {
                    var removeKeys = obj
                        .Where(x => x.Value is JsonArray array && array.Count == 0)
                        .Select(x => x.Key)
                        .ToArray();
                    foreach (var key in removeKeys)
                    {
                        obj.Remove(key);
                    }

                    foreach (var child in obj.Select(x => x.Value).Where(x => x != null).ToArray())
                    {
                        RemoveEmptyArrayProperties(child!);
                    }
                }
                else if (node is JsonArray array)
                {
                    foreach (var child in array.Where(x => x != null).ToArray())
                    {
                        RemoveEmptyArrayProperties(child!);
                    }
                }
            }

            private static void AddNode(Node node, List<ThumbnailVertex> vertices, ref Vector3 min, ref Vector3 max, Dictionary<Material, MaterialPreview> materialCache)
            {
                if (node.Mesh != null)
                {
                    AddMesh(node.Mesh, node.WorldMatrix, vertices, ref min, ref max, materialCache);
                }

                foreach (var child in node.VisualChildren)
                {
                    AddNode(child, vertices, ref min, ref max, materialCache);
                }
            }

            private static void AddMesh(Mesh mesh, System.Numerics.Matrix4x4 world, List<ThumbnailVertex> vertices, ref Vector3 min, ref Vector3 max, Dictionary<Material, MaterialPreview> materialCache)
            {
                foreach (var primitive in mesh.Primitives)
                {
                    if (primitive.DrawPrimitiveType != SharpGLTF.Schema2.PrimitiveType.TRIANGLES)
                    {
                        continue;
                    }

                    var positionAccessor = primitive.GetVertexAccessor("POSITION");
                    if (positionAccessor == null)
                    {
                        continue;
                    }

                    var positions = positionAccessor.AsVector3Array();
                    var normals = primitive.GetVertexAccessor("NORMAL")?.AsVector3Array();
                    var material = GetMaterialPreview(primitive.Material, materialCache);
                    var texCoordName = $"TEXCOORD_{material.TextureCoordinate}";
                    var texCoords = material.Texture != null
                        ? primitive.GetVertexAccessor(texCoordName)?.AsVector2Array()
                        : null;

                    foreach (var (a, b, c) in primitive.GetTriangleIndices())
                    {
                        var p0 = Vector3.Transform(positions[a], world);
                        var p1 = Vector3.Transform(positions[b], world);
                        var p2 = Vector3.Transform(positions[c], world);
                        var n0 = ReadNormal(normals, a, p0, p1, p2, world);
                        var n1 = ReadNormal(normals, b, p0, p1, p2, world);
                        var n2 = ReadNormal(normals, c, p0, p1, p2, world);
                        vertices.Add(new ThumbnailVertex(p0, n0, ReadColor(material, texCoords, a)));
                        vertices.Add(new ThumbnailVertex(p1, n1, ReadColor(material, texCoords, b)));
                        vertices.Add(new ThumbnailVertex(p2, n2, ReadColor(material, texCoords, c)));
                        min = Vector3.Min(min, Vector3.Min(p0, Vector3.Min(p1, p2)));
                        max = Vector3.Max(max, Vector3.Max(p0, Vector3.Max(p1, p2)));
                    }
                }
            }

            private static Vector3 ReadNormal(IReadOnlyList<Vector3>? normals, int index, Vector3 p0, Vector3 p1, Vector3 p2, System.Numerics.Matrix4x4 world)
            {
                var normal = normals != null && index < normals.Count
                    ? Vector3.TransformNormal(normals[index], world)
                    : Vector3.Cross(p1 - p0, p2 - p0);

                return normal.LengthSquared() > 0.000001f ? Vector3.Normalize(normal) : Vector3.UnitY;
            }

            private static MaterialPreview GetMaterialPreview(Material material, Dictionary<Material, MaterialPreview> cache)
            {
                if (material == null)
                {
                    return MaterialPreview.Default;
                }

                if (cache.TryGetValue(material, out var cached))
                {
                    return cached;
                }

                var baseColor = GetBaseColor(material);
                var texture = TryLoadBaseColorTexture(material, out var textureCoordinate, out var textureTransform);
                var preview = new MaterialPreview(baseColor, texture, textureCoordinate, textureTransform);
                cache[material] = preview;
                return preview;
            }

            private static Vector4 GetBaseColor(Material material)
            {
                if (material == null)
                {
                    return new Vector4(0.72f, 0.72f, 0.72f, 1.0f);
                }

                var channel = material.FindChannel("BaseColor");
                if (channel.HasValue)
                {
                    var color = channel.Value.Color;
                    return new Vector4(
                        ClampColor(color.X),
                        ClampColor(color.Y),
                        ClampColor(color.Z),
                        MathF.Max(ClampColor(color.W), 0.35f));
                }

                return new Vector4(0.72f, 0.72f, 0.72f, 1.0f);
            }

            private static ThumbnailTexture? TryLoadBaseColorTexture(Material material, out int textureCoordinate, out System.Numerics.Matrix3x2 textureTransform)
            {
                textureCoordinate = 0;
                textureTransform = System.Numerics.Matrix3x2.Identity;

                var channel = material?.FindChannel("BaseColor");
                if (!channel.HasValue || channel.Value.Texture?.PrimaryImage == null)
                {
                    return null;
                }

                textureCoordinate = Math.Max(0, channel.Value.TextureCoordinate);
                if (channel.Value.TextureTransform != null)
                {
                    textureCoordinate = channel.Value.TextureTransform.TextureCoordinateOverride ?? textureCoordinate;
                    textureTransform = channel.Value.TextureTransform.Matrix;
                }

                try
                {
                    return ThumbnailTexture.Load(channel.Value.Texture.PrimaryImage, channel.Value.TextureSampler);
                }
                catch
                {
                    // 缩略图不能因为单张贴图解码失败阻塞模型浏览；失败时退回材质色。
                    return null;
                }
            }

            private static Vector4 ReadColor(MaterialPreview material, IReadOnlyList<System.Numerics.Vector2>? texCoords, int index)
            {
                var color = material.BaseColor;
                if (material.Texture == null || texCoords == null || index < 0 || index >= texCoords.Count)
                {
                    return color;
                }

                var uv = System.Numerics.Vector2.Transform(texCoords[index], material.TextureTransform);
                var sampled = material.Texture.Sample(uv);
                return new Vector4(
                    ClampColor(color.X * sampled.X),
                    ClampColor(color.Y * sampled.Y),
                    ClampColor(color.Z * sampled.Z),
                    MathF.Max(ClampColor(color.W * sampled.W), 0.35f));
            }

            private static float ClampColor(float value)
            {
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    return 0.72f;
                }

                return Math.Clamp(value, 0.02f, 1.0f);
            }

            private sealed record MaterialPreview(
                Vector4 BaseColor,
                ThumbnailTexture? Texture,
                int TextureCoordinate,
                System.Numerics.Matrix3x2 TextureTransform)
            {
                public static readonly MaterialPreview Default = new(
                    new Vector4(0.72f, 0.72f, 0.72f, 1.0f),
                    null,
                    0,
                    System.Numerics.Matrix3x2.Identity);
            }

            private sealed class ThumbnailTexture : IDisposable
            {
                private readonly byte[] _bgra;
                private readonly GltfTextureWrapMode _wrapS;
                private readonly GltfTextureWrapMode _wrapT;
                private bool _disposed;

                private ThumbnailTexture(byte[] bgra, int width, int height, GltfTextureWrapMode wrapS, GltfTextureWrapMode wrapT)
                {
                    _bgra = bgra;
                    Width = width;
                    Height = height;
                    _wrapS = wrapS;
                    _wrapT = wrapT;
                }

                public int Width { get; }
                public int Height { get; }

                public static ThumbnailTexture Load(GltfImage image, TextureSampler sampler)
                {
                    using var stream = image.Content.Open();
                    using var loaded = new Bitmap(stream);
                    using var bitmap = new Bitmap(loaded.Width, loaded.Height, DrawingPixelFormat.Format32bppArgb);
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.DrawImage(loaded, 0, 0, loaded.Width, loaded.Height);
                    }

                    var data = bitmap.LockBits(
                        new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                        ImageLockMode.ReadOnly,
                        DrawingPixelFormat.Format32bppArgb);
                    try
                    {
                        var bytes = new byte[Math.Abs(data.Stride) * bitmap.Height];
                        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                        return new ThumbnailTexture(
                            bytes,
                            bitmap.Width,
                            bitmap.Height,
                            sampler?.WrapS ?? GltfTextureWrapMode.REPEAT,
                            sampler?.WrapT ?? GltfTextureWrapMode.REPEAT);
                    }
                    finally
                    {
                        bitmap.UnlockBits(data);
                    }
                }

                public Vector4 Sample(System.Numerics.Vector2 uv)
                {
                    if (_disposed || Width <= 0 || Height <= 0 || _bgra.Length == 0)
                    {
                        return Vector4.One;
                    }

                    var u = Wrap(uv.X, _wrapS);
                    var v = Wrap(uv.Y, _wrapT);
                    var x = Math.Clamp((int)MathF.Round(u * (Width - 1)), 0, Width - 1);
                    var y = Math.Clamp((int)MathF.Round(v * (Height - 1)), 0, Height - 1);
                    var offset = (y * Width + x) * 4;
                    if (offset < 0 || offset + 3 >= _bgra.Length)
                    {
                        return Vector4.One;
                    }

                    return new Vector4(
                        _bgra[offset + 2] / 255.0f,
                        _bgra[offset + 1] / 255.0f,
                        _bgra[offset] / 255.0f,
                        _bgra[offset + 3] / 255.0f);
                }

                private static float Wrap(float value, GltfTextureWrapMode mode)
                {
                    if (float.IsNaN(value) || float.IsInfinity(value))
                    {
                        return 0;
                    }

                    return mode switch
                    {
                        GltfTextureWrapMode.CLAMP_TO_EDGE => Math.Clamp(value, 0, 1),
                        GltfTextureWrapMode.MIRRORED_REPEAT => MirrorRepeat(value),
                        _ => Repeat(value)
                    };
                }

                private static float Repeat(float value)
                {
                    value -= MathF.Floor(value);
                    return value < 0 ? value + 1 : value;
                }

                private static float MirrorRepeat(float value)
                {
                    var repeated = Repeat(value);
                    var tile = (int)MathF.Floor(value);
                    return Math.Abs(tile) % 2 == 0 ? repeated : 1.0f - repeated;
                }

                public void Dispose()
                {
                    _disposed = true;
                }
            }
        }
    }
}
