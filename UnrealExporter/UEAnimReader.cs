using System.IO.Compression;
using System.Numerics;
using System.Text;
using CUE4Parse_Conversion.UEFormat.Enums;

namespace UnrealExporter;

internal static class UEAnimReader
{
    public static UEAnimData Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
        var magic = Encoding.UTF8.GetString(reader.ReadBytes("UEFORMAT".Length));
        if (magic != "UEFORMAT")
            throw new InvalidDataException("Not a UEFORMAT file.");

        var identifier = ReadFString(reader);
        if (!identifier.Equals("UEANIM", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsupported UEFORMAT identifier: {identifier}");

        _ = reader.ReadByte(); // file version
        var objectName = ReadFString(reader);
        var compressed = reader.ReadBoolean();
        byte[] payload;
        if (compressed)
        {
            var compression = ReadFString(reader);
            var uncompressedSize = reader.ReadInt32();
            var compressedSize = reader.ReadInt32();
            var compressedData = reader.ReadBytes(compressedSize);
            payload = Decompress(compressedData, compression, uncompressedSize);
        }
        else
        {
            payload = reader.ReadBytes((int)(reader.BaseStream.Length - reader.BaseStream.Position));
        }

        using var payloadReader = new BinaryReader(new MemoryStream(payload), Encoding.UTF8);
        var result = new UEAnimData { Name = objectName };
        while (payloadReader.BaseStream.Position < payloadReader.BaseStream.Length)
        {
            var chunkName = ReadFString(payloadReader);
            var count = payloadReader.ReadInt32();
            var size = payloadReader.ReadInt32();
            var chunkEnd = payloadReader.BaseStream.Position + size;
            switch (chunkName)
            {
                case "METADATA":
                    result.FrameCount = payloadReader.ReadInt32();
                    result.FramesPerSecond = payloadReader.ReadSingle();
                    _ = ReadFString(payloadReader); // ref pose path
                    _ = payloadReader.ReadByte(); // additive type
                    _ = payloadReader.ReadByte(); // ref pose type
                    _ = payloadReader.ReadInt32(); // ref frame index
                    break;
                case "TRACKS":
                    for (var i = 0; i < count; i++)
                        result.Tracks.Add(ReadTrack(payloadReader));
                    break;
            }

            payloadReader.BaseStream.Position = chunkEnd;
        }

        return result;
    }

    private static UEAnimTrack ReadTrack(BinaryReader reader)
    {
        var track = new UEAnimTrack(ReadFString(reader));
        var positionCount = reader.ReadInt32();
        for (var i = 0; i < positionCount; i++)
            track.Positions.Add(new UEAnimKey<Vector3>(reader.ReadInt32(), ReadVector3(reader)));

        var rotationCount = reader.ReadInt32();
        for (var i = 0; i < rotationCount; i++)
            track.Rotations.Add(new UEAnimKey<Quaternion>(reader.ReadInt32(), ReadQuaternion(reader)));

        var scaleCount = reader.ReadInt32();
        for (var i = 0; i < scaleCount; i++)
            track.Scales.Add(new UEAnimKey<Vector3>(reader.ReadInt32(), ReadVector3(reader)));

        return track;
    }

    private static byte[] Decompress(byte[] data, string compression, int expectedSize)
    {
        if (compression.Equals(EFileCompressionFormat.GZIP.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            using var input = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
            using var output = new MemoryStream(expectedSize > 0 ? expectedSize : data.Length * 2);
            input.CopyTo(output);
            return output.ToArray();
        }

        throw new NotSupportedException($"Unsupported UEAnim compression: {compression}");
    }

    private static string ReadFString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length == 0)
            return "";

        if (length > 0)
        {
            var bytes = reader.ReadBytes(length);
            return Encoding.UTF8.GetString(bytes);
        }

        var charCount = -length;
        var bytes16 = reader.ReadBytes(charCount * 2);
        return Encoding.Unicode.GetString(bytes16);
    }

    private static Vector3 ReadVector3(BinaryReader reader)
        => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static Quaternion ReadQuaternion(BinaryReader reader)
        => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}

internal sealed class UEAnimData
{
    public string Name { get; init; } = "";
    public int FrameCount { get; set; }
    public float FramesPerSecond { get; set; } = 30;
    public List<UEAnimTrack> Tracks { get; } = [];
}

internal sealed class UEAnimTrack
{
    public UEAnimTrack(string boneName)
    {
        BoneName = boneName;
    }

    public string BoneName { get; }
    public List<UEAnimKey<Vector3>> Positions { get; } = [];
    public List<UEAnimKey<Quaternion>> Rotations { get; } = [];
    public List<UEAnimKey<Vector3>> Scales { get; } = [];
}

internal readonly record struct UEAnimKey<T>(int Frame, T Value)
{
    public float Time(float framesPerSecond)
        => framesPerSecond > 0 ? Frame / framesPerSecond : Frame / 30f;
}
