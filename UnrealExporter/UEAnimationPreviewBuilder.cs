using System.Numerics;
using Newtonsoft.Json;
using SharpGLTF.IO;
using SharpGLTF.Schema2;

namespace UnrealExporter;

internal static class UEAnimationPreviewBuilder
{
    public static int Run(string modelPath, string animationPath, string outputPath, string? reportPath = null)
    {
        reportPath = string.IsNullOrWhiteSpace(reportPath)
            ? Path.ChangeExtension(Path.GetFullPath(outputPath), ".preview_validation.json")
            : Path.GetFullPath(reportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

        try
        {
            var animation = UEAnimReader.Read(animationPath);
            var model = ModelRoot.Load(modelPath, new ReadSettings());
            var nodesByName = model.LogicalNodes
                .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

            var gltfAnimation = model.CreateAnimation(animation.Name);
            gltfAnimation.Extras = JsonContent.Parse(JsonConvert.SerializeObject(new
            {
                unrealExporterPreview = new
                {
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
            var retargetedTranslationTracks = 0;
            var skippedStaticTranslationTracks = 0;
            var missingBones = new List<string>();
            foreach (var track in animation.Tracks)
            {
                if (!nodesByName.TryGetValue(track.BoneName, out var node))
                {
                    missingBones.Add(track.BoneName);
                    continue;
                }

                matchedTracks++;
                if (track.Positions.Count > 0)
                {
                    var translation = BuildTranslationKeyMap(track, node, animation.FramesPerSecond);
                    if (translation.Keys.Count > 0)
                    {
                        gltfAnimation.CreateTranslationChannel(node, translation.Keys, linear: true);
                        writtenChannels++;
                    }

                    if (translation.Retargeted)
                        retargetedTranslationTracks++;
                    if (translation.SkippedStatic)
                        skippedStaticTranslationTracks++;
                }

                if (track.Rotations.Count > 0)
                {
                    gltfAnimation.CreateRotationChannel(
                        node,
                        BuildKeyMap(track.Rotations, animation.FramesPerSecond, SwapYZ),
                        linear: true);
                    writtenChannels++;
                }

                if (track.Scales.Count > 0)
                {
                    gltfAnimation.CreateScaleChannel(
                        node,
                        BuildKeyMap(track.Scales, animation.FramesPerSecond, x => x),
                        linear: true);
                    writtenChannels++;
                }
            }

            model.SaveGLB(outputPath, new WriteSettings());
            var heavyTranslationRetarget = matchedTracks > 0 && retargetedTranslationTracks > matchedTracks * 0.5f;
            var status = matchedTracks > 0 && writtenChannels > 0 && File.Exists(outputPath)
                ? missingBones.Count == 0 && !heavyTranslationRetarget ? "ok" : "warning"
                : "error";
            WriteReport(reportPath, new
            {
                status,
                gltf = Path.GetFullPath(outputPath),
                report = reportPath,
                model = Path.GetFullPath(modelPath),
                animation = Path.GetFullPath(animationPath),
                animationName = animation.Name,
                frameCount = animation.FrameCount,
                framesPerSecond = animation.FramesPerSecond,
                duration = animation.FrameCount > 0 && animation.FramesPerSecond > 0
                    ? animation.FrameCount / animation.FramesPerSecond
                    : 0,
                trackCount = animation.Tracks.Count,
                matchedTracks,
                missingTrackCount = missingBones.Count,
                writtenChannels,
                retargetedTranslationTracks,
                skippedStaticTranslationTracks,
                heavyTranslationRetarget,
                missingBones = missingBones.Take(64).ToArray(),
            });

            Console.WriteLine($"=> {outputPath}");
            Console.WriteLine($"UE animation preview: {status}, matchedTracks={matchedTracks}, channels={writtenChannels}, missingBones={missingBones.Count}, retargetedTranslations={retargetedTranslationTracks}, skippedStaticTranslations={skippedStaticTranslationTracks}");
            return status == "error" ? 2 : 0;
        }
        catch (Exception ex)
        {
            WriteReport(reportPath, new
            {
                status = "error",
                gltf = (string?)null,
                report = reportPath,
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

    private static Quaternion SwapYZ(Quaternion value)
        => Quaternion.Normalize(new Quaternion(value.X, value.Z, value.Y, value.W));

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

    private static void WriteReport(string path, object report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonConvert.SerializeObject(report, Formatting.Indented));
    }

}

internal readonly record struct TranslationKeyMap(
    Dictionary<float, Vector3> Keys,
    bool Retargeted,
    bool SkippedStatic);
