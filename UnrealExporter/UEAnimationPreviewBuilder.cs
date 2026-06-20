using System.Numerics;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SharpGLTF.IO;
using SharpGLTF.Schema2;

namespace UnrealExporter;

internal static class UEAnimationPreviewBuilder
{
    public static int Run(
        string modelPath,
        string animationPath,
        string outputPath,
        string? reportPath = null,
        string? reportDbPath = null,
        string? skipBoneRegex = null)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        reportPath = string.IsNullOrWhiteSpace(reportPath) ? null : Path.GetFullPath(reportPath);
        reportDbPath = string.IsNullOrWhiteSpace(reportDbPath) ? null : Path.GetFullPath(reportDbPath);
        if (reportPath == null && reportDbPath == null)
            reportDbPath = Path.ChangeExtension(fullOutputPath, ".preview_validation.db");

        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        if (reportPath != null)
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        if (reportDbPath != null)
            Directory.CreateDirectory(Path.GetDirectoryName(reportDbPath)!);

        try
        {
            var animation = UEAnimReader.Read(animationPath);
            var model = ModelRoot.Load(modelPath, new ReadSettings());
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
            var retargetedRotationTracks = 0;
            var skippedStaticRotationTracks = 0;
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
                    var rotation = BuildRotationKeyMap(track, node, animation.FramesPerSecond);
                    if (rotation.Keys.Count > 0)
                    {
                        gltfAnimation.CreateRotationChannel(
                            node,
                            rotation.Keys,
                            linear: true);
                        writtenChannels++;
                    }

                    if (rotation.Retargeted)
                        retargetedRotationTracks++;
                    if (rotation.SkippedStatic)
                        skippedStaticRotationTracks++;
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
            UnrealExporter.SanitizeGlbForPreview(outputPath);
            var anyRetarget = retargetedTranslationTracks > 0 || retargetedRotationTracks > 0;
            var anyAdjustedOrSkipped = anyRetarget || skippedStaticTranslationTracks > 0 || skippedStaticRotationTracks > 0;
            var heavyTranslationRetarget = matchedTracks > 0 && retargetedTranslationTracks > matchedTracks * 0.5f;
            var status = matchedTracks > 0 && writtenChannels > 0 && File.Exists(outputPath)
                ? missingBones.Count == 0 && !anyAdjustedOrSkipped && !heavyTranslationRetarget ? "ok" : "warning"
                : "error";
            WriteReport(reportPath, reportDbPath, new
            {
                status,
                gltf = fullOutputPath,
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
                matchedTracks,
                missingTrackCount = missingBones.Count,
                writtenChannels,
                retargetedTranslationTracks,
                skippedStaticTranslationTracks,
                retargetedRotationTracks,
                skippedStaticRotationTracks,
                skippedByRegexTracks,
                skipBoneRegex,
                anyRetarget,
                anyAdjustedOrSkipped,
                heavyTranslationRetarget,
                visualAcceptance = new
                {
                    status = anyAdjustedOrSkipped || missingBones.Count > 0 ? "notAccepted" : "requiresManualReview",
                    reason = anyAdjustedOrSkipped
                        ? "Preview generation applied or skipped uncertain transform channels. This can prove a diagnostic preview was generated, but cannot prove humanoid animation correctness."
                        : "Preview generated without automatic retargeting, but humanoid animation correctness still requires manual rest/mid/end visual review.",
                    requiresScreenshots = new[] { "restPose", "animationStart", "animationMiddle", "animationEnd" }
                },
                missingBones = missingBones.Take(64).ToArray(),
            });

            Console.WriteLine($"=> {outputPath}");
            Console.WriteLine($"UE animation preview: {status}, matchedTracks={matchedTracks}, channels={writtenChannels}, missingBones={missingBones.Count}, retargetedTranslations={retargetedTranslationTracks}, skippedStaticTranslations={skippedStaticTranslationTracks}, retargetedRotations={retargetedRotationTracks}, skippedStaticRotations={skippedStaticRotationTracks}, skippedByRegex={skippedByRegexTracks}");
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
