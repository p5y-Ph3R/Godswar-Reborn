using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal readonly record struct ZodiacSyncRequest(
    uint PlayerId,
    ushort Module,
    ushort Sid,
    int Value1,
    int Value2,
    int Value3)
{
    private const int PacketLength = 24;

    public bool IsFullSync => Module == 0 && Sid == 1;

    public bool IsLevelUpgrade => Module == 0 && Sid == 3;

    public bool IsSkillGridActivation => Module == 0 && Sid == 100;

    public bool IsSkillGridUpgrade =>
        Module is 0 or 0xFF &&
        Sid == 101 &&
        Value2 == -1 &&
        Value3 == 0;

    public bool IsSkillGridSelection =>
        Module is 0 or 0xFF && Sid == 102 && Value3 == 0;

    public bool IsCharacterUiStatsV1Envelope =>
        Module == PacketBuilder.CharacterUiStatsModule &&
        Sid == PacketBuilder.CharacterUiStatsV1Sid;

    public bool IsCanonicalCharacterUiStatsV1Probe =>
        IsCharacterUiStatsV1Envelope &&
        PlayerId == 0 &&
        Value1 == 1 &&
        Value2 == 0 &&
        Value3 == 0;

    public static bool TryParse(ReadOnlySpan<byte> packet, out ZodiacSyncRequest request)
    {
        request = default;
        if (packet.Length != PacketLength ||
            BinaryPrimitives.ReadUInt16LittleEndian(packet[..2]) != PacketLength ||
            BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(2, 2)) !=
                Opcodes.Zodiac)
        {
            return false;
        }

        request = new ZodiacSyncRequest(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(4, 4)),
            BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(8, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(10, 2)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(12, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(16, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(20, 4)));
        return true;
    }
}
