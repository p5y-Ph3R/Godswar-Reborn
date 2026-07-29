using System.IO.Compression;
using System.Text;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static class NpcContentBaselineCodec
{
    private static readonly byte[] Magic = "GWNPCB01"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 =
        new(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

    private const int MaximumCompressedBytes = 1024 * 1024;
    private const int MaximumUncompressedBytes = 8 * 1024 * 1024;
    private const int MaximumSceneKeyBytes = 96 * 4;
    private const int MaximumNpcKeyBytes = 96 * 4;
    private const int MaximumTemplateKeyBytes = 128 * 4;
    private const int MaximumDetailBytes = ushort.MaxValue;

    public static byte[] Serialize(
        IEnumerable<NpcSpawnDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var canonical = definitions
            .OrderBy(static definition => definition.MapId)
            .ThenBy(
                static definition => definition.NpcKey,
                StringComparer.Ordinal)
            .ThenBy(
                static definition => definition.TemplateKey,
                StringComparer.Ordinal)
            .ThenBy(static definition => definition.ObjectId)
            .ToArray();
        if (canonical.Length > NpcContentLimits.MaximumDefinitions)
        {
            throw new InvalidDataException(
                "NPC baseline contains too many definitions.");
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
                writer.Write(definition.MapId);
                WriteString(
                    writer,
                    definition.SceneKey,
                    MaximumSceneKeyBytes);
                WriteString(
                    writer,
                    definition.NpcKey,
                    MaximumNpcKeyBytes);
                WriteString(
                    writer,
                    definition.TemplateKey,
                    MaximumTemplateKeyBytes);
                writer.Write(definition.ObjectId);
                writer.Write(definition.X);
                writer.Write(definition.Z);
                writer.Write(definition.InteractionId);
                writer.Write(definition.AppearanceType);
                writer.Write(definition.Facing);
                WriteBytes(
                    writer,
                    definition.Detail10077,
                    MaximumDetailBytes);
                WriteBytes(
                    writer,
                    definition.Detail10080,
                    MaximumDetailBytes);
            }
        }

        if (uncompressed.Length > MaximumUncompressedBytes)
        {
            throw new InvalidDataException(
                "NPC baseline exceeds the uncompressed size limit.");
        }

        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(
                   compressed,
                   CompressionLevel.SmallestSize,
                   leaveOpen: true))
        {
            uncompressed.Position = 0;
            uncompressed.CopyTo(brotli);
        }

        if (compressed.Length > MaximumCompressedBytes)
        {
            throw new InvalidDataException(
                "NPC baseline exceeds the compressed size limit.");
        }

        return compressed.ToArray();
    }

    public static NpcSpawnDefinition[] Deserialize(
        ReadOnlySpan<byte> compressed)
    {
        if (compressed.Length is <= 0 or > MaximumCompressedBytes)
        {
            throw new InvalidDataException(
                "NPC baseline compressed length is invalid.");
        }

        using var compressedStream =
            new MemoryStream(compressed.ToArray(), writable: false);
        using var brotli = new BrotliStream(
            compressedStream,
            CompressionMode.Decompress);
        using var uncompressed = ReadBoundedUncompressed(brotli);
        using var reader = new BinaryReader(
            uncompressed,
            StrictUtf8,
            leaveOpen: true);

        var magic = ReadExact(reader, Magic.Length);
        if (!magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException(
                "NPC baseline magic or format version is invalid.");
        }

        var count = reader.ReadInt32();
        if (count is < 0 or > NpcContentLimits.MaximumDefinitions)
        {
            throw new InvalidDataException(
                "NPC baseline definition count is invalid.");
        }

        var definitions = new NpcSpawnDefinition[count];
        for (var index = 0; index < count; index++)
        {
            definitions[index] = new NpcSpawnDefinition(
                reader.ReadInt16(),
                ReadString(reader, MaximumSceneKeyBytes),
                ReadString(reader, MaximumNpcKeyBytes),
                ReadString(reader, MaximumTemplateKeyBytes),
                reader.ReadUInt32(),
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                reader.ReadSingle(),
                ReadBytes(reader, MaximumDetailBytes),
                ReadBytes(reader, MaximumDetailBytes));
        }

        if (uncompressed.Position != uncompressed.Length)
        {
            throw new InvalidDataException(
                "NPC baseline contains trailing bytes.");
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
                    "NPC baseline expands beyond its size limit.");
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
                "NPC baseline field exceeds its size limit.");
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
                "NPC baseline field length is invalid.");
        }

        return ReadExact(reader, length);
    }

    private static byte[] ReadExact(BinaryReader reader, int length)
    {
        var value = reader.ReadBytes(length);
        if (value.Length != length)
        {
            throw new EndOfStreamException(
                "NPC baseline ended before a field was complete.");
        }

        return value;
    }
}
