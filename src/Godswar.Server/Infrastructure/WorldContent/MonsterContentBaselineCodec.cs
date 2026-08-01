using System.IO.Compression;
using System.Text;
using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static class MonsterContentBaselineCodec
{
    private static readonly byte[] Magic = "GWMONB01"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    private const int MaximumCompressedBytes = 4 * 1024 * 1024;
    private const int MaximumUncompressedBytes = 64 * 1024 * 1024;
    private const int MaximumSceneKeyBytes = 96 * 4;
    private const int MaximumTemplateKeyBytes = 128 * 4;
    private const int MaximumDisplayNameBytes = 255 * 4;

    public static byte[] Serialize(
        IEnumerable<CapturedMonsterSpawn> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var canonical = definitions
            .OrderBy(static definition => definition.MapId)
            .ThenBy(static definition => definition.ObjectId)
            .ThenBy(
                static definition => definition.TemplateKey,
                StringComparer.Ordinal)
            .ToArray();
        if (canonical.Length > MonsterContentLimits.MaximumDefinitions)
        {
            throw new InvalidDataException(
                "Monster baseline contains too many definitions.");
        }

        using var uncompressed = new MemoryStream();
        using (var writer = new BinaryWriter(
                   uncompressed,
                   StrictUtf8,
                   leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(canonical.Length);
            foreach (var definition in canonical)
            {
                definition.Validate(definition.MapId);
                writer.Write(definition.MapId);
                WriteString(writer, definition.SceneKey, MaximumSceneKeyBytes);
                WriteString(
                    writer,
                    definition.TemplateKey,
                    MaximumTemplateKeyBytes);
                WriteString(
                    writer,
                    definition.DisplayName,
                    MaximumDisplayNameBytes);
                writer.Write(definition.ObjectId);
                writer.Write(definition.X);
                writer.Write(definition.Z);
                WriteBytes(
                    writer,
                    definition.Packet,
                    MonsterContentLimits.MaximumAppearancePacketBytes);
            }
        }

        if (uncompressed.Length > MaximumUncompressedBytes)
        {
            throw new InvalidDataException(
                "Monster baseline exceeds the uncompressed size limit.");
        }

        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(
                   compressed,
                   CompressionLevel.Optimal,
                   leaveOpen: true))
        {
            uncompressed.Position = 0;
            uncompressed.CopyTo(gzip);
        }

        if (compressed.Length > MaximumCompressedBytes)
        {
            throw new InvalidDataException(
                "Monster baseline exceeds the compressed size limit.");
        }

        return compressed.ToArray();
    }

    public static CapturedMonsterSpawn[] Deserialize(
        ReadOnlySpan<byte> compressed)
    {
        if (compressed.Length is <= 0 or > MaximumCompressedBytes)
        {
            throw new InvalidDataException(
                "Monster baseline compressed length is invalid.");
        }

        using var compressedStream =
            new MemoryStream(compressed.ToArray(), writable: false);
        using var gzip = new GZipStream(
            compressedStream,
            CompressionMode.Decompress);
        using var uncompressed = ReadBoundedUncompressed(gzip);
        using var reader = new BinaryReader(
            uncompressed,
            StrictUtf8,
            leaveOpen: true);

        if (!ReadExact(reader, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException(
                "Monster baseline magic or format version is invalid.");
        }

        var count = reader.ReadInt32();
        if (count is < 0 or > MonsterContentLimits.MaximumDefinitions)
        {
            throw new InvalidDataException(
                "Monster baseline definition count is invalid.");
        }

        var definitions = new CapturedMonsterSpawn[count];
        for (var index = 0; index < count; index++)
        {
            definitions[index] = new CapturedMonsterSpawn(
                reader.ReadInt16(),
                ReadString(reader, MaximumSceneKeyBytes),
                ReadString(reader, MaximumTemplateKeyBytes),
                ReadString(reader, MaximumDisplayNameBytes),
                reader.ReadUInt32(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                ReadBytes(
                    reader,
                    MonsterContentLimits.MaximumAppearancePacketBytes));
            definitions[index].Validate(definitions[index].MapId);
        }

        if (uncompressed.Position != uncompressed.Length)
        {
            throw new InvalidDataException(
                "Monster baseline contains trailing bytes.");
        }

        return definitions;
    }

    private static MemoryStream ReadBoundedUncompressed(Stream source)
    {
        var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                destination.Position = 0;
                return destination;
            }

            if (destination.Length + read > MaximumUncompressedBytes)
            {
                destination.Dispose();
                throw new InvalidDataException(
                    "Monster baseline expands beyond its size limit.");
            }

            destination.Write(buffer, 0, read);
        }
    }

    private static void WriteString(
        BinaryWriter writer,
        string value,
        int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        WriteBytes(writer, StrictUtf8.GetBytes(value), maximumBytes);
    }

    private static string ReadString(
        BinaryReader reader,
        int maximumBytes) =>
        StrictUtf8.GetString(ReadBytes(reader, maximumBytes));

    private static void WriteBytes(
        BinaryWriter writer,
        byte[] value,
        int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > maximumBytes)
        {
            throw new InvalidDataException(
                "Monster baseline field exceeds its size limit.");
        }

        writer.Write(value.Length);
        writer.Write(value);
    }

    private static byte[] ReadBytes(
        BinaryReader reader,
        int maximumBytes)
    {
        var length = reader.ReadInt32();
        if (length is < 0 || length > maximumBytes)
        {
            throw new InvalidDataException(
                "Monster baseline field length is invalid.");
        }

        return ReadExact(reader, length);
    }

    private static byte[] ReadExact(BinaryReader reader, int length)
    {
        var value = reader.ReadBytes(length);
        if (value.Length != length)
        {
            throw new EndOfStreamException(
                "Monster baseline ended before a field was complete.");
        }

        return value;
    }
}
