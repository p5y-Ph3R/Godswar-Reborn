using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] TalentUpgradeAck(TalentUpgradeResult result)
    {
        const int packetLength = 28;
        var packet = new byte[packetLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), packetLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2741);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), result.TalentId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12, 4), result.NewRank);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16, 4), result.Cost);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(20, 4), result.RemainingTalentPoints);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(24, 4), result.DisplayValue);
        return packet;
    }

    public static byte[] TalentRankList(IReadOnlyList<TalentState> talents)
    {
        if (talents.Count == 0)
        {
            return [];
        }

        const int headerLength = 12;
        const int recordLength = 16;
        var packet = new byte[headerLength + (talents.Count * recordLength)];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), TalentRankListOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), talents.Count);

        for (var i = 0; i < talents.Count; i++)
        {
            var offset = headerLength + (i * recordLength);
            var talent = talents[i];
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset, 4), talent.TalentId);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset + 4, 4), talent.Rank);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset + 8, 4), talent.DisplayValue);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset + 12, 4), talent.NextCost);
        }

        return packet;
    }

    public static byte[] TalentSkillUnlockList(IReadOnlyList<SkillState> skills)
    {
        if (skills.Count == 0)
        {
            return [];
        }

        const int headerLength = 12;
        const int recordLength = 8;
        var packet = new byte[headerLength + (skills.Count * recordLength)];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), TalentSkillUnlockListOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), skills.Count);

        for (var i = 0; i < skills.Count; i++)
        {
            var offset = headerLength + (i * recordLength);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset, 4), skills[i].SkillId);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset + 4, 4), 0);
        }

        return packet;
    }

    public static byte[] ChampionTalentSkillUnlockList()
    {
        var packet = new byte[28];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), TalentSkillUnlockListOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), LocalPlayerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12, 4), 250);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(16, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(20, 4), 3062);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(24, 4), 0);
        return packet;
    }

    public static byte[] SkillList(IReadOnlyList<SkillState> skills)
    {
        if (skills.Count == 0)
        {
            return [];
        }

        const int headerLength = 12;
        const int recordLength = 12;
        var packet = new byte[headerLength + (skills.Count * recordLength)];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), SkillListOpcode);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), skills.Count);

        for (var i = 0; i < skills.Count; i++)
        {
            var offset = headerLength + (i * recordLength);
            var skill = skills[i];
            var levelFlag = 0x100 | Math.Clamp(skill.Level, 1, 255);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset, 4), skill.SkillId);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset + 4, 4), levelFlag);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(offset + 8, 4), 0);
        }

        return packet;
    }

    public static byte[] SkillListBootstrap()
    {
        return EmptySkillListBootstrapTemplate.ToArray();
    }
}
