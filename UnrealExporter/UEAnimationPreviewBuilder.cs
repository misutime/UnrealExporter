using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharpGLTF.IO;
using SharpGLTF.Schema2;

namespace UnrealExporter;

internal static class UEAnimationPreviewBuilder
{
    private const float TranslationAnimationEpsilon = 0.0001f;
    private const float RotationAnimationEpsilonRadians = 0.01f;
    private const float MorphAnimationEpsilon = 0.0001f;

    public static int Run(
        string modelPath,
        string animationPath,
        string outputPath,
        string? reportPath = null,
        string? reportDbPath = null,
        string? skipBoneRegex = null,
        bool formalExport = false)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        reportPath = string.IsNullOrWhiteSpace(reportPath) ? null : Path.GetFullPath(reportPath);
        reportDbPath = string.IsNullOrWhiteSpace(reportDbPath) ? null : Path.GetFullPath(reportDbPath);
        if (reportPath == null && reportDbPath == null)
            reportDbPath = Path.ChangeExtension(fullOutputPath, formalExport ? ".animation_export.db" : ".preview_validation.db");

        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        if (reportPath != null)
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        if (reportDbPath != null)
            Directory.CreateDirectory(Path.GetDirectoryName(reportDbPath)!);

        try
        {
            var animation = UEAnimReader.Read(animationPath);
            var model = LoadModelForPreview(modelPath);
            Regex? skipBonePattern = string.IsNullOrWhiteSpace(skipBoneRegex)
                ? null
                : new Regex(skipBoneRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            var nodesByName = model.LogicalNodes
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

            var gltfAnimation = model.CreateAnimation(animation.Name);
            gltfAnimation.Extras = JsonContent.Parse(JsonConvert.SerializeObject(new
            {
                unrealExporter = new
                {
                    mode = formalExport ? "formalAnimationExport" : "preview",
                    source = "UEAnim",
                    model = Path.GetFullPath(modelPath),
                    animation = Path.GetFullPath(animationPath),
                    animationName = animation.Name,
                    animation.FrameCount,
                    animation.FramesPerSecond,
                }
            }), default);

            var matchedTracks = 0;
            var writtenChannels = 0;
            var animatedTransformChannels = 0;
            var retargetedTranslationTracks = 0;
            var skippedStaticTranslationTracks = 0;
            var skippedNonRootTranslationTracks = 0;
            var retargetedRotationTracks = 0;
            var skippedStaticRotationTracks = 0;
            var skippedScaleTracks = 0;
            var skippedByRegexTracks = 0;
            var missingBones = new List<string>();
            foreach (var track in animation.Tracks)
            {
                if (skipBonePattern?.IsMatch(track.BoneName) == true)
                {
                    skippedByRegexTracks++;
                    continue;
                }

                if (!nodesByName.TryGetValue(track.BoneName, out var node))
                {
                    missingBones.Add(track.BoneName);
                    continue;
                }

                matchedTracks++;
                if (track.Positions.Count > 0)
                {
                    if (formalExport || ShouldWriteTranslationChannel(track.BoneName))
                    {
                        var translation = formalExport
                            ? BuildFormalTranslationKeyMap(track, animation.FramesPerSecond)
                            : BuildTranslationKeyMap(track, node, animation.FramesPerSecond);
                        if (translation.Keys.Count > 0 && (formalExport || IsAnimatedTranslation(translation.Keys)))
                        {
                            gltfAnimation.CreateTranslationChannel(node, translation.Keys, linear: true);
                            writtenChannels++;
                            if (IsAnimatedTranslation(translation.Keys))
                                animatedTransformChannels++;
                        }
                        else if (!formalExport && translation.Keys.Count > 0)
                        {
                            skippedStaticTranslationTracks++;
                        }

                        if (translation.Retargeted)
                            retargetedTranslationTracks++;
                        if (translation.SkippedStatic)
                            skippedStaticTranslationTracks++;
                    }
                    else
                    {
                        skippedNonRootTranslationTracks++;
                    }
                }

                if (track.Rotations.Count > 0)
                {
                    var rotation = formalExport
                        ? BuildFormalRotationKeyMap(track, animation.FramesPerSecond)
                        : BuildRotationKeyMap(track, node, animation.FramesPerSecond);
                    if (rotation.Keys.Count > 0 && (formalExport || IsAnimatedRotation(rotation.Keys)))
                    {
                        gltfAnimation.CreateRotationChannel(
                            node,
                            rotation.Keys,
                            linear: true);
                        writtenChannels++;
                        if (IsAnimatedRotation(rotation.Keys))
                            animatedTransformChannels++;
                    }
                    else if (!formalExport && rotation.Keys.Count > 0)
                    {
                        skippedStaticRotationTracks++;
                    }

                    if (rotation.Retargeted)
                        retargetedRotationTracks++;
                    if (rotation.SkippedStatic)
                        skippedStaticRotationTracks++;
                }

                if (track.Scales.Count > 0)
                {
                    if (formalExport)
                    {
                        var scaleKeys = BuildFormalScaleKeyMap(track, animation.FramesPerSecond);
                        if (scaleKeys.Count > 0)
                        {
                            gltfAnimation.CreateScaleChannel(node, scaleKeys, linear: true);
                            writtenChannels++;
                            if (IsAnimatedScale(scaleKeys))
                                animatedTransformChannels++;
                        }
                    }
                    else
                    {
                        // UE .ueanim tracks are authored against the source Skeleton and often contain
                        // per-bone ref-pose scale keys. Writing them blindly into a standalone glTF
                        // preview can collapse or tear skinned meshes. Keep previews conservative until
                        // we can apply the engine's exact retarget/AnimBP context.
                        skippedScaleTracks++;
                    }
                }
            }

            var morphCurveResult = CreateMorphCurveChannels(gltfAnimation, model, modelPath, animation);
            writtenChannels += morphCurveResult.WrittenChannels;
            var hasActualMotion = formalExport
                ? writtenChannels > 0
                : animatedTransformChannels > 0 || morphCurveResult.WrittenChannels > 0;
            if (hasActualMotion)
            {
                model.SaveGLB(outputPath, new WriteSettings());
                if (!formalExport)
                    UnrealExporter.SanitizeGlbForPreview(outputPath);
            }
            else if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            var anyRetarget = retargetedTranslationTracks > 0 || retargetedRotationTracks > 0;
            var anyAdjustedOrSkipped = anyRetarget
                                       || skippedStaticTranslationTracks > 0
                                       || skippedNonRootTranslationTracks > 0
                                       || skippedStaticRotationTracks > 0
                                       || skippedScaleTracks > 0
                                       || morphCurveResult.UnmappedAnimatedCurves > 0
                                       || morphCurveResult.IncompatibleMorphMeshes > 0;
            var heavyTranslationRetarget = matchedTracks > 0 && retargetedTranslationTracks > matchedTracks * 0.5f;
            var status = hasActualMotion && File.Exists(outputPath)
                ? missingBones.Count == 0 && !anyAdjustedOrSkipped && !heavyTranslationRetarget ? "ok" : "warning"
                : "error";
            var noActualMotionReason = hasActualMotion
                ? null
                : morphCurveResult.AnimatedCurveCount > 0
                    ? "Animation contains animated UE curves, but none mapped to valid morph targets on this model; transform tracks were static/noise."
                    : "Animation transform tracks were static/noise and no animated UE curves were present.";
            WriteReport(reportPath, reportDbPath, new
            {
                status,
                mode = formalExport ? "formalAnimationExport" : "preview",
                gltf = File.Exists(outputPath) ? fullOutputPath : null,
                report = reportPath,
                reportDb = reportDbPath,
                model = Path.GetFullPath(modelPath),
                animation = Path.GetFullPath(animationPath),
                animationName = animation.Name,
                frameCount = animation.FrameCount,
                framesPerSecond = animation.FramesPerSecond,
                duration = animation.FrameCount > 0 && animation.FramesPerSecond > 0
                    ? animation.FrameCount / animation.FramesPerSecond
                    : 0,
                trackCount = animation.Tracks.Count,
                curveCount = animation.Curves.Count,
                matchedTracks,
                missingTrackCount = missingBones.Count,
                writtenChannels,
                animatedTransformChannels,
                writtenMorphChannels = morphCurveResult.WrittenChannels,
                animatedCurveCount = morphCurveResult.AnimatedCurveCount,
                matchedMorphCurves = morphCurveResult.MatchedAnimatedCurves,
                unmappedMorphCurves = morphCurveResult.UnmappedAnimatedCurves,
                incompatibleMorphMeshes = morphCurveResult.IncompatibleMorphMeshes,
                noActualMotionReason,
                retargetedTranslationTracks,
                skippedStaticTranslationTracks,
                skippedNonRootTranslationTracks,
                retargetedRotationTracks,
                skippedStaticRotationTracks,
                skippedScaleTracks,
                skippedByRegexTracks,
                skipBoneRegex,
                anyRetarget,
                anyAdjustedOrSkipped,
                heavyTranslationRetarget,
                visualAcceptance = new
                {
                    status = status == "error" || anyAdjustedOrSkipped || missingBones.Count > 0 ? "notAccepted" : "requiresManualReview",
                    reason = status == "error"
                        ? noActualMotionReason
                        : formalExport
                        ? "Formal export preserved transform channels from the UEAnim clip. Visual/gameplay acceptance still requires importer-side review."
                        : anyAdjustedOrSkipped
                        ? "Preview generation applied or skipped uncertain transform channels. This can prove a diagnostic preview was generated, but cannot prove humanoid animation correctness."
                        : "Preview generated without automatic retargeting, but humanoid animation correctness still requires manual rest/mid/end visual review.",
                    requiresScreenshots = new[] { "restPose", "animationStart", "animationMiddle", "animationEnd" }
                },
                missingBones = missingBones.Take(64).ToArray(),
            });

            if (File.Exists(outputPath))
                Console.WriteLine($"=> {outputPath}");
            Console.WriteLine($"UE animation {(formalExport ? "export" : "preview")}: {status}, matchedTracks={matchedTracks}, transformChannels={animatedTransformChannels}, morphChannels={morphCurveResult.WrittenChannels}, curves={animation.Curves.Count}, matchedMorphCurves={morphCurveResult.MatchedAnimatedCurves}, unmappedMorphCurves={morphCurveResult.UnmappedAnimatedCurves}, missingBones={missingBones.Count}, retargetedTranslations={retargetedTranslationTracks}, skippedStaticTranslations={skippedStaticTranslationTracks}, skippedNonRootTranslations={skippedNonRootTranslationTracks}, retargetedRotations={retargetedRotationTracks}, skippedStaticRotations={skippedStaticRotationTracks}, skippedScales={skippedScaleTracks}, skippedByRegex={skippedByRegexTracks}");
            return status == "error" ? 2 : 0;
        }
        catch (Exception ex)
        {
            WriteReport(reportPath, reportDbPath, new
            {
                status = "error",
                gltf = (string?)null,
                report = reportPath,
                reportDb = reportDbPath,
                model = Path.GetFullPath(modelPath),
                animation = Path.GetFullPath(animationPath),
                error = ex.Message,
            });
            Console.WriteLine($"ERROR: UE animation preview failed ({ex.Message})");
            return 1;
        }
    }

