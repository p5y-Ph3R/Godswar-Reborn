using System.Buffers.Binary;
using System.Text;

sealed partial class PacketTransactionLog
{
    private static bool TryParseCityNpcSpawn(PacketTransactionRecord packet, out CapturedNpcSpawnRecord spawn)
    {
        spawn = default;

        if (!string.Equals(packet.ConnectionName, "game", StringComparison.OrdinalIgnoreCase) ||
            packet.Direction != "S2C" ||
            packet.Opcode != 10020 ||
            packet.ClearBytes.Length < 108)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(packet.ClearBytes.AsSpan(0, 2));
        if (length > packet.ClearBytes.Length || length < 108)
        {
            return false;
        }

        var templateKey = ReadNullTerminatedAscii(packet.ClearBytes.AsSpan(44, length - 44));
        short mapId;
        string sceneKey;
        if (templateKey.StartsWith("Sparta_", StringComparison.Ordinal))
        {
            mapId = 0;
            sceneKey = "Sparta";
        }
        else if (templateKey.StartsWith("Athens_", StringComparison.Ordinal))
        {
            mapId = 1;
            sceneKey = "Athens";
        }
        else
        {
            return false;
        }

        var secondUnderscore = templateKey.IndexOf('_', "Athens_".Length);
        if (secondUnderscore < 0)
        {
            return false;
        }

        var npcKey = templateKey[..secondUnderscore];
        var objectId = BinaryPrimitives.ReadUInt32LittleEndian(packet.ClearBytes.AsSpan(8, 4));
        var x = BinaryPrimitives.ReadSingleLittleEndian(packet.ClearBytes.AsSpan(28, 4));
        var z = BinaryPrimitives.ReadSingleLittleEndian(packet.ClearBytes.AsSpan(36, 4));
        if (objectId == 0 || IsReservedPlayerObjectId(objectId) || !float.IsFinite(x) || !float.IsFinite(z))
        {
            return false;
        }

        spawn = new CapturedNpcSpawnRecord(
            mapId,
            sceneKey,
            npcKey,
            templateKey,
            objectId,
            x,
            z,
            packet.ClearBytes[..length]);
        return true;
    }

    private static bool TryParseMonsterSpawn(PacketTransactionRecord packet, out CapturedMonsterSpawnRecord spawn)
    {
        spawn = default;

        if (!string.Equals(packet.ConnectionName, "game", StringComparison.OrdinalIgnoreCase) ||
            packet.Direction != "S2C" ||
            packet.Opcode != 10020 ||
            packet.ClearBytes.Length < 108)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(packet.ClearBytes.AsSpan(0, 2));
        if (length > packet.ClearBytes.Length || length < 108)
        {
            return false;
        }

        var templateKey = ReadNullTerminatedAscii(packet.ClearBytes.AsSpan(44, length - 44));
        if (templateKey.StartsWith("Sparta_", StringComparison.Ordinal) ||
            templateKey.StartsWith("Athens_", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(templateKey))
        {
            return false;
        }

        var objectType = BinaryPrimitives.ReadUInt32LittleEndian(packet.ClearBytes.AsSpan(4, 4));
        if ((objectType & 0xFFu) != 0x12u)
        {
            return false;
        }

        var objectId = BinaryPrimitives.ReadUInt32LittleEndian(packet.ClearBytes.AsSpan(8, 4));
        var x = BinaryPrimitives.ReadSingleLittleEndian(packet.ClearBytes.AsSpan(28, 4));
        var z = BinaryPrimitives.ReadSingleLittleEndian(packet.ClearBytes.AsSpan(36, 4));
        if (objectId == 0 || IsReservedPlayerObjectId(objectId) || !float.IsFinite(x) || !float.IsFinite(z))
        {
            return false;
        }

        spawn = new CapturedMonsterSpawnRecord(
            templateKey,
            objectId,
            x,
            z,
            packet.ClearBytes[..length]);
        return true;
    }

    private static bool IsReservedPlayerObjectId(uint objectId)
    {
        return objectId == 0x1448 || objectId is >= 1 and <= 0x05DB;
    }

    private static bool TryParseNpcDetailPacket(PacketTransactionRecord packet, out CapturedNpcDetailRecord detail)
    {
        detail = default;

        if (!string.Equals(packet.ConnectionName, "game", StringComparison.OrdinalIgnoreCase) ||
            packet.Direction != "S2C" ||
            packet.Opcode is not (10077 or 10080) ||
            packet.ClearBytes.Length < 8)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(packet.ClearBytes.AsSpan(0, 2));
        if (length > packet.ClearBytes.Length || length < 8)
        {
            return false;
        }

        var objectId = BinaryPrimitives.ReadUInt32LittleEndian(packet.ClearBytes.AsSpan(4, 4));
        detail = new CapturedNpcDetailRecord(packet.Opcode.Value, objectId, packet.ClearBytes[..length]);
        return true;
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        return Encoding.ASCII.GetString(bytes[..length]);
    }
}
