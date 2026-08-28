using System.Buffers.Binary;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaMonsterContentChecks
{
    public const string CheckName =
        "Medusa database monster, scoring, loot, and corpse content";

    public static Task RunAsync()
    {
        CheckMonsterRules();
        CheckExternalScoring();
        CheckLootRollsAndPackets();
        CheckMalformedContentFailsClosed();
        return Task.CompletedTask;
    }

    private static void CheckMonsterRules()
    {
        var content = MedusaMonsterContentCatalog.Current;
        Check.Equal(
            Enum.GetValues<MedusaEncounterDifficulty>().Length *
                MedusaIslandRosterPolicy.Templates.Length,
            content.Monsters.Count,
            "every difficulty/template has exactly one editable rule");

        Check.True(
            Rule(MedusaEncounterDifficulty.Enhanced,
                MedusaIslandRosterTemplateAliases.MudCrocodile).Level == 95 &&
            Rule(MedusaEncounterDifficulty.Enhanced,
                MedusaIslandRosterTemplateAliases.EliteGorgonDemon).Level ==
                100 &&
            Rule(MedusaEncounterDifficulty.Enhanced,
                MedusaIslandRosterTemplateAliases.Euryale).Level == 130 &&
            Rule(MedusaEncounterDifficulty.Enhanced,
                MedusaIslandRosterTemplateAliases.Stheno).Level == 200 &&
            Rule(MedusaEncounterDifficulty.Enhanced,
                MedusaIslandRosterTemplateAliases.Medusa).Level == 200,
            "appearance tiers follow the captured external monster levels");

        foreach (var template in MedusaIslandRosterPolicy.Templates)
        {
            var expectedNormalHealth = ExpectedNormalHealth(template.Alias);
            var normal = Rule(
                MedusaEncounterDifficulty.Normal,
                template.Alias);
            var enhanced = Rule(
                MedusaEncounterDifficulty.Enhanced,
                template.Alias);
            var mythic = Rule(
                MedusaEncounterDifficulty.Mythic,
                template.Alias);
            Check.True(
                normal.MaximumHealth == expectedNormalHealth &&
                enhanced.MaximumHealth == expectedNormalHealth * 2 &&
                mythic.MaximumHealth == expectedNormalHealth * 5 &&
                normal.Level == enhanced.Level &&
                enhanced.Level == mythic.Level &&
                normal.Score == enhanced.Score &&
                enhanced.Score == mythic.Score,
                $"{template.Alias} uses captured Normal HP and exact difficulty scaling");

            var isFinalBoss = template.Alias is
                MedusaIslandRosterTemplateAliases.Stheno or
                MedusaIslandRosterTemplateAliases.Medusa;
            Check.True(
                isFinalBoss
                    ? enhanced.CorpseWithoutLootMilliseconds is null &&
                      enhanced.CorpseWithLootMilliseconds is null
                    : enhanced.CorpseWithoutLootMilliseconds == 4_200 &&
                      enhanced.CorpseWithLootMilliseconds == 20_000,
                $"{template.Alias} uses the captured corpse lifetime");
        }

        Check.True(
            Rule(MedusaEncounterDifficulty.Enhanced,
                MedusaIslandRosterTemplateAliases.Euryale)
                .MovementSpeedBasisPoints == 7_368 &&
            Rule(MedusaEncounterDifficulty.Enhanced,
                MedusaIslandRosterTemplateAliases.Stheno)
                .MovementSpeedBasisPoints == 5_000 &&
            Rule(MedusaEncounterDifficulty.Enhanced,
                MedusaIslandRosterTemplateAliases.Medusa)
                .MovementSpeedBasisPoints == 5_000 &&
            Rule(MedusaEncounterDifficulty.Enhanced,
                MedusaIslandRosterTemplateAliases.Chrysaor)
                .MovementSpeedBasisPoints == 10_000,
            "boss movement cadence follows the external capture");

    }

    private static uint ExpectedNormalHealth(string alias) => alias switch
    {
        MedusaIslandRosterTemplateAliases.Stheno => 3_000_000,
        MedusaIslandRosterTemplateAliases.Euryale => 5_000_000,
        MedusaIslandRosterTemplateAliases.Chrysaor => 2_000_000,
        MedusaIslandRosterTemplateAliases.Medusa => 3_500_000,
        MedusaIslandRosterTemplateAliases.EliteArcher or
            MedusaIslandRosterTemplateAliases.EliteCrazyAxemanA or
            MedusaIslandRosterTemplateAliases.EliteShamanSix or
            MedusaIslandRosterTemplateAliases.EliteShamanEight => 2_500_000,
        MedusaIslandRosterTemplateAliases.EliteCrazyAxemanC or
            MedusaIslandRosterTemplateAliases.EliteGuardianB or
            MedusaIslandRosterTemplateAliases.ElitePriestB12 or
            MedusaIslandRosterTemplateAliases.EliteShamanC9 or
            MedusaIslandRosterTemplateAliases.EliteShamanC8 => 800_000,
        MedusaIslandRosterTemplateAliases.EliteJungleWizardB or
            MedusaIslandRosterTemplateAliases.EliteGorgonPriestC14 => 500_000,
        MedusaIslandRosterTemplateAliases.EliteGorgonWizard or
            MedusaIslandRosterTemplateAliases.EliteCyclopsSwordsman =>
                8_000_000,
        MedusaIslandRosterTemplateAliases.ElitePriestA12 => 250_000,
        _ when alias.StartsWith("elite-", StringComparison.Ordinal) =>
            1_500_000,
        _ when alias.StartsWith("normal-", StringComparison.Ordinal) =>
            800_000,
        _ => throw new ArgumentOutOfRangeException(nameof(alias), alias, null)
    };

    private static void CheckExternalScoring()
    {
        var scores = MedusaIslandRosterPolicy.Spawns.Select(spawn =>
            Rule(
                MedusaEncounterDifficulty.Enhanced,
                spawn.TemplateAlias).Score).ToArray();
        Check.True(
            scores.Count(score => score == 1) == 102 &&
            scores.Count(score => score == 50) == 32 &&
            scores.Count(score => score == 1_000) == 1 &&
            scores.Count(score => score == 1_100) == 1 &&
            scores.Sum() == 3_802,
            "external score values produce the exact 3,802-point roster total");
        Check.Equal(
            3_802,
            MedusaIslandEncounterPolicy.TotalVictoryScore(
                MedusaIslandEncounterPolicy.Difficulties.Single(value =>
                    value.Difficulty ==
                        MedusaEncounterDifficulty.Enhanced)),
            "runtime scoring has no obsolete 3,000-point cap");
    }

    private static void CheckLootRollsAndPackets()
    {
        var content = MedusaMonsterContentCatalog.Current;
        Check.Equal(27, content.Loot.Count,
            "nine editable drop rows are present per difficulty");
        var deathEventId = new Guid(
            "1f20be32-8403-4e74-bab6-6bfdc22f9051");
        var first = content.RollLoot(
            MedusaEncounterDifficulty.Enhanced,
            MedusaIslandRosterTemplateAliases.Medusa,
            deathEventId);
        var replay = content.RollLoot(
            MedusaEncounterDifficulty.Enhanced,
            MedusaIslandRosterTemplateAliases.Medusa,
            deathEventId);
        Check.True(
            first.SequenceEqual(replay) &&
            first.Select(static drop => (drop.ItemId, drop.Quantity))
                .SequenceEqual([(9941u, 1), (9940u, 1), (9916u, 6)]),
            "loot rolling is deterministic and retains captured Medusa drops");

        var entries = first.Select((drop, index) => new MonsterLootEntry(
            index,
            drop.LootIndex,
            drop.ItemId,
            drop.Quantity)).ToArray();
        var packet = PacketBuilder.MonsterLoot(0x1234_5678, entries);
        Check.True(
            packet.Length == 228 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet) == 228 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) ==
                Opcodes.MonsterDrops &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) ==
                0x1234_5678 &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8)) == 3,
            "opcode 10029 carries the captured header and three 72-byte items");
        for (var index = 0; index < entries.Length; index++)
        {
            var item = packet.AsSpan(12 + index * 72, 72);
            var sentinelsMatch = true;
            for (var sentinel = 1; sentinel <= 5; sentinel++)
            {
                sentinelsMatch &=
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        item.Slice(sentinel * 4)) == uint.MaxValue;
            }
            Check.True(
                BinaryPrimitives.ReadUInt32LittleEndian(item) ==
                    entries[index].ItemId &&
                sentinelsMatch &&
                BinaryPrimitives.ReadUInt32LittleEndian(item.Slice(24)) ==
                    ((uint)entries[index].Quantity << 24 | 0x101u) &&
                item[28..].IndexOfAnyExcept((byte)0) < 0,
                $"drop {index} matches captured opcode-10029 layout");
        }

        var pickup = PacketBuilder.MonsterLootPickup(
            0x1020_3040,
            0x5060_7080,
            2);
        Check.True(
            pickup.Length == 16 &&
            BinaryPrimitives.ReadUInt16LittleEndian(pickup) == 16 &&
            BinaryPrimitives.ReadUInt16LittleEndian(pickup.AsSpan(2)) ==
                Opcodes.PickupDrops &&
            BinaryPrimitives.ReadUInt32LittleEndian(pickup.AsSpan(4)) ==
                0x1020_3040 &&
            BinaryPrimitives.ReadUInt32LittleEndian(pickup.AsSpan(8)) ==
                0x5060_7080 &&
            BinaryPrimitives.ReadInt32LittleEndian(pickup.AsSpan(12)) == 2,
            "opcode 10048 exactly acknowledges the picked corpse slot");
    }

    private static void CheckMalformedContentFailsClosed()
    {
        var content = MedusaMonsterContentCatalog.Current;
        var duplicateRules = content.Monsters
            .Append(content.Monsters.First())
            .ToArray();
        Check.Throws<InvalidDataException>(
            () => new MedusaMonsterContentSnapshot(
                duplicateRules,
                content.Loot.ToArray()),
            "duplicate difficulty/template rules fail closed");

        var malformedLoot = content.Loot.ToArray();
        malformedLoot[0] = malformedLoot[0] with
        {
            ChanceBasisPoints = 0
        };
        Check.Throws<InvalidDataException>(
            () => new MedusaMonsterContentSnapshot(
                content.Monsters.ToArray(),
                malformedLoot),
            "invalid editable drop chance fails closed");
    }

    private static MedusaMonsterRule Rule(
        MedusaEncounterDifficulty difficulty,
        string templateAlias)
    {
        Check.True(
            MedusaMonsterContentCatalog.Current.TryGetMonster(
                difficulty,
                templateAlias,
                out var rule),
            $"{difficulty}/{templateAlias} rule resolves");
        return rule;
    }
}
