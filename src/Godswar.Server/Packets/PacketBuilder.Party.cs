using System.Buffers.Binary;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] PartyAction(
        ushort opcode,
        uint actorObjectId,
        string firstName,
        string secondName)
    {
        if (!PartyProtocol.IsClientAction(opcode) &&
            opcode != Opcodes.PartyDestroy)
        {
            throw new ArgumentOutOfRangeException(nameof(opcode));
        }

        var packet = new byte[PartyProtocol.ActionPacketBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, sizeof(ushort)),
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)),
            opcode);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(
                PartyProtocol.ActionObjectIdOffset,
                sizeof(uint)),
            actorObjectId);
        PacketText.WriteFixedAscii(
            packet.AsSpan(
                PartyProtocol.FirstNameOffset,
                PartyProtocol.NameBytes),
            firstName);
        PacketText.WriteFixedAscii(
            packet.AsSpan(
                PartyProtocol.SecondNameOffset,
                PartyProtocol.NameBytes),
            secondName);
        return packet;
    }

    public static byte[] PartyRefresh(
        IReadOnlyList<PartyMemberSnapshot> members,
        int recipientCharacterId)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count is < 1 or > PartyProtocol.MaximumMembers)
        {
            throw new ArgumentOutOfRangeException(nameof(members));
        }

        var packet = new byte[PartyProtocol.RefreshPacketBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, sizeof(ushort)),
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)),
            Opcodes.PartyRefresh);

        for (var index = 0;
             index < PartyProtocol.MaximumMembers;
             index++)
        {
            var record = packet.AsSpan(
                4 + index * PartyProtocol.RefreshMemberBytes,
                PartyProtocol.RefreshMemberBytes);
            if (index >= members.Count)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    record,
                    uint.MaxValue);
                continue;
            }

            var member = members[index];
            BinaryPrimitives.WriteUInt32LittleEndian(
                record,
                member.CharacterId == recipientCharacterId
                    ? LocalPlayerObjectId
                    : member.ObjectId);
            BinaryPrimitives.WriteInt32LittleEndian(
                record.Slice(4, sizeof(int)),
                member.CurrentHp);
            BinaryPrimitives.WriteInt32LittleEndian(
                record.Slice(8, sizeof(int)),
                member.MaxHp);
            BinaryPrimitives.WriteInt32LittleEndian(
                record.Slice(12, sizeof(int)),
                member.Level);
            record[16] = member.Profession;
            PacketText.WriteFixedAscii(record.Slice(17, 65), member.Name);
            BinaryPrimitives.WriteInt16LittleEndian(
                record.Slice(82, sizeof(short)),
                member.MapId);
            BinaryPrimitives.WriteSingleLittleEndian(
                record.Slice(84, sizeof(float)),
                member.PositionX);
            BinaryPrimitives.WriteSingleLittleEndian(
                record.Slice(88, sizeof(float)),
                0f);
            BinaryPrimitives.WriteSingleLittleEndian(
                record.Slice(92, sizeof(float)),
                member.PositionZ);
        }

        return packet;
    }
}
