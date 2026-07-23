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
    private static Task CheckSkillCombatCatalogAsync()
    {
        Check.True(SkillCombatCatalog.TryGet(0, out var lightChop), "Light Chop combat data exists");
        Check.Equal(44, lightChop.Target, "Light Chop target mode");
        Check.Equal(28, lightChop.AffectObj, "Light Chop affected-object mode");
        Check.Equal(3f, lightChop.Distance, "Light Chop distance");
        Check.Equal(0f, lightChop.Range, "Light Chop single-target range");
        Check.Equal(0, lightChop.Property, "Light Chop uses physical attack");
        Check.Equal(12, lightChop.Mp, "Light Chop mana cost");
        Check.Equal(-0.5m, lightChop.Power1, "Light Chop physical attack multiplier");
        Check.Equal(250m, lightChop.Power2, "Light Chop flat damage");

        var warrior = CreateCharacter();
        warrior.CalculatedStats = new CharacterStats { PhysicalAttack = 40 };
        Check.True(SkillCombatResolver.IsHostileMonsterSkill(lightChop), "Light Chop can target a hostile monster");
        Check.Equal(270u, SkillCombatResolver.CalculateDamage(warrior, lightChop), "Light Chop damage formula");
        Check.True(
            SkillCombatResolver.IsWithinRange(41.15f, 165.53f, 40.8691f, 162.7964f, lightChop),
            "captured account-13 cast is within Light Chop range");
        Check.True(
            !SkillCombatResolver.IsWithinRange(41.15f, 165.53f, 60f, 180f, lightChop),
            "distant monster cast is rejected");

        Check.True(SkillCombatCatalog.TryGet(334, out var meteorBlast), "Meteor Blast 5 combat data exists");
        Check.Equal(1, meteorBlast.Target, "Meteor Blast targets the caster");
        Check.Equal(28, meteorBlast.AffectObj, "Meteor Blast affected-object mode");
        Check.Equal(0f, meteorBlast.Distance, "Meteor Blast has no selected-target distance");
        Check.Equal(10f, meteorBlast.Range, "Meteor Blast area radius");
        Check.Equal(0, meteorBlast.Property, "Meteor Blast uses physical attack");
        Check.Equal(900, meteorBlast.Mp, "Meteor Blast mana cost");
        Check.Equal(0.88m, meteorBlast.Power1, "Meteor Blast physical attack multiplier");
        Check.Equal(1980m, meteorBlast.Power2, "Meteor Blast flat damage");
        Check.True(
            SkillCombatResolver.IsHostileMonsterAreaSkill(meteorBlast),
            "Meteor Blast is admitted as a hostile self-centred area skill");
        foreach (var championAreaSkillId in new[] { 304, 314, 324, 334 })
        {
            Check.True(
                SkillCombatCatalog.TryGet(championAreaSkillId, out var championAreaSkill) &&
                SkillCombatResolver.IsHostileMonsterAreaSkill(championAreaSkill),
                $"Champion area skill {championAreaSkillId} uses the shared AOE path");
        }

        Check.Equal(
            2055u,
            SkillCombatResolver.CalculateDamage(warrior, meteorBlast),
            "Meteor Blast damage formula");
        Check.True(
            SkillCombatResolver.IsWithinArea(10f, 10f, 19.99f, 10f, meteorBlast),
            "Meteor Blast includes monsters strictly inside its area");
        Check.True(
            !SkillCombatResolver.IsWithinArea(10f, 10f, 20f, 10f, meteorBlast),
            "Meteor Blast excludes monsters on its strict area boundary");
        return Task.CompletedTask;
    }

    private static Task CheckSacredZealStatusCompositionAsync()
    {
        var expected = new[]
        {
            (SkillId: 340, StatusId: 200u, Priority: 1, Mp: 50, Hit: 10, Critical: 4),
            (SkillId: 341, StatusId: 201u, Priority: 2, Mp: 90, Hit: 20, Critical: 8),
            (SkillId: 342, StatusId: 202u, Priority: 3, Mp: 130, Hit: 30, Critical: 12),
            (SkillId: 343, StatusId: 203u, Priority: 4, Mp: 200, Hit: 45, Critical: 18),
            (SkillId: 344, StatusId: 204u, Priority: 5, Mp: 300, Hit: 60, Critical: 24)
        };
        foreach (var item in expected)
        {
            Check.True(
                SkillStatusEffectCatalog.TryGet(item.SkillId, out var definition),
                $"Sacred Zeal {item.SkillId} status definition exists");
            Check.Equal(item.StatusId, definition.StatusId, $"Sacred Zeal {item.SkillId} status ID");
            Check.Equal(7, definition.Kind, $"Sacred Zeal {item.SkillId} status kind");
            Check.Equal(item.Priority, definition.Priority, $"Sacred Zeal {item.SkillId} priority");
            Check.True(definition.Beneficial, $"Sacred Zeal {item.SkillId} is beneficial");
            Check.Equal(TimeSpan.FromSeconds(600), definition.Duration, $"Sacred Zeal {item.SkillId} duration");
            Check.Equal(TimeSpan.FromSeconds(10), definition.Cooldown, $"Sacred Zeal {item.SkillId} cooldown");
            Check.Equal(item.Hit, definition.HitBonus, $"Sacred Zeal {item.SkillId} Hit bonus");
            Check.Equal(item.Critical, definition.CriticalAppendBonus, $"Sacred Zeal {item.SkillId} Critical bonus");
            Check.True(
                SkillCombatCatalog.TryGet(item.SkillId, out var combat),
                $"Sacred Zeal {item.SkillId} combat definition exists");
            Check.Equal(item.Mp, combat.Mp, $"Sacred Zeal {item.SkillId} MP cost");
            Check.Equal(1, combat.Target, $"Sacred Zeal {item.SkillId} targets self");
            Check.Equal(1, combat.AffectObj, $"Sacred Zeal {item.SkillId} affects self");
        }

        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var boosts = new ExperienceBoostState(
        [
            new ActiveExperienceBoost(
                ExperienceStatusIds.Weekend,
                ExperienceBoostKinds.Weekend,
                20_000,
                1,
                now.AddHours(8),
                "weekend"),
            new ActiveExperienceBoost(
                ExperienceStatusIds.VipPlatinum,
                ExperienceBoostKinds.Vip,
                2_000,
                4,
                null,
                "vip:platinum")
        ]);
        var runtime = new ActiveRuntimeStatus(
            204,
            7,
            5,
            true,
            now.AddSeconds(600),
            new ClientStatusAggregate(60, 24, 0f),
            1);
        var snapshot = PlayerStatusComposer.Compose(boosts, [runtime], now);

        Check.Equal(3, snapshot.Effects.Count, "EXP and Sacred Zeal status count");
        Check.Equal(204u, snapshot.Effects[0].StatusId, "Sacred Zeal remains in sorted full snapshot");
        Check.Equal(600u, snapshot.Effects[0].RemainingSeconds, "Sacred Zeal timer starts at 600 seconds");
        Check.Equal(511u, snapshot.Effects[1].StatusId, "weekend EXP status is preserved");
        Check.Equal(1503u, snapshot.Effects[2].StatusId, "VIP EXP status is preserved");
        Check.Equal(60, snapshot.Aggregate.Hit, "Sacred Zeal aggregate Hit bonus");
        Check.Equal(24, snapshot.Aggregate.CriticalAppend, "Sacred Zeal aggregate Critical bonus");
        Check.Equal(2.2f, snapshot.Aggregate.ExperienceBonus, "EXP aggregate is preserved");

        var character = CreateCharacter();
        var packet = PacketBuilder.PlayerStatusEffects(
            character,
            snapshot.Effects,
            snapshot.Aggregate);
        Check.Equal(204u, ReadUInt32(packet, 12), "Sacred Zeal status packet ID");
        Check.Equal(600u, ReadUInt32(packet, 92), "Sacred Zeal status packet timer");
        Check.Equal(
            character.CalculatedStats!.Hit + 60,
            ReadInt32(packet, 204),
            "StatusData includes base and Sacred Zeal Hit");
        Check.Equal(
            character.CalculatedStats.Critical + 24,
            ReadInt32(packet, 212),
            "StatusData includes base and Sacred Zeal Critical");
        Check.Equal(2.2f, ReadSingle(packet, 300), "StatusData EXP wire offset");

        var oneSecondLater = PlayerStatusComposer.Compose(boosts, [runtime], now.AddSeconds(1));
        Check.Equal(
            snapshot.Fingerprint,
            oneSecondLater.Fingerprint,
            "status fingerprint excludes the changing countdown");
        Check.Equal(599u, oneSecondLater.Effects[0].RemainingSeconds, "status countdown still updates when republished");

        var expired = PlayerStatusComposer.Compose(boosts, [runtime], now.AddSeconds(601));
        Check.Equal(2, expired.Effects.Count, "Sacred Zeal expires without removing EXP statuses");
        Check.Equal(0, expired.Aggregate.Hit, "expired Sacred Zeal removes aggregate Hit");
        Check.Equal(0, expired.Aggregate.CriticalAppend, "expired Sacred Zeal removes aggregate Critical");
        Check.Equal(2.2f, expired.Aggregate.ExperienceBonus, "expired Sacred Zeal preserves aggregate EXP");

        return Task.CompletedTask;
    }

    private static Task CheckHolyWardStatusCompositionAsync()
    {
        var expected = new[]
        {
            (SkillId: 90, StatusId: 160u, Priority: 2, Mp: 35, Physical: 0.10m, Magical: 0m),
            (SkillId: 91, StatusId: 161u, Priority: 3, Mp: 45, Physical: 0.13m, Magical: 0m),
            (SkillId: 92, StatusId: 162u, Priority: 4, Mp: 60, Physical: 0.16m, Magical: 0.05m),
            (SkillId: 93, StatusId: 163u, Priority: 5, Mp: 90, Physical: 0.20m, Magical: 0.10m),
            (SkillId: 94, StatusId: 164u, Priority: 6, Mp: 120, Physical: 0.25m, Magical: 0.15m)
        };
        foreach (var item in expected)
        {
            Check.True(
                SkillStatusEffectCatalog.TryGet(item.SkillId, out var definition),
                $"Holy Ward {item.SkillId} status definition exists");
            Check.Equal(item.StatusId, definition.StatusId, $"Holy Ward {item.SkillId} status ID");
            Check.Equal(6, definition.Kind, $"Holy Ward {item.SkillId} status kind");
            Check.Equal(item.Priority, definition.Priority, $"Holy Ward {item.SkillId} priority");
            Check.True(definition.Beneficial, $"Holy Ward {item.SkillId} is beneficial");
            Check.Equal(TimeSpan.FromSeconds(600), definition.Duration, $"Holy Ward {item.SkillId} duration");
            Check.Equal(TimeSpan.FromSeconds(10), definition.Cooldown, $"Holy Ward {item.SkillId} cooldown");
            Check.Equal(item.Physical, definition.PhysicalDamageReduction, $"Holy Ward {item.SkillId} physical mitigation");
            Check.Equal(item.Magical, definition.MagicDamageReduction, $"Holy Ward {item.SkillId} magical mitigation");
            Check.True(
                SkillCombatCatalog.TryGet(item.SkillId, out var combat),
                $"Holy Ward {item.SkillId} combat definition exists");
            Check.Equal(item.Mp, combat.Mp, $"Holy Ward {item.SkillId} MP cost");
            Check.Equal(1, combat.Target, $"Holy Ward {item.SkillId} targets self");
            Check.Equal(1, combat.AffectObj, $"Holy Ward {item.SkillId} affects self");
        }

        var now = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);
        var holyWard = new ActiveRuntimeStatus(
            160,
            6,
            2,
            true,
            now.AddSeconds(600),
            ClientStatusAggregate.Empty,
            1,
            PhysicalDamageReduction: 0.10m);
        var snapshot = PlayerStatusComposer.Compose(
            ExperienceBoostState.Empty,
            [holyWard],
            now);
        Check.Equal(1, snapshot.Effects.Count, "Holy Ward publishes one status icon");
        Check.Equal(160u, snapshot.Effects[0].StatusId, "Holy Ward publishes Apollo's Shield status ID");
        Check.Equal(600u, snapshot.Effects[0].RemainingSeconds, "Holy Ward publishes the ten-minute timer");
        var strongerSnapshot = PlayerStatusComposer.Compose(
            ExperienceBoostState.Empty,
            [holyWard with { PhysicalDamageReduction = 0.20m }],
            now);
        Check.True(
            !string.Equals(snapshot.Fingerprint, strongerSnapshot.Fingerprint, StringComparison.Ordinal),
            "Holy Ward mitigation participates in the full-status fingerprint");

        var character = CreateCharacter();
        character.CalculatedStats = new CharacterStats { PhysicalDefense = 0 };
        var packet = PacketBuilder.PlayerStatusEffects(
            character,
            snapshot.Effects,
            snapshot.Aggregate);
        Check.Equal(160u, ReadUInt32(packet, 12), "Holy Ward status packet carries its icon ID");
        Check.Equal(600u, ReadUInt32(packet, 92), "Holy Ward status packet carries its timer");
        Check.Equal(
            21u,
            MonsterCombatResolver.CalculateMonsterPhysicalAttack(
                tier: 1,
                character,
                holyWard.PhysicalDamageReduction),
            "Holy Ward 1 reduces a captured 24-damage monster hit by ten percent with native truncation");
        Check.Equal(
            18u,
            MonsterCombatResolver.CalculateMonsterPhysicalAttack(
                tier: 1,
                character,
                receivedDamageReduction: 0.25m),
            "Holy Ward 5 reduces a captured monster hit by twenty-five percent");

        return Task.CompletedTask;
    }
}
