using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static Task CheckPriestDefensiveStatusCompositionAsync()
    {
        var gaiaCare = new[]
        {
            (SkillId: 770, StatusId: 270u, Priority: 1, Mp: 100,
                Duration: 5, Cooldown: 150),
            (SkillId: 771, StatusId: 271u, Priority: 2, Mp: 150,
                Duration: 5, Cooldown: 120),
            (SkillId: 772, StatusId: 272u, Priority: 3, Mp: 220,
                Duration: 7, Cooldown: 120),
            (SkillId: 773, StatusId: 273u, Priority: 4, Mp: 300,
                Duration: 8, Cooldown: 90),
            (SkillId: 774, StatusId: 274u, Priority: 5, Mp: 400,
                Duration: 10, Cooldown: 90)
        };
        foreach (var item in gaiaCare)
        {
            Check.True(
                SkillStatusEffectCatalog.TryGet(
                    item.SkillId,
                    out var definition),
                $"Gaia Care {item.SkillId} status definition exists");
            Check.Equal(item.StatusId, definition.StatusId,
                $"Gaia Care {item.SkillId} status ID");
            Check.Equal(34, definition.Kind,
                $"Gaia Care {item.SkillId} status kind");
            Check.Equal(item.Priority, definition.Priority,
                $"Gaia Care {item.SkillId} priority");
            Check.Equal(TimeSpan.FromSeconds(item.Duration),
                definition.Duration,
                $"Gaia Care {item.SkillId} duration");
            Check.Equal(TimeSpan.FromSeconds(item.Cooldown),
                definition.Cooldown,
                $"Gaia Care {item.SkillId} cooldown");
            Check.Equal(3_000, definition.DodgeBonus,
                $"Gaia Care {item.SkillId} Dodge bonus");
            Check.Equal(1_000, definition.CriticalResistanceBonus,
                $"Gaia Care {item.SkillId} Critical Resistance bonus");
            Check.True(
                GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(
                    item.SkillId,
                    out var combat),
                $"Gaia Care {item.SkillId} combat definition exists");
            Check.Equal(item.Mp, combat.Mp,
                $"Gaia Care {item.SkillId} MP cost");
            Check.Equal(1, combat.Target,
                $"Gaia Care {item.SkillId} targets caster");
            Check.Equal(1, combat.AffectObj,
                $"Gaia Care {item.SkillId} affects only caster");
            Check.Equal(0f, combat.Range,
                $"Gaia Care {item.SkillId} has no area radius");
        }

        var manaShield = new[]
        {
            (SkillId: 780, StatusId: 260u, Priority: 1, Mp: 100,
                Physical: 20, Magical: 15),
            (SkillId: 781, StatusId: 261u, Priority: 2, Mp: 150,
                Physical: 40, Magical: 30),
            (SkillId: 782, StatusId: 262u, Priority: 3, Mp: 210,
                Physical: 100, Magical: 80),
            (SkillId: 783, StatusId: 263u, Priority: 4, Mp: 300,
                Physical: 180, Magical: 140),
            (SkillId: 784, StatusId: 264u, Priority: 5, Mp: 450,
                Physical: 280, Magical: 200)
        };
        foreach (var item in manaShield)
        {
            Check.True(
                SkillStatusEffectCatalog.TryGet(
                    item.SkillId,
                    out var definition),
                $"Mana Shield {item.SkillId} status definition exists");
            Check.Equal(item.StatusId, definition.StatusId,
                $"Mana Shield {item.SkillId} status ID");
            Check.Equal(9, definition.Kind,
                $"Mana Shield {item.SkillId} status kind");
            Check.Equal(item.Priority, definition.Priority,
                $"Mana Shield {item.SkillId} priority");
            Check.Equal(TimeSpan.FromSeconds(600), definition.Duration,
                $"Mana Shield {item.SkillId} duration");
            Check.Equal(TimeSpan.FromSeconds(10), definition.Cooldown,
                $"Mana Shield {item.SkillId} cooldown");
            Check.Equal(item.Physical, definition.PhysicalDefenseBonus,
                $"Mana Shield {item.SkillId} Physical Defense bonus");
            Check.Equal(item.Magical, definition.MagicDefenseBonus,
                $"Mana Shield {item.SkillId} Magic Defense bonus");
            Check.True(
                GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(
                    item.SkillId,
                    out var combat),
                $"Mana Shield {item.SkillId} combat definition exists");
            Check.Equal(item.Mp, combat.Mp,
                $"Mana Shield {item.SkillId} MP cost");
            Check.Equal(1, combat.Target,
                $"Mana Shield {item.SkillId} is caster-centred");
            Check.Equal(3, combat.AffectObj,
                $"Mana Shield {item.SkillId} uses friendly-player mask");
            Check.Equal(10f, combat.Range,
                $"Mana Shield {item.SkillId} friendly radius");
        }

        CheckPriestDefensiveStatusPacketsAndExpiry();
        return Task.CompletedTask;
    }

    private static void CheckPriestDefensiveStatusPacketsAndExpiry()
    {
        var now = new DateTimeOffset(
            2026,
            8,
            9,
            12,
            0,
            0,
            TimeSpan.Zero);
        var mana = new ActiveRuntimeStatus(
            264,
            9,
            5,
            true,
            now.AddSeconds(600),
            new ClientStatusAggregate(
                0,
                0,
                0f,
                PhysicalDefense: 280,
                MagicDefense: 200),
            1);
        var gaia = new ActiveRuntimeStatus(
            274,
            34,
            5,
            true,
            now.AddSeconds(10),
            new ClientStatusAggregate(
                0,
                0,
                0f,
                Dodge: 3_000,
                CriticalResistance: 1_000),
            2);
        var snapshot = PlayerStatusComposer.Compose(
            ExperienceBoostState.Empty,
            [mana, gaia],
            now);
        Check.Equal(280, snapshot.Aggregate.PhysicalDefense,
            "Mana Shield aggregate Physical Defense");
        Check.Equal(200, snapshot.Aggregate.MagicDefense,
            "Mana Shield aggregate Magic Defense");
        Check.Equal(3_000, snapshot.Aggregate.Dodge,
            "Gaia Care aggregate Dodge");
        Check.Equal(1_000, snapshot.Aggregate.CriticalResistance,
            "Gaia Care aggregate Critical Resistance");

        var character = CreateCharacter();
        character.CalculatedStats = new CharacterStats
        {
            PhysicalDefense = 50,
            MagicDefense = 70,
            Dodge = 389,
            CriticalResistance = 0
        };
        var statusPacket = PacketBuilder.PlayerStatusEffects(
            character,
            snapshot.Effects,
            snapshot.Aggregate);
        Check.Equal(330, ReadInt32(statusPacket, 192),
            "Mana Shield updates full-status Physical Defense");
        Check.Equal(270, ReadInt32(statusPacket, 200),
            "Mana Shield updates full-status Magic Defense");
        Check.Equal(3_389, ReadInt32(statusPacket, 208),
            "Gaia Care matches captured Dodge");
        Check.Equal(1_000, ReadInt32(statusPacket, 216),
            "Gaia Care matches captured Critical Resistance");

        var gameDataPacket = PacketBuilder.PlayerStatusUpdate(
            character,
            snapshot.Aggregate);
        Check.Equal(330, ReadInt32(gameDataPacket, 164),
            "ordinary game-data refresh preserves Mana Physical Defense");
        Check.Equal(270, ReadInt32(gameDataPacket, 172),
            "ordinary game-data refresh preserves Mana Magic Defense");
        Check.Equal(3_389, ReadInt32(gameDataPacket, 180),
            "ordinary game-data refresh preserves Gaia Dodge");
        Check.Equal(1_000, ReadInt32(gameDataPacket, 188),
            "ordinary game-data refresh preserves Gaia Crit Resistance");

        var afterGaia = PlayerStatusComposer.Compose(
            ExperienceBoostState.Empty,
            [mana, gaia],
            now.AddSeconds(11));
        Check.Equal(1, afterGaia.Effects.Count,
            "Gaia Care expires without removing Mana Shield");
        Check.Equal(0, afterGaia.Aggregate.Dodge,
            "expired Gaia Care removes Dodge bonus");
        Check.Equal(280, afterGaia.Aggregate.PhysicalDefense,
            "Mana Shield remains after Gaia Care expires");
        Check.Equal(
            17u,
            MonsterCombatResolver.CalculateMonsterPhysicalAttack(
                tier: 20,
                character,
                physicalDefenseBonus: 20),
            "Mana Shield Physical Defense reduces authoritative monster damage");
    }
}
