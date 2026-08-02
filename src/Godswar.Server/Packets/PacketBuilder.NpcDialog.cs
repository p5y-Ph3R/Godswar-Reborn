using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] AthensNpc(GameCharacter character)
    {
        var stream = ValidPacketStreamPrefix(ReferencePackets.AthensNpc);
        return PatchReferencePlayerPackets(stream, character);
    }

    public static byte[] CityNpcFallback(GameCharacter character)
    {
        if (character.CurrentMap is not (0 or 1))
        {
            return [];
        }

        var packet = AthensNpc(character);
        if (character.CurrentMap == 0)
        {
            ReplaceAscii(packet, AthensTemplatePrefix, SpartaTemplatePrefix);
        }

        return packet;
    }

    public static byte[] CapturedNpcSpawns(IReadOnlyList<CapturedNpcSpawn> spawns)
    {
        if (spawns.Count == 0)
        {
            return [];
        }

        var length = 0;
        foreach (var spawn in spawns)
        {
            length += spawn.Packet.Length;
            length += spawn.Detail10077.Length;
            length += spawn.Detail10080.Length;
        }

        var stream = new byte[length];
        var offset = 0;
        foreach (var spawn in spawns)
        {
            spawn.Packet.CopyTo(stream.AsSpan(offset));
            offset += spawn.Packet.Length;
            spawn.Detail10077.CopyTo(stream.AsSpan(offset));
            offset += spawn.Detail10077.Length;
            spawn.Detail10080.CopyTo(stream.AsSpan(offset));
            offset += spawn.Detail10080.Length;
        }

        return stream;
    }

    public static byte[] NpcSpawns(IReadOnlyList<NpcSpawnDefinition> spawns)
    {
        if (spawns.Count == 0)
        {
            return [];
        }

        var length = 0;
        foreach (var spawn in spawns)
        {
            length = checked(
                length +
                WorldObjectAppearanceLength +
                spawn.Detail10077.Length +
                spawn.Detail10080.Length);
        }

        var stream = new byte[length];
        var offset = 0;
        foreach (var spawn in spawns)
        {
            WriteNpcWorldObjectAppearance(
                stream.AsSpan(offset, WorldObjectAppearanceLength),
                spawn);
            offset += WorldObjectAppearanceLength;
            spawn.Detail10077.CopyTo(stream.AsSpan(offset));
            offset += spawn.Detail10077.Length;
            spawn.Detail10080.CopyTo(stream.AsSpan(offset));
            offset += spawn.Detail10080.Length;
        }

        return stream;
    }

    public static int CountCityNpcSpawnPackets(ReadOnlySpan<byte> stream)
    {
        var count = 0;
        var offset = 0;
        while (offset + 4 <= stream.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(stream[offset..]);
            if (length < 4 || offset + length > stream.Length)
            {
                break;
            }

            var opcode = BinaryPrimitives.ReadUInt16LittleEndian(stream.Slice(offset + 2, 2));
            if (opcode == 0x2724)
            {
                count++;
            }

            offset += length;
        }

        return count;
    }

    public static byte[] NpcDialogOpenAck(uint npcId, int dialogIndex, string scriptKey)
    {
        return NpcDialogOpenAck(npcId, [dialogIndex], scriptKey);
    }

    /// <summary>
    /// Advertises ordered top-level extended NPC functions. The stock client
    /// decodes the field at offset 12 as base-1000 digits, starting with the
    /// least-significant digit, so 4 followed by 37 is encoded as 37004.
    /// </summary>
    public static byte[] NpcDialogOpenAck(
        uint npcId,
        IReadOnlyList<int> dialogIndices,
        string scriptKey)
    {
        var packet = new byte[48];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), NpcDialogOpenOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), npcId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), 0x200);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12, 4),
            PackNpcDialogIndices(dialogIndices));
        PacketText.WriteFixedAscii(packet.AsSpan(16, 32), scriptKey);
        return packet;
    }

    internal static int PackNpcDialogIndices(
        IReadOnlyList<int> dialogIndices)
    {
        ArgumentNullException.ThrowIfNull(dialogIndices);
        if (dialogIndices.Count is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dialogIndices),
                "The stock client supports one to three packed NPC dialogs.");
        }

        var packed = 0;
        var multiplier = 1;
        var seen = new HashSet<int>();
        foreach (var dialogIndex in dialogIndices)
        {
            if (dialogIndex is < 1 or > 999)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(dialogIndices),
                    "Each packed NPC dialog index must be between 1 and 999.");
            }

            if (!seen.Add(dialogIndex))
            {
                throw new ArgumentException(
                    "Packed NPC dialog indices must be unique.",
                    nameof(dialogIndices));
            }

            packed = checked(packed + (dialogIndex * multiplier));
            multiplier = checked(multiplier * 1000);
        }

        return packed;
    }

    /// <summary>
    /// Builds a private Talk reply using the identity and channel metadata from
    /// the command packet. The stock client includes the UTF-16 terminator in
    /// its length field but omits the terminator itself from the packet body.
    /// </summary>
    public static byte[] DeveloperCommandTalkReply(
        ReadOnlySpan<byte> requestPayload,
        string message)
    {
        const int talkMetadataLength = 12;
        const int talkTextLengthOffset = 4;
        const int talkChannelOffset = 8;
        const int packetHeaderLength = 4;
        if (requestPayload.Length < talkMetadataLength)
        {
            throw new ArgumentException("Talk request payload is incomplete.", nameof(requestPayload));
        }

        var text = Encoding.Unicode.GetBytes(message);
        var packetLength = checked(packetHeaderLength + talkMetadataLength + text.Length);
        if (packetLength > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(message), "Talk reply is too long.");
        }

        var packet = new byte[packetLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), Opcodes.Talk);
        requestPayload[..sizeof(uint)].CopyTo(packet.AsSpan(packetHeaderLength, sizeof(uint)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(packetHeaderLength + talkTextLengthOffset, sizeof(uint)),
            checked((uint)text.Length + sizeof(ushort)));
        requestPayload.Slice(talkChannelOffset, sizeof(uint)).CopyTo(
            packet.AsSpan(packetHeaderLength + talkChannelOffset, sizeof(uint)));
        text.CopyTo(packet.AsSpan(packetHeaderLength + talkMetadataLength));
        return packet;
    }

    public static byte[] NpcFunctionActionResponse(uint npcId, int dialogIndex, params int[] subIds)
    {
        var packet = new byte[12 + (subIds.Length * 4)];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), NpcFunctionActionResponseOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), npcId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), dialogIndex);

        for (var i = 0; i < subIds.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12 + (i * 4), 4), subIds[i]);
        }

        return packet;
    }

    private static void WriteNpcWorldObjectAppearance(
        Span<byte> packet,
        NpcSpawnDefinition spawn)
    {
        packet.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(packet[..2], WorldObjectAppearanceLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(2, 2), 0x2724);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.Slice(4, 4),
            spawn.AppearanceType == 0
                ? NpcAppearanceDefaults.AppearanceType
                : spawn.AppearanceType);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(8, 4), spawn.ObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(12, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.Slice(24, 4), 1521);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(28, 4), spawn.X);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(32, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.Slice(36, 4), spawn.Z);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.Slice(40, 4),
            float.IsFinite(spawn.Facing)
                ? spawn.Facing
                : NpcAppearanceDefaults.Facing);
        PacketText.WriteFixedAscii(
            packet.Slice(WorldObjectTemplateOffset, WorldObjectTemplateLength),
            spawn.TemplateKey);
    }
}
