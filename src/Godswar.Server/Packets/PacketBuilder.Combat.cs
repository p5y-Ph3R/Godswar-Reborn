using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] SkillCastVisual(ReadOnlySpan<byte> clientSkillCastPacket, uint objectId)
    {
        var packet = clientSkillCastPacket.ToArray();
        if (packet.Length < 8)
        {
            return packet;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), 0x2738);
        PatchSkillCastObjectId(packet, 4, objectId);
        // The working server preserves the selected target at offset 16 and
        // advances the cast state at offset 20 from the client value 0 to 10.
        if (packet.Length >= 24)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20, 4), 10);
        }

        return packet;
    }

    public static byte[] SelfTargetSkillCastVisual(
        ReadOnlySpan<byte> clientSkillCastPacket,
        uint objectId)
    {
        var packet = SkillCastVisual(clientSkillCastPacket, objectId);
        // A self-targeted cast arrives with the client's fixed local player ID at
        // offset 16. Remote viewers need both identities translated to the
        // caster's world object ID.
        PatchSkillCastObjectId(packet, 16, objectId);
        return packet;
    }

    public static byte[] SkillCastImpact(ReadOnlySpan<byte> clientSkillCastPacket, uint objectId)
    {
        var targetObjectId = clientSkillCastPacket.Length >= 20
            ? BinaryPrimitives.ReadUInt32LittleEndian(clientSkillCastPacket.Slice(16, 4))
            : 0;
        var skillId = clientSkillCastPacket.Length >= 12
            ? BinaryPrimitives.ReadUInt32LittleEndian(clientSkillCastPacket.Slice(8, 4))
            : 0;
        var targetX = 0f;
        var targetZ = 0f;
        if (clientSkillCastPacket.Length >= 40)
        {
            targetX = BinaryPrimitives.ReadSingleLittleEndian(clientSkillCastPacket.Slice(32, 4));
            targetZ = BinaryPrimitives.ReadSingleLittleEndian(clientSkillCastPacket.Slice(36, 4));
        }
        else if (clientSkillCastPacket.Length >= 32)
        {
            targetX = BinaryPrimitives.ReadSingleLittleEndian(clientSkillCastPacket.Slice(24, 4));
            targetZ = BinaryPrimitives.ReadSingleLittleEndian(clientSkillCastPacket.Slice(28, 4));
        }

        return SkillCastImpact(objectId, targetObjectId, skillId, targetX, targetZ);
    }

    public static byte[] SkillCastImpact(
        uint attackerObjectId,
        uint targetObjectId,
        uint skillId,
        float targetX,
        float targetZ)
    {
        var packet = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), SkillCastImpactOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), attackerObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), targetObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), skillId);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(16, 4), targetX);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(20, 4), targetZ);
        return packet;
    }

    public static byte[] SkillCastInterrupt(uint playerObjectId)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            Opcodes.SkillCastInterrupt);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), playerObjectId);
        return packet;
    }

    public static byte[] SkillDamage(
        uint attackerObjectId,
        uint targetObjectId,
        uint resultFlags,
        uint damage,
        uint skillId,
        float targetX,
        float targetZ)
    {
        var packet = new byte[32];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), SkillDamageOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), attackerObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), targetObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), resultFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16, 4), damage);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20, 4), skillId);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(24, 4), targetX);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28, 4), targetZ);
        return packet;
    }

    public static byte[] SkillHealing(
        uint healerObjectId,
        uint targetObjectId,
        int healing,
        uint skillId,
        float targetX,
        float targetZ)
    {
        return SkillDamage(
            healerObjectId,
            targetObjectId,
            resultFlags: 0x101,
            damage: EncodeHealingAmount(healing),
            skillId,
            targetX,
            targetZ);
    }

    public static byte[] SkillClusterDamage(
        uint attackerObjectId,
        uint skillId,
        IReadOnlyList<SkillClusterDamageEntry> hits)
    {
        ArgumentNullException.ThrowIfNull(hits);

        const int headerLength = 17;
        const int hitLength = 12;
        var maximumHits = (ushort.MaxValue - headerLength) / hitLength;
        if (hits.Count > maximumHits)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hits),
                hits.Count,
                $"A skill cluster packet supports at most {maximumHits} hits.");
        }

        var packet = new byte[headerLength + (hits.Count * hitLength)];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), SkillClusterDamageOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), attackerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), hits.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12, 4), skillId);
        // Offset 16 is the aggregate status-hit flag. Status application is not
        // modeled yet, so report no status while preserving the captured layout.
        packet[16] = 0;

        for (var index = 0; index < hits.Count; index++)
        {
            var hit = hits[index];
            var offset = headerLength + (index * hitLength);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset, 4), hit.TargetObjectId);
            packet[offset + 4] = hit.AttackType;
            packet[offset + 5] = hit.DamageType;
            // Offsets +6 and +7 are the captured alignment padding and remain 0.
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset + 8, 4), hit.Damage);
        }

        return packet;
    }

    public static byte[] SkillClusterHealing(
        uint healerObjectId,
        uint skillId,
        IReadOnlyList<SkillClusterHealingEntry> heals)
    {
        ArgumentNullException.ThrowIfNull(heals);

        var encoded = new SkillClusterDamageEntry[heals.Count];
        for (var index = 0; index < heals.Count; index++)
        {
            var heal = heals[index];
            encoded[index] = new SkillClusterDamageEntry(
                heal.TargetObjectId,
                EncodeHealingAmount(heal.Healing));
        }

        return SkillClusterDamage(
            healerObjectId,
            skillId,
            encoded);
    }

    public static byte[] PhysicalDamage(
        uint attackerObjectId,
        float attackerX,
        float attackerY,
        float attackerZ,
        uint targetObjectId,
        uint damage,
        byte result,
        byte damageType = 1)
    {
        var packet = new byte[30];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PhysicalDamageOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), attackerObjectId);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(8, 4), attackerX);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(12, 4), attackerY);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(16, 4), attackerZ);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20, 4), targetObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24, 4), damage);
        packet[28] = result;
        packet[29] = damageType;
        return packet;
    }

    public static byte[] PlayerDeath(
        uint playerObjectId,
        float x,
        float y,
        float z,
        uint mapId)
    {
        var packet = new byte[28];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerDeathOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), playerObjectId);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(8, 4), x);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(12, 4), y);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(16, 4), z);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(20, 4), mapId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24, 4), 1);
        return packet;
    }

    public static byte[] ExperienceGain(
        long gainedExperience,
        long currentExperience,
        byte result = 0)
    {
        var packet = new byte[13];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), ExperienceGainOpcode);
        // This client reads +4 only for its "Get EXP" toast. The first working
        // capture had equal gained/current values, which previously hid this.
        WriteLegacyFighterExperience(
            packet.AsSpan(4, 4),
            gainedExperience,
            nameof(gainedExperience));
        WriteLegacyFighterExperience(
            packet.AsSpan(8, 4),
            currentExperience,
            nameof(currentExperience));
        packet[12] = result;
        return packet;
    }

    public static byte[] MonsterDeathReward(
        uint monsterObjectId,
        uint playerObjectId,
        long currentExperience,
        int currentTalentExperience,
        int currentTalentPoints)
    {
        const int partySlots = 5;
        var packet = new byte[116];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), MonsterDeathRewardOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), monsterObjectId);

        for (var index = 0; index < partySlots; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                packet.AsSpan(8 + (index * sizeof(int)), sizeof(int)),
                index == 0 ? unchecked((int)playerObjectId) : -1);
        }

        WriteLegacyFighterExperience(
            packet.AsSpan(48, 4),
            currentExperience,
            nameof(currentExperience));
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(68, 4), Math.Max(0, currentTalentExperience));
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(88, 4), Math.Max(0, currentTalentPoints));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(108, 4), monsterObjectId);
        return packet;
    }

    public static byte[] PlayerLevelUp(
        uint playerObjectId,
        int level,
        long experienceMaximum,
        long currentExperience,
        int maxHp,
        int currentHp,
        int maxMp,
        int currentMp)
    {
        var packet = new byte[36];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerLevelUpOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), playerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), Math.Max(1, level));
        WriteLegacyFighterExperience(
            packet.AsSpan(12, 4),
            experienceMaximum,
            nameof(experienceMaximum));
        WriteLegacyFighterExperience(
            packet.AsSpan(16, 4),
            currentExperience,
            nameof(currentExperience));
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(20, 4), Math.Max(1, maxHp));
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(24, 4), Math.Clamp(currentHp, 0, Math.Max(1, maxHp)));
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(28, 4), Math.Max(0, maxMp));
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(32, 4), Math.Clamp(currentMp, 0, Math.Max(0, maxMp)));
        return packet;
    }

    public static byte[] TalentExperienceGain(int gainedTalentExperience)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), AttributeGainOpcode);
        // Attribute-note type 4 is "Talent Exp" in the shipped client data.
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(4, 4), 4);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), Math.Max(0, gainedTalentExperience));
        return packet;
    }

    public static byte[] PlayerManaUpdate(uint attackerObjectId, int currentMp)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerManaUpdateOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), attackerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), Math.Max(0, currentMp));
        return packet;
    }

    public static byte[] PlayerVitalsUpdate(uint playerObjectId, int currentHp, int currentMp)
    {
        var packet = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerVitalsUpdateOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), playerObjectId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8, 4), Math.Max(0, currentHp));
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12, 4), Math.Max(0, currentMp));
        return packet;
    }

    private static void PatchSkillCastObjectId(byte[] packet, int offset, uint objectId)
    {
        if (packet.Length < offset + 4)
        {
            return;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(offset, 4), objectId);
    }

    private static uint EncodeHealingAmount(int healing)
    {
        if (healing <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(healing),
                healing,
                "A healing combat-text amount must be positive.");
        }

        // The stock client uses the damage field as a signed integer. A
        // negative value plus result flag 0x101 renders green +healing text.
        return unchecked((uint)-healing);
    }
}

internal readonly record struct SkillClusterDamageEntry(
    uint TargetObjectId,
    uint Damage,
    byte AttackType = 1,
    byte DamageType = 0);

internal readonly record struct SkillClusterHealingEntry(
    uint TargetObjectId,
    int Healing);
