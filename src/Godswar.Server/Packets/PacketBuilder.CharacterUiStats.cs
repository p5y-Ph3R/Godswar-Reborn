using System.Buffers.Binary;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal readonly record struct CharacterUiStatsV1Projection(
    int SpeedBasisPoints,
    int PhysicalPenetrationBasisPoints,
    int MagicPenetrationBasisPoints);

internal static partial class PacketBuilder
{
    internal const ushort CharacterUiStatsModule = 200;
    internal const ushort CharacterUiStatsV1Sid = 200;
    internal const int CharacterUiStatsV1PacketLength = 24;
    internal const int CharacterUiStatsBasisPointScale = 10_000;
    internal const int CharacterUiStatsMinimumSpeedBasisPoints = 1_000;
    internal const int CharacterUiStatsMaximumSpeedBasisPoints = 100_000;
    internal const int CharacterUiStatsMaximumPenetrationBasisPoints = 8_000;

    public static byte[] CharacterUiStatsV1(
        in CharacterUiStatsV1Projection projection)
    {
        var normalized = NormalizeCharacterUiStatsV1(projection);
        var packet = new byte[CharacterUiStatsV1PacketLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            CharacterUiStatsV1PacketLength);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            Opcodes.Zodiac);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            LocalPlayerObjectId);
        // Module 200 and SID 200 form a symmetric, globally reserved custom
        // envelope. Stock Origin ignores unknown SIDs above its 1..102 table.
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(8, 2),
            CharacterUiStatsModule);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(10, 2),
            CharacterUiStatsV1Sid);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(12, 4),
            normalized.SpeedBasisPoints);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(16, 4),
            normalized.PhysicalPenetrationBasisPoints);
        BinaryPrimitives.WriteInt32LittleEndian(
            packet.AsSpan(20, 4),
            normalized.MagicPenetrationBasisPoints);
        return packet;
    }

    internal static CharacterUiStatsV1Projection NormalizeCharacterUiStatsV1(
        in CharacterUiStatsV1Projection projection) =>
        new(
            Math.Clamp(
                projection.SpeedBasisPoints,
                CharacterUiStatsMinimumSpeedBasisPoints,
                CharacterUiStatsMaximumSpeedBasisPoints),
            Math.Clamp(
                projection.PhysicalPenetrationBasisPoints,
                0,
                CharacterUiStatsMaximumPenetrationBasisPoints),
            Math.Clamp(
                projection.MagicPenetrationBasisPoints,
                0,
                CharacterUiStatsMaximumPenetrationBasisPoints));
}