    private static Vector3 SwapYZ(Vector3 value)
        => new(value.X, value.Z, value.Y);

    private static bool ShouldWriteTranslationChannel(string boneName)
    {
        if (string.IsNullOrWhiteSpace(boneName))
            return false;

        return boneName.Equals("root", StringComparison.OrdinalIgnoreCase)
               || boneName.Equals("Root", StringComparison.OrdinalIgnoreCase)
               || boneName.Equals("Armature", StringComparison.OrdinalIgnoreCase)
               || boneName.Equals("Bip001", StringComparison.OrdinalIgnoreCase)
               || boneName.EndsWith("_root", StringComparison.OrdinalIgnoreCase)
               || boneName.EndsWith("-root", StringComparison.OrdinalIgnoreCase)
               || boneName.Contains("ik_", StringComparison.OrdinalIgnoreCase)
               || boneName.Contains("_ik", StringComparison.OrdinalIgnoreCase)
               || boneName.StartsWith("wq_root", StringComparison.OrdinalIgnoreCase);
    }

    private static MorphCurvePreviewResult CreateMorphCurveChannels(Animation gltfAnimation, ModelRoot model, string modelPath, UEAnimData animation)
    {
        var animatedCurves = animation.Curves
            .Where(curve => IsAnimatedScalar(curve.Keys))
            .GroupBy(curve => NormalizeMorphName(curve.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        if (animatedCurves.Count == 0)
            return new MorphCurvePreviewResult(0, 0, 0, 0, 0);

        var targetNamesByMesh = ReadMorphTargetNamesByMeshIndex(modelPath);
        var matchedCurveNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var writtenChannels = 0;
        var incompatibleMeshes = 0;

        foreach (var node in model.LogicalNodes)
        {
            var mesh = node.Mesh;
            if (mesh == null)
                continue;

            var targetCount = GetConsistentMorphTargetCount(mesh);
            if (targetCount <= 0)
            {
                if (mesh.Primitives.Any(x => x.MorphTargetsCount > 0))
                    incompatibleMeshes++;
                continue;
            }

            if (!targetNamesByMesh.TryGetValue(mesh.LogicalIndex, out var targetNames) || targetNames.Length == 0)
                continue;

            var curveBindings = new List<MorphCurveBinding>();
            for (var targetIndex = 0; targetIndex < Math.Min(targetNames.Length, targetCount); targetIndex++)
            {
                var normalized = NormalizeMorphName(targetNames[targetIndex]);
                if (animatedCurves.TryGetValue(normalized, out var curve))
                    curveBindings.Add(new MorphCurveBinding(targetIndex, normalized, curve));
            }

            if (curveBindings.Count == 0)
                continue;

            var keyMap = BuildMorphKeyMap(node, mesh, curveBindings, targetCount, animation.FramesPerSecond);
            if (!IsAnimatedMorphKeyMap(keyMap))
                continue;

            gltfAnimation.CreateMorphChannel(node, keyMap, targetCount, linear: true);
            writtenChannels++;
            foreach (var binding in curveBindings)
                matchedCurveNames.Add(binding.NormalizedName);
        }

        return new MorphCurvePreviewResult(
            animatedCurves.Count,
            matchedCurveNames.Count,
            animatedCurves.Count - matchedCurveNames.Count,
            writtenChannels,
            incompatibleMeshes);
    }

    private static Dictionary<float, float[]> BuildMorphKeyMap(
        Node node,
        Mesh mesh,
        IReadOnlyList<MorphCurveBinding> curveBindings,
        int targetCount,
        float framesPerSecond)
    {
        var times = curveBindings
            .SelectMany(binding => binding.Curve.Keys.Select(key => key.Time(framesPerSecond)))
            .Distinct()
            .OrderBy(x => x)
            .ToArray();
        var baseWeights = ReadBaseMorphWeights(node, mesh, targetCount);
        var result = new Dictionary<float, float[]>();
        foreach (var time in times)
        {
            var weights = (float[])baseWeights.Clone();
            foreach (var binding in curveBindings)
                weights[binding.TargetIndex] = Math.Clamp(SampleCurve(binding.Curve, time, framesPerSecond), 0f, 1f);
            result[time] = weights;
        }

        return result;
    }

    private static float[] ReadBaseMorphWeights(Node node, Mesh mesh, int targetCount)
    {
        var result = new float[targetCount];
        var weights = node.GetMorphWeights();
        if (weights.Count == 0)
            weights = mesh.GetMorphWeights();

        for (var i = 0; i < Math.Min(targetCount, weights.Count); i++)
            result[i] = weights[i];
        return result;
    }

    private static float SampleCurve(UEAnimCurve curve, float time, float framesPerSecond)
    {
        if (curve.Keys.Count == 0)
            return 0;

        var keys = curve.Keys
            .Select(key => (Time: key.Time(framesPerSecond), key.Value))
            .OrderBy(key => key.Time)
            .ToArray();
        if (time <= keys[0].Time)
            return keys[0].Value;
        if (time >= keys[^1].Time)
            return keys[^1].Value;

        for (var i = 1; i < keys.Length; i++)
        {
            if (time > keys[i].Time)
                continue;

            var previous = keys[i - 1];
            var next = keys[i];
            var span = next.Time - previous.Time;
            if (span <= 0)
                return next.Value;

            var amount = (time - previous.Time) / span;
            return previous.Value + (next.Value - previous.Value) * amount;
        }

        return keys[^1].Value;
    }

    private static int GetConsistentMorphTargetCount(Mesh mesh)
    {
        var counts = mesh.Primitives
            .Select(x => x.MorphTargetsCount)
            .Distinct()
            .ToArray();
        return counts.Length == 1 ? counts[0] : 0;
    }

    private static bool IsAnimatedTranslation(IReadOnlyDictionary<float, Vector3> keys)
    {
        if (keys.Count < 2)
            return false;

        var first = keys.OrderBy(x => x.Key).First().Value;
        return keys.Values.Any(value => Vector3.Distance(value, first) > TranslationAnimationEpsilon);
    }

    private static bool IsAnimatedScale(IReadOnlyDictionary<float, Vector3> keys)
    {
        if (keys.Count < 2)
            return false;

        var first = keys.OrderBy(x => x.Key).First().Value;
        return keys.Values.Any(value => Vector3.Distance(value, first) > TranslationAnimationEpsilon);
    }

    private static bool IsAnimatedRotation(IReadOnlyDictionary<float, Quaternion> keys)
    {
        if (keys.Count < 2)
            return false;

        var first = Quaternion.Normalize(keys.OrderBy(x => x.Key).First().Value);
        foreach (var value in keys.Values)
        {
            var current = Quaternion.Normalize(value);
            var dot = MathF.Abs(Quaternion.Dot(first, current));
            dot = Math.Clamp(dot, -1f, 1f);
            var angle = 2f * MathF.Acos(dot);
            if (angle > RotationAnimationEpsilonRadians)
                return true;
        }

        return false;
    }

    private static bool IsAnimatedScalar(IReadOnlyList<UEAnimKey<float>> keys)
    {
        if (keys.Count < 2)
            return false;

        var first = keys.OrderBy(x => x.Frame).First().Value;
        return keys.Any(key => MathF.Abs(key.Value - first) > MorphAnimationEpsilon);
    }

    private static bool IsAnimatedMorphKeyMap(IReadOnlyDictionary<float, float[]> keys)
    {
        if (keys.Count < 2)
            return false;

        var first = keys.OrderBy(x => x.Key).First().Value;
        foreach (var weights in keys.Values)
        {
            for (var i = 0; i < Math.Min(first.Length, weights.Length); i++)
            {
                if (MathF.Abs(weights[i] - first[i]) > MorphAnimationEpsilon)
                    return true;
            }
        }

        return false;
    }

    private static string NormalizeMorphName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
                builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private static Dictionary<int, string[]> ReadMorphTargetNamesByMeshIndex(string modelPath)
    {
        try
        {
            var json = ReadGltfJson(modelPath);
            if (string.IsNullOrWhiteSpace(json))
                return [];

            var root = JObject.Parse(json);
            var result = new Dictionary<int, string[]>();
            if (root["meshes"] is not JArray meshes)
                return result;

            for (var i = 0; i < meshes.Count; i++)
            {
                if (meshes[i] is not JObject mesh)
                    continue;

                var names = ReadTargetNames(mesh["extras"]);
                if (names.Length > 0)
                    result[i] = names;
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    private static string[] ReadTargetNames(JToken? extras)
    {
        if (extras == null)
            return [];

        JObject? extrasObject = extras as JObject;
        if (extras.Type == JTokenType.String)
        {
            var text = extras.Value<string>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    extrasObject = JObject.Parse(text);
                }
                catch
                {
                    return [];
                }
            }
        }

        return extrasObject?["targetNames"] is JArray targetNames
            ? targetNames.Values<string>().Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray()
            : [];
    }

    private static string ReadGltfJson(string path)
    {
        if (path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
            return File.ReadAllText(path, Encoding.UTF8);

        if (!path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            return "";

        var data = File.ReadAllBytes(path);
        if (data.Length < 20 || Encoding.ASCII.GetString(data, 0, 4) != "glTF")
            return "";

        var offset = 12;
        while (offset + 8 <= data.Length)
        {
            var chunkLength = BitConverter.ToInt32(data, offset);
            var chunkType = BitConverter.ToUInt32(data, offset + 4);
            offset += 8;
            if (chunkLength < 0 || offset + chunkLength > data.Length)
                return "";

            if (chunkType == 0x4E4F534A)
                return Encoding.UTF8.GetString(data, offset, chunkLength).TrimEnd('\0', ' ', '\r', '\n', '\t');

            offset += chunkLength;
        }

        return "";
    }

    private static ModelRoot LoadModelForPreview(string modelPath)
    {
        var fullPath = Path.GetFullPath(modelPath);
        var tempPath = TryCreateBomFreeGltfSibling(fullPath);
        try
        {
            return ModelRoot.Load(tempPath ?? fullPath, new ReadSettings
            {
                // Preview composition should accept UnrealExporter output even when
                // strict glTF validation would reject viewer-tolerated details.
                Validation = SharpGLTF.Validation.ValidationMode.Skip
            });
        }
        finally
        {
            if (tempPath != null)
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // A stale temp glTF is harmless; future runs use a new GUID name.
                }
            }
        }
    }

    private static string? TryCreateBomFreeGltfSibling(string modelPath)
    {
        if (!modelPath.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
            return null;

        using var input = File.OpenRead(modelPath);
        if (input.Length < 3)
            return null;

        Span<byte> bom = stackalloc byte[3];
        if (input.Read(bom) != 3 || bom[0] != 0xEF || bom[1] != 0xBB || bom[2] != 0xBF)
            return null;

        var directory = Path.GetDirectoryName(modelPath)!;
        var tempPath = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(modelPath)}.uepreview.{Guid.NewGuid():N}.gltf");
        using var output = File.Create(tempPath);
        input.CopyTo(output);
        return tempPath;
    }

    // Swapping Y/Z is a handedness-changing basis reflection; quaternion W must flip too.
    private static Quaternion SwapYZ(Quaternion value)
        => Quaternion.Normalize(new Quaternion(value.X, value.Z, value.Y, -value.W));

    private static TranslationKeyMap BuildTranslationKeyMap(UEAnimTrack track, Node node, float framesPerSecond)
    {
        var directKeys = BuildKeyMap(track.Positions, framesPerSecond, x => SwapYZ(x) * 0.01f);
        if (directKeys.Count == 0)
            return new TranslationKeyMap(directKeys, Retargeted: false, SkippedStatic: false);

        var first = directKeys
            .OrderBy(x => x.Key)
            .First()
            .Value;
        var rest = node.LocalTransform.Translation;
        if (IsTranslationCompatibleWithRest(first, rest))
            return new TranslationKeyMap(directKeys, Retargeted: false, SkippedStatic: false);

        if (directKeys.Count == 1)
            return new TranslationKeyMap([], Retargeted: true, SkippedStatic: true);

        // UE 同 Skeleton 可以被不同体型复用。动画 position 第一帧常带源体型骨长，
        // 直接写入 glTF 会覆盖目标模型 rest pose，导致骨骼散开。
        // 这里保留动作位移变化量，但用目标模型自己的 rest translation 作为基准。
        var retargetedKeys = directKeys.ToDictionary(
            x => x.Key,
            x => rest + (x.Value - first));
        return new TranslationKeyMap(retargetedKeys, Retargeted: true, SkippedStatic: false);
    }

    private static bool IsTranslationCompatibleWithRest(Vector3 first, Vector3 rest)
    {
        var distance = Vector3.Distance(first, rest);
        var restLength = rest.Length();
        var firstLength = first.Length();
        var lengthBase = MathF.Max(MathF.Max(restLength, firstLength), 0.01f);
        return distance <= MathF.Max(0.05f, lengthBase * 0.35f);
    }

    private static RotationKeyMap BuildRotationKeyMap(UEAnimTrack track, Node node, float framesPerSecond)
    {
        var directKeys = BuildKeyMap(track.Rotations, framesPerSecond, SwapYZ);
        if (directKeys.Count == 1)
        {
            var rest = Quaternion.Normalize(node.LocalTransform.Rotation);
            var first = directKeys.Values.First();
            if (!IsRotationCompatibleWithRest(first, rest))
                return new RotationKeyMap([], Retargeted: false, SkippedStatic: true);
        }

        return new RotationKeyMap(directKeys, Retargeted: false, SkippedStatic: false);
    }

    private static TranslationKeyMap BuildFormalTranslationKeyMap(UEAnimTrack track, float framesPerSecond)
        => new(BuildKeyMap(track.Positions, framesPerSecond, x => SwapYZ(x) * 0.01f), Retargeted: false, SkippedStatic: false);

    private static RotationKeyMap BuildFormalRotationKeyMap(UEAnimTrack track, float framesPerSecond)
        => new(BuildKeyMap(track.Rotations, framesPerSecond, SwapYZ), Retargeted: false, SkippedStatic: false);

    private static Dictionary<float, Vector3> BuildFormalScaleKeyMap(UEAnimTrack track, float framesPerSecond)
        => BuildKeyMap(track.Scales, framesPerSecond, SwapYZ);

    private static bool IsRotationCompatibleWithRest(Quaternion first, Quaternion rest)
    {
        var dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(first), Quaternion.Normalize(rest)));
        dot = Math.Clamp(dot, -1f, 1f);
        var angle = 2f * MathF.Acos(dot);
        return angle <= 0.35f;
    }

    private static Dictionary<float, TOut> BuildKeyMap<TIn, TOut>(
        IEnumerable<UEAnimKey<TIn>> keys,
        float framesPerSecond,
        Func<TIn, TOut> convert)
    {
        // UEAnim 可能保留相同帧的修正 key；glTF sampler 要求时间唯一，保留最后一个。
        var result = new Dictionary<float, TOut>();
        foreach (var key in keys)
        {
            result[key.Time(framesPerSecond)] = convert(key.Value);
        }

        return result;
    }

    private static void WriteReport(string? path, string? dbPath, object report)
    {
        var rawJson = JsonConvert.SerializeObject(report, Formatting.Indented);
        if (!string.IsNullOrWhiteSpace(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, rawJson);
        }

        if (!string.IsNullOrWhiteSpace(dbPath))
            WriteReportDb(dbPath, JObject.Parse(rawJson));
    }

    private static void WriteReportDb(string dbPath, JObject report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        SQLitePCL.Batteries_V2.Init();
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS preview_validation_reports (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                status TEXT NOT NULL,
                generated_at TEXT NOT NULL,
                gltf TEXT,
                model TEXT,
                animation TEXT,
                animation_name TEXT,
                frame_count INTEGER,
                frames_per_second REAL,
                duration REAL,
                track_count INTEGER,
                matched_tracks INTEGER,
                missing_track_count INTEGER,
                written_channels INTEGER,
                raw_json TEXT NOT NULL
            );
            """);

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO preview_validation_reports (
                id, status, generated_at, gltf, model, animation, animation_name,
                frame_count, frames_per_second, duration, track_count,
                matched_tracks, missing_track_count, written_channels, raw_json
            )
            VALUES (
                1, $status, $generatedAt, $gltf, $model, $animation, $animationName,
                $frameCount, $framesPerSecond, $duration, $trackCount,
                $matchedTracks, $missingTrackCount, $writtenChannels, $rawJson
            )
            ON CONFLICT(id) DO UPDATE SET
                status = excluded.status,
                generated_at = excluded.generated_at,
                gltf = excluded.gltf,
                model = excluded.model,
                animation = excluded.animation,
                animation_name = excluded.animation_name,
                frame_count = excluded.frame_count,
                frames_per_second = excluded.frames_per_second,
                duration = excluded.duration,
                track_count = excluded.track_count,
                matched_tracks = excluded.matched_tracks,
                missing_track_count = excluded.missing_track_count,
                written_channels = excluded.written_channels,
                raw_json = excluded.raw_json;
            """;
        Add(command, "$status", (string?)report["status"] ?? "unknown");
        Add(command, "$generatedAt", DateTime.UtcNow.ToString("O"));
        Add(command, "$gltf", (string?)report["gltf"]);
        Add(command, "$model", (string?)report["model"]);
        Add(command, "$animation", (string?)report["animation"]);
        Add(command, "$animationName", (string?)report["animationName"]);
        Add(command, "$frameCount", (int?)report["frameCount"]);
        Add(command, "$framesPerSecond", (double?)report["framesPerSecond"]);
        Add(command, "$duration", (double?)report["duration"]);
        Add(command, "$trackCount", (int?)report["trackCount"]);
        Add(command, "$matchedTracks", (int?)report["matchedTracks"]);
        Add(command, "$missingTrackCount", (int?)report["missingTrackCount"]);
        Add(command, "$writtenChannels", (int?)report["writtenChannels"]);
        Add(command, "$rawJson", report.ToString(Formatting.None));
        command.ExecuteNonQuery();
        Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

}

internal readonly record struct TranslationKeyMap(
    Dictionary<float, Vector3> Keys,
    bool Retargeted,
    bool SkippedStatic);

internal readonly record struct RotationKeyMap(
    Dictionary<float, Quaternion> Keys,
    bool Retargeted,
    bool SkippedStatic);

internal readonly record struct MorphCurveBinding(
    int TargetIndex,
    string NormalizedName,
    UEAnimCurve Curve);

internal readonly record struct MorphCurvePreviewResult(
    int AnimatedCurveCount,
    int MatchedAnimatedCurves,
    int UnmappedAnimatedCurves,
    int WrittenChannels,
    int IncompatibleMorphMeshes);
