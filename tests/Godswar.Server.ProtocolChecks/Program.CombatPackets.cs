using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static Task CheckSkillCastTargetAndImpactAsync()
    {
        const uint localObjectId = 0x1448;
        const uint remoteCasterId = 0x0002;
        const uint monsterId = 0x282C;
        var clientCast = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(clientCast.AsSpan(0, 2), (ushort)clientCast.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(clientCast.AsSpan(2, 2), 10040);
        BinaryPrimitives.WriteUInt32LittleEndian(clientCast.AsSpan(4, 4), localObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(clientCast.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(clientCast.AsSpan(16, 4), monsterId);
        BinaryPrimitives.WriteSingleLittleEndian(clientCast.AsSpan(24, 4), 41.15f);
        BinaryPrimitives.WriteSingleLittleEndian(clientCast.AsSpan(28, 4), 165.53f);
        BinaryPrimitives.WriteSingleLittleEndian(clientCast.AsSpan(32, 4), 44.75f);
        BinaryPrimitives.WriteSingleLittleEndian(clientCast.AsSpan(36, 4), 166.25f);

        Check.True(SkillCastRequest.TryParse(clientCast, out var parsed), "client skill cast parses");
        Check.Equal(localObjectId, parsed.CasterObjectId, "client skill cast caster");
        Check.Equal(0u, parsed.SkillId, "client skill cast supports skill ID zero");
        Check.Equal(monsterId, parsed.TargetObjectId, "client skill cast target at absolute offset 16");
        Check.Equal(41.15f, parsed.CasterX, "client skill cast caster X");
        Check.Equal(165.53f, parsed.CasterZ, "client skill cast caster Z");
        Check.Equal(44.75f, parsed.TargetX, "client skill cast target X");
        Check.Equal(166.25f, parsed.TargetZ, "client skill cast target Z");
        Check.True(parsed.HasTargetPosition,
            "40-byte skill cast declares a target position");

        var shortDeclaredCast = clientCast.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(
            shortDeclaredCast.AsSpan(0, 2),
            20);
        Check.True(
            SkillCastRequest.TryParse(shortDeclaredCast, out var shortParsed),
            "short declared skill cast parses its declared fields");
        Check.True(!shortParsed.HasTargetPosition,
            "trailing bytes cannot inject an undeclared ground target");
        Check.True(float.IsNaN(shortParsed.TargetX) &&
                   float.IsNaN(shortParsed.TargetZ),
            "undeclared ground coordinates remain unavailable");

        var visual = PacketBuilder.SkillCastVisual(clientCast, remoteCasterId);
        Check.Equal(remoteCasterId, ReadUInt32(visual, 4), "cast visual patches only the caster identity");
        Check.Equal(monsterId, ReadUInt32(visual, 16), "cast visual preserves selected monster target");
        Check.Equal(10u, ReadUInt32(visual, 20), "cast visual advances captured cast state");

        var impact = PacketBuilder.SkillCastImpact(clientCast, remoteCasterId);
        Check.Equal(24, impact.Length, "skill impact length");
        Check.Equal((ushort)10046, ReadUInt16(impact, 2), "skill impact opcode");
        Check.Equal(remoteCasterId, ReadUInt32(impact, 4), "skill impact attacker");
        Check.Equal(monsterId, ReadUInt32(impact, 8), "skill impact target");
        Check.Equal(0u, ReadUInt32(impact, 12), "skill impact supports skill ID zero");
        Check.Equal(44.75f, ReadSingle(impact, 16), "skill impact target X");
        Check.Equal(166.25f, ReadSingle(impact, 20), "skill impact target Z");

        var damage = PacketBuilder.SkillDamage(
            remoteCasterId,
            monsterId,
            resultFlags: 1,
            damage: 865,
            skillId: 0,
            targetX: 44.75f,
            targetZ: 166.25f);
        Check.Equal(32, damage.Length, "skill damage length");
        Check.Equal((ushort)10045, ReadUInt16(damage, 2), "skill damage opcode");
        Check.Equal(remoteCasterId, ReadUInt32(damage, 4), "skill damage attacker");
        Check.Equal(monsterId, ReadUInt32(damage, 8), "skill damage target");
        Check.Equal(1u, ReadUInt32(damage, 12), "skill damage normal-hit result");
        Check.Equal(865u, ReadUInt32(damage, 16), "skill damage reports the uncapped resolved amount");
        Check.Equal(0u, ReadUInt32(damage, 20), "skill damage skill ID zero");
        Check.Equal(44.75f, ReadSingle(damage, 24), "skill damage target X");
        Check.Equal(166.25f, ReadSingle(damage, 28), "skill damage target Z");

        var capturedSingleHeal = Convert.FromHexString(
            "20003D2770030000FA0300000101000047F3FFFFF00200004C501A43D0E925C3");
        var singleHeal = PacketBuilder.SkillHealing(
            healerObjectId: 0x370,
            targetObjectId: 0x3FA,
            healing: 3_257,
            skillId: 752,
            targetX: ReadSingle(capturedSingleHeal, 24),
            targetZ: ReadSingle(capturedSingleHeal, 28));
        Check.True(
            singleHeal.SequenceEqual(capturedSingleHeal),
            "Priest single-target healing matches the original capture byte-for-byte");
        Check.Equal(0x101u, ReadUInt32(singleHeal, 12),
            "Priest healing uses the captured healing-result flags");
        Check.Equal(-3_257, ReadInt32(singleHeal, 16),
            "Priest healing encodes a signed negative combat-text amount");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = PacketBuilder.SkillHealing(
                remoteCasterId,
                localObjectId,
                healing: 0,
                skillId: 750,
                targetX: 0,
                targetZ: 0),
            "Priest healing rejects a zero combat-text amount");

        var areaCast = clientCast.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(areaCast.AsSpan(8, 4), 334);
        BinaryPrimitives.WriteUInt32LittleEndian(areaCast.AsSpan(16, 4), localObjectId);
        var areaVisual = PacketBuilder.SelfTargetSkillCastVisual(areaCast, remoteCasterId);
        Check.Equal(remoteCasterId, ReadUInt32(areaVisual, 4), "area cast visual patches caster identity");
        Check.Equal(remoteCasterId, ReadUInt32(areaVisual, 16), "area cast visual patches self-target identity");

        var groundAreaCast = clientCast.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            groundAreaCast.AsSpan(8, 4),
            564);
        BinaryPrimitives.WriteUInt32LittleEndian(
            groundAreaCast.AsSpan(16, 4),
            uint.MaxValue);
        var groundAreaVisual = PacketBuilder.SkillCastVisual(
            groundAreaCast,
            remoteCasterId);
        Check.Equal(uint.MaxValue, ReadUInt32(groundAreaVisual, 16),
            "ground area visual preserves the position-target sentinel");
        Check.Equal(44.75f, ReadSingle(groundAreaVisual, 32),
            "ground area visual preserves cursor X");
        Check.Equal(166.25f, ReadSingle(groundAreaVisual, 36),
            "ground area visual preserves cursor Z");

        var emptyCluster = PacketBuilder.SkillClusterDamage(
            localObjectId,
            334,
            Array.Empty<SkillClusterDamageEntry>());
        Check.Equal(17, emptyCluster.Length, "empty area damage packet length");
        Check.Equal((ushort)10047, ReadUInt16(emptyCluster, 2), "area damage opcode");
        Check.Equal(localObjectId, ReadUInt32(emptyCluster, 4), "area damage caster");
        Check.Equal(0u, ReadUInt32(emptyCluster, 8), "empty area damage count");
        Check.Equal(334u, ReadUInt32(emptyCluster, 12), "area damage skill");
        Check.Equal((byte)0, emptyCluster[16], "area damage aggregate status flag");

        var cluster = PacketBuilder.SkillClusterDamage(
            remoteCasterId,
            334,
            [
                new SkillClusterDamageEntry(monsterId, 2055),
                new SkillClusterDamageEntry(monsterId + 1, 1200)
            ]);
        Check.Equal(41, cluster.Length, "two-target area damage packet length");
        Check.Equal(2u, ReadUInt32(cluster, 8), "area damage hit count");
        Check.Equal(monsterId, ReadUInt32(cluster, 17), "first area damage target");
        Check.Equal((byte)1, cluster[21], "first area damage hit result");
        Check.Equal((byte)0, cluster[22], "first area damage affects HP");
        Check.Equal((byte)0, cluster[23], "first area damage alignment byte one");
        Check.Equal((byte)0, cluster[24], "first area damage alignment byte two");
        Check.Equal(2055u, ReadUInt32(cluster, 25), "first area damage amount");
        Check.Equal(monsterId + 1, ReadUInt32(cluster, 29), "second area damage target");
        Check.Equal(1200u, ReadUInt32(cluster, 37), "second area damage amount");

        var capturedMeteorBlastCluster = Convert.FromHexString(
            "1D003F276B020000010000004D01000000A42800000100000001000000");
        var reproducedMeteorBlastCluster = PacketBuilder.SkillClusterDamage(
            0x26B,
            333,
            [new SkillClusterDamageEntry(0x28A4, 1)]);
        Check.True(
            reproducedMeteorBlastCluster.SequenceEqual(capturedMeteorBlastCluster),
            "Meteor Blast area damage matches the original capture byte-for-byte");

        var capturedAreaHeal = Convert.FromHexString(
            "7D003F277003000009000000F902000000" +
            "14000000010000004AFCFFFF" +
            "F403000001000000E6FAFFFF" +
            "700300000100000069FBFFFF" +
            "F40400000100000098FBFFFF" +
            "9704000001000000E6FAFFFF" +
            "FA0300000100000006FCFFFF" +
            "3005000001000000E6FAFFFF" +
            "13010000010000004AFCFFFF" +
            "9F02000001000000EBFBFFFF");
        var areaHeal = PacketBuilder.SkillClusterHealing(
            healerObjectId: 0x370,
            skillId: 761,
            [
                new SkillClusterHealingEntry(20, 950),
                new SkillClusterHealingEntry(1012, 1_306),
                new SkillClusterHealingEntry(880, 1_175),
                new SkillClusterHealingEntry(1268, 1_128),
                new SkillClusterHealingEntry(1175, 1_306),
                new SkillClusterHealingEntry(1018, 1_018),
                new SkillClusterHealingEntry(1328, 1_306),
                new SkillClusterHealingEntry(275, 950),
                new SkillClusterHealingEntry(671, 1_045)
            ]);
        Check.True(
            areaHeal.SequenceEqual(capturedAreaHeal),
            "Priest area healing matches the original capture byte-for-byte");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = PacketBuilder.SkillClusterHealing(
                remoteCasterId,
                skillId: 760,
                [new SkillClusterHealingEntry(localObjectId, 0)]),
            "Priest area healing rejects a zero combat-text amount");

        var mana = PacketBuilder.PlayerManaUpdate(remoteCasterId, 165);
        Check.Equal(12, mana.Length, "mana update length");
        Check.Equal((ushort)10135, ReadUInt16(mana, 2), "mana update opcode");
        Check.Equal(remoteCasterId, ReadUInt32(mana, 4), "mana update caster");
        Check.Equal(165u, ReadUInt32(mana, 8), "mana update absolute current MP");
        return Task.CompletedTask;
    }

    private static Task CheckAttackPacketLayoutsAsync()
    {
        var clientAttack = Convert.FromHexString(
            "20002A279F0400009AC83043000000007B4731401D270000AED27D007F007F00");
        Check.True(BasicAttackRequest.TryParse(clientAttack, out var parsed), "captured basic attack parses");
        Check.Equal(0x49Fu, parsed.AttackerObjectId, "basic attack attacker");
        Check.Equal(ReadSingle(clientAttack, 8), parsed.AttackerX, "basic attack X");
        Check.Equal(ReadSingle(clientAttack, 12), parsed.AttackerY, "basic attack Y");
        Check.Equal(ReadSingle(clientAttack, 16), parsed.AttackerZ, "basic attack Z");
        Check.Equal(10013u, parsed.TargetObjectId, "basic attack target");
        CheckBasicAttackRequestFraming(clientAttack, parsed);

        var freeRevive = Convert.FromHexString("0C0023274814000002000000");
        Check.True(ReviveRequest.TryParse(freeRevive, out var revive), "original free-revive request parses");
        Check.Equal(0x1448u, revive.PlayerObjectId, "revive request player object");
        Check.Equal(2, revive.ReviveType, "revive request free-revival type");

        var capturedPlayerDamage = Convert.FromHexString(
            "1E002A279F0400000000000000000000000000001D270000370000000301");
        var playerDamage = PacketBuilder.PhysicalDamage(
            0x49F,
            0f,
            0f,
            0f,
            10013,
            55,
            result: 3);
        Check.True(playerDamage.SequenceEqual(capturedPlayerDamage), "player normal damage matches capture byte-for-byte");

        var capturedMonsterImpact = Convert.FromHexString(
            "18003E271D2700009F040000D007000078BD3043873C2C40");
        var monsterImpact = PacketBuilder.SkillCastImpact(
            10013,
            0x49F,
            2000,
            ReadSingle(capturedMonsterImpact, 16),
            ReadSingle(capturedMonsterImpact, 20));
        Check.True(monsterImpact.SequenceEqual(capturedMonsterImpact), "monster attack impact matches capture byte-for-byte");

        var capturedMonsterDamage = Convert.FromHexString(
            "1E002A271D270000F227324300000000A5064F409F040000180000000001");
        var monsterDamage = PacketBuilder.PhysicalDamage(
            10013,
            ReadSingle(capturedMonsterDamage, 8),
            ReadSingle(capturedMonsterDamage, 12),
            ReadSingle(capturedMonsterDamage, 16),
            0x49F,
            24,
            result: 0);
        Check.True(monsterDamage.SequenceEqual(capturedMonsterDamage), "monster physical damage matches capture byte-for-byte");

        var capturedDeath = Convert.FromHexString(
            "1C0022274F0200000000164300000000000012C30000000001000000");
        var death = PacketBuilder.PlayerDeath(
            0x24F,
            ReadSingle(capturedDeath, 8),
            ReadSingle(capturedDeath, 12),
            ReadSingle(capturedDeath, 16),
            0);
        Check.True(death.SequenceEqual(capturedDeath), "player death matches capture byte-for-byte");

        var firstExperience = PacketBuilder.ExperienceGain(80, 80);
        Check.True(
            firstExperience.SequenceEqual(Convert.FromHexString("0D002F27500000005000000000")),
            "first-kill EXP notice matches capture byte-for-byte");
        var laterExperience = PacketBuilder.ExperienceGain(80, 160);
        Check.Equal(80, ReadInt32(laterExperience, 4), "EXP notice displays gained delta at +4");
        Check.Equal(160, ReadInt32(laterExperience, 8), "EXP notice carries resulting total at +8");
        Check.True(
            PacketBuilder.TalentExperienceGain(2).SequenceEqual(
                Convert.FromHexString("0C0045280400000002000000")),
            "Talent EXP notice matches capture byte-for-byte");
        Check.True(
            PacketBuilder.PlayerLevelUp(
                0x466,
                2,
                252,
                0,
                1351,
                1331,
                386,
                380).SequenceEqual(
                Convert.FromHexString(
                    "24002E276604000002000000FC000000000000004705000033050000820100007C010000")),
            "fighter level-up notice matches capture byte-for-byte");
        Check.True(
            PacketBuilder.MonsterDeathReward(10013, 0x49F, 80, 2, 0).SequenceEqual(
                Convert.FromHexString(
                    "74002B271D2700009F040000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF00000000000000000000000000000000000000005000000000000000000000000000000000000000020000000000000000000000000000000000000000000000000000000000000000000000000000001D27000000000000")),
            "monster-death progression refresh matches capture byte-for-byte");

        var physical = CreateCharacter();
        physical.Profession = 0;
        physical.CalculatedStats = new CharacterStats { PhysicalAttack = 55, MagicAttack = 99 };
        Check.Equal(55u, MonsterCombatResolver.CalculatePlayerBasicAttack(physical), "physical class basic damage");
        physical.Profession = 3;
        Check.Equal(99u, MonsterCombatResolver.CalculatePlayerBasicAttack(physical), "caster class basic damage");
        Check.True(
            MonsterCombatResolver.IsWithinBasicAttackRange(0, 0, 2.49f, 0),
            "normal attack accepts a target inside 2.5 units");
        Check.True(
            MonsterCombatResolver.IsWithinBasicAttackRange(0, 0, 2.5f, 0),
            "normal attack accepts the exact 2.5-unit collision boundary");
        Check.True(
            MonsterCombatResolver.TryResolvePlayerBasicAttackPosition(
                153.39f,
                142.62f,
                153.2126f,
                142.6414f,
                out var resolvedAttackX,
                out var resolvedAttackZ),
            "normal attack accepts the captured final auto-approach position");
        Check.Equal(153.2126f, resolvedAttackX, "normal attack uses reported auto-approach X");
        Check.Equal(142.6414f, resolvedAttackZ, "normal attack uses reported auto-approach Z");
        Check.True(
            MonsterCombatResolver.IsWithinBasicAttackRange(
                resolvedAttackX,
                resolvedAttackZ,
                150.8749f,
                142.9226f),
            "warrior auto-approach position reaches the live snake target");
        Check.True(
            !MonsterCombatResolver.TryResolvePlayerBasicAttackPosition(
                153.39f,
                142.62f,
                149f,
                142.62f,
                out _,
                out _),
            "normal attack rejects an implausible reported-position correction");

        var undefended = CreateCharacter();
        undefended.CalculatedStats = new CharacterStats { PhysicalDefense = 0 };
        Check.Equal(24u, MonsterCombatResolver.CalculateMonsterPhysicalAttack(1, undefended), "tier-one monster attack");
        Check.Equal(27u, MonsterCombatResolver.CalculateMonsterPhysicalAttack(2, undefended), "tier-two monster attack");
        Check.Equal(31u, MonsterCombatResolver.CalculateMonsterPhysicalAttack(3, undefended), "tier-three monster attack");
        undefended.CalculatedStats = new CharacterStats { PhysicalDefense = 22 };
        Check.Equal(2u, MonsterCombatResolver.CalculateMonsterPhysicalAttack(1, undefended), "physical defense reduces monster damage");
        undefended.CalculatedStats = new CharacterStats { PhysicalDefense = 999 };
        Check.Equal(1u, MonsterCombatResolver.CalculateMonsterPhysicalAttack(1, undefended), "monster damage floors at one");
        return Task.CompletedTask;
    }
}
