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
                    gltfAnimation.CreateTranslationChannel(
                        node,
                        BuildKeyMap(track.Positions, animation.FramesPerSecond, x => SwapYZ(x) * 0.01f),
                        linear: true);
                    writtenChannels++;
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
            var status = matchedTracks > 0 && writtenChannels > 0 && File.Exists(outputPath)
                ? missingBones.Count == 0 ? "ok" : "warning"
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
                missingBones = missingBones.Take(64).ToArray(),
            });

            Console.WriteLine($"=> {outputPath}");
            Console.WriteLine($"UE animation preview: {status}, matchedTracks={matchedTracks}, channels={writtenChannels}, missingBones={missingBones.Count}");
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
