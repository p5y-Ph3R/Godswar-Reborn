using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static class PetHealingTalentProtocolChecks
{
    public static Task RunAsync()
    {
        var sequence = PacketBuilder.PetHealingTalentResult(
            petObjectId: 70,
            ownerObjectId: 0x1448,
            appliedHealing: 150,
            skillId: PetHealingTalentPolicy.CombatTextSkillId,
            ownerX: 10f,
            ownerZ: 20f,
            currentHp: 550,
            currentMp: 40);

        Check.Equal((ushort)10045,
            BinaryPrimitives.ReadUInt16LittleEndian(
                sequence.CombatText.AsSpan(2, 2)),
            "pet Healing publishes green combat text first");
        Check.Equal(70u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                sequence.CombatText.AsSpan(4, 4)),
            "pet Healing combat text identifies summoned pet");
        Check.Equal(0x1448u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                sequence.CombatText.AsSpan(8, 4)),
            "pet Healing combat text targets its owner");
        Check.Equal(0x101u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                sequence.CombatText.AsSpan(12, 4)),
            "pet Healing uses native green-number result flags");
        Check.Equal(-150,
            BinaryPrimitives.ReadInt32LittleEndian(
                sequence.CombatText.AsSpan(16, 4)),
            "pet Healing encodes signed green amount");

        Check.Equal((ushort)0x2771,
            BinaryPrimitives.ReadUInt16LittleEndian(
                sequence.AuthoritativeVitals.AsSpan(2, 2)),
            "authoritative vitals follows pet Healing combat text");
        Check.Equal(0x1448u,
            BinaryPrimitives.ReadUInt32LittleEndian(
                sequence.AuthoritativeVitals.AsSpan(4, 4)),
            "pet Healing vitals targets owner");
        Check.Equal(550,
            BinaryPrimitives.ReadInt32LittleEndian(
                sequence.AuthoritativeVitals.AsSpan(8, 4)),
            "pet Healing vitals carries final HP");
        Check.Equal(40,
            BinaryPrimitives.ReadInt32LittleEndian(
                sequence.AuthoritativeVitals.AsSpan(12, 4)),
            "pet Healing vitals preserves MP");

        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.PetHealingTalentResult(
                70,
                0x1448,
                appliedHealing: 0,
                skillId: PetHealingTalentPolicy.CombatTextSkillId,
                ownerX: 0,
                ownerZ: 0,
                currentHp: 1,
                currentMp: 0),
            "pet Healing packet sequence rejects zero healing");
        return Task.CompletedTask;
    }
}
