using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class MountEquipmentProgressionChecks
{
    private static readonly string[] MountGearKinds =
    [
        "mounthead", "mountarmor", "mountsoul", "mountornament", "mountamulet"
    ];

    private static readonly string[] QualityVectorNames =
    [
        "Attack", "AttackRadius", "AttackSpeed", "MaxHP", "MaxMP", "Defence",
        "MagicAk", "MagicRec", "Hit", "Miss", "State", "StateImmunity",
        "AcceptCure", "Cure", "PhysicalDamage", "MagicDamage",
        "PhysicalDamageAbsorb", "MagicDamageAbsorb", "Speed", "FuryAddAk",
        "FuryAddRec", "InjureImbibe"
    ];

    private static readonly decimal[] GradeProfile =
    [
        116m, 133m, 151m, 170m, 190m, 211m, 233m,
        256m, 280m, 305m, 332m, 365m, 400m
    ];

    // These are the complete native G13-G25 tails whose values exceed the
    // ordinary profile at one or more grades. All other native tails are
    // below the profile floor and therefore need no separate exception data.
    private static readonly IReadOnlyDictionary<int, decimal[]>
        LegacyAuthoredGradeTailsAboveProfile = new Dictionary<int, decimal[]>
        {
            [10] = [90m, 100m, 113m, 127m, 142m, 160m, 179m, 200m, 223m, 247m, 274m, 302m, 331m],
            [30] = [63m, 71m, 80m, 90m, 101m, 113m, 127m, 142m, 158m, 176m, 195m, 215m, 236m],
            [60] = [25m, 28m, 31m, 35m, 40m, 45m, 50m, 56m, 63m, 70m, 77m, 85m, 94m],
            [70] = [37m, 41m, 47m, 53m, 59m, 67m, 75m, 84m, 94m, 104m, 116m, 128m, 140m],
            [100] = [100m, 112m, 126m, 142m, 160m, 180m, 202m, 226m, 252m, 280m, 310m, 342m, 376m],
            [101] = [153m, 171m, 192m, 216m, 243m, 273m, 306m, 342m, 381m, 423m, 468m, 516m, 567m],
            [102] = [204m, 228m, 256m, 288m, 324m, 364m, 408m, 456m, 508m, 564m, 624m, 688m, 756m],
            [110] = [43m, 49m, 56m, 64m, 73m, 83m, 94m, 106m, 119m, 133m, 148m, 164m, 181m],
            [120] = [43m, 49m, 56m, 64m, 73m, 83m, 94m, 106m, 119m, 133m, 148m, 164m, 181m],
            [211] = [24m, 27m, 30m, 34m, 39m, 44m, 49m, 55m, 62m, 69m, 76m, 84m, 93m],
            [231] = [24m, 27m, 30m, 34m, 39m, 44m, 49m, 55m, 62m, 69m, 76m, 84m, 93m],
            [321] = [5m, 7m, 9m, 11m, 13m, 15m, 18m, 21m, 24m, 28m, 32m, 36m, 40m],
            [333] = [6m, 8m, 10m, 12m, 14m, 16m, 19m, 22m, 25m, 29m, 33m, 37m, 41m],
            [336] = [9m, 11m, 13m, 15m, 17m, 19m, 22m, 25m, 28m, 32m, 36m, 40m, 44m]
        };

    public static Task RunAsync()
    {
        CheckMountGearQualityVectors();
        CheckGradeAttributeVectors();
        CheckMountGearSnapshot();
        return Task.CompletedTask;
    }

    private static void CheckMountGearQualityVectors()
    {
        CheckAllMountGearQualityVectors();
        CheckAllMountFamilyQualityVectors();
        CheckVector(14504, "Hit", 13m, 27m, 43m);
        CheckVector(14604, "MaxHP", 2_960m, 5_920m, 9_209m);
        CheckVector(14704, "InjureImbibe", 74m, 148m, 230m);
        CheckVector(14804, "MaxHP", 1_110m, 2_220m, 3_453m);
        CheckVector(14904, "Miss", 12m, 24m, 37m);

        // Native mount base vectors remain flat through Q10. Q11-Q20 then
        // distribute one conservative, level-family tier of progression.
        CheckVector(16204, "Speed", 0.24m, 0.24m, 0.25m);
        CheckVector(16204, "MaxHP", 3_700m, 3_700m, 4_000m);
    }

    private static void CheckAllMountGearQualityVectors()
    {
        var mountGear = ItemTemplateSeeds.All
            .Where(template => MountGearKinds.Contains(
                template.Kind,
                StringComparer.OrdinalIgnoreCase))
            .ToArray();
        Check.Equal(45, mountGear.Length, "complete mount-gear template catalog");

        foreach (var kind in mountGear.GroupBy(static template => template.Kind))
        {
            var tiers = kind.OrderBy(static template => template.Id).ToArray();
            Check.Equal(9, tiers.Length, $"{kind.Key} level-tier count");

            foreach (var key in QualityVectorNames)
            {
                var members = tiers
                    .Select(template => (
                        template.Id,
                        Values: TryReadVector(template, key)))
                    .Where(static member => member.Values is not null)
                    .Select(static member => (member.Id, Values: member.Values!))
                    .ToArray();
                if (members.Length == 0)
                {
                    continue;
                }

                Check.Equal(
                    tiers.Length,
                    members.Length,
                    $"{kind.Key} {key} exists on every level tier");
                foreach (var member in members)
                {
                    var values = member.Values;
                    Check.Equal(20, values.Length, $"{member.Id} {key} quality count");
                    for (var qualityIndex = 1; qualityIndex < values.Length; qualityIndex++)
                    {
                        Check.True(
                            values[qualityIndex] >= values[qualityIndex - 1],
                            $"{member.Id} {key} remains monotonic at Q{qualityIndex + 1}");
                    }

                    var averageSlope = (values[9] - values[0]) / 9m;
                    var integerOnly = values
                        .Take(10)
                        .All(value => value == decimal.Truncate(value));
                    for (var qualityIndex = 10; qualityIndex < values.Length; qualityIndex++)
                    {
                        var step = qualityIndex - 9;
                        var expected = values[9] + (averageSlope * step);
                        expected = decimal.Round(
                            expected,
                            integerOnly ? 0 : 12,
                            MidpointRounding.AwayFromZero);
                        Check.Equal(
                            expected,
                            values[qualityIndex],
                            $"{member.Id} {key} Q{qualityIndex + 1} uses the native average slope");
                    }
                }

                for (var tierIndex = 1; tierIndex < members.Length; tierIndex++)
                {
                    for (var qualityIndex = 0; qualityIndex < 20; qualityIndex++)
                    {
                        Check.True(
                            members[tierIndex].Values[qualityIndex] >=
                            members[tierIndex - 1].Values[qualityIndex],
                            $"{kind.Key} {key} tier order at Q{qualityIndex + 1}");
                    }
                }
            }
        }
    }

    private static void CheckAllMountFamilyQualityVectors()
    {
        var mounts = ItemTemplateSeeds.All
            .Where(static template =>
                string.Equals(template.Kind, "mount", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Check.Equal(350, mounts.Length, "complete mount template catalog");

        foreach (var family in mounts.GroupBy(static template => template.Id / 10))
        {
            foreach (var key in new[] { "Speed", "MaxHP" })
            {
                var members = family
                    .Select(template => (
                        template.Id,
                        Values: TryReadVector(template, key)))
                    .Where(static member => member.Values is not null)
                    .Select(static member => (member.Id, Values: member.Values!))
                    .OrderBy(static member => member.Values[0])
                    .ThenBy(static member => member.Id)
                    .ToArray();
                if (members.Length == 0)
                {
                    continue;
                }

                var nativeValues = members
                    .Select(static member => member.Values[0])
                    .Distinct()
                    .Order()
                    .ToArray();
                var familyDelta = Enumerable.Range(1, nativeValues.Length - 1)
                    .Select(index => nativeValues[index] - nativeValues[index - 1])
                    .Where(static delta => delta > 0m)
                    .DefaultIfEmpty(0m)
                    .Min();

                foreach (var member in members)
                {
                    Check.Equal(
                        20,
                        member.Values.Length,
                        $"{member.Id} {key} quality vector length");
                    Check.True(
                        member.Values.Take(10).All(value => value == member.Values[0]),
                        $"{member.Id} {key} preserves its flat native Q1-Q10 prefix");
                    Check.Equal(
                        member.Values[0] + familyDelta,
                        member.Values[19],
                        $"{member.Id} {key} Q20 gains exactly one family-tier step");
                }

                for (var index = 1; index < members.Length; index++)
                {
                    Check.True(
                        members[index].Values[19] >= members[index - 1].Values[19],
                        $"{family.Key} {key} preserves mount-tier ordering at Q20");
                }
            }
        }
    }

    private static void CheckGradeAttributeVectors()
    {
        CheckAllGradeAttributeExtensions();
        CheckAttribute(343, 28m, 55m, 220m);
        CheckAttribute(363, 0.0051m, 0.0102m, 0.0408m);
        CheckAttribute(403, 0.0051m, 0.0102m, 0.0408m);
        CheckAttribute(423, 61m, 122m, 488m);
    }

    private static void CheckAllGradeAttributeExtensions()
    {
        var templates = ItemAttributeTemplateSeeds.All;
        Check.Equal(195, templates.Count, "complete item-attribute catalog");
        Check.Equal(
            templates.Count,
            templates.Select(template => template.Id).Distinct().Count(),
            "item-attribute IDs remain unique");
        Check.Equal(
            14,
            LegacyAuthoredGradeTailsAboveProfile.Count,
            "all native tails above the ordinary profile are guarded");

        foreach (var template in templates)
        {
            Check.Equal((short)25, template.MaxLevel, $"{template.Id} maximum grade");
            Check.Equal(25, template.LevelValues.Length, $"{template.Id} grade vector length");

            var values = template.LevelValues;
            var anchor = values[11];
            var integerOnly = values
                .Take(12)
                .All(value => value == decimal.Truncate(value));
            LegacyAuthoredGradeTailsAboveProfile.TryGetValue(
                template.Id,
                out var authoredTail);
            var previous = anchor;

            for (var tailIndex = 0; tailIndex < GradeProfile.Length; tailIndex++)
            {
                var grade = tailIndex + 13;
                var value = values[tailIndex + 12];
                var profileFloor = anchor * GradeProfile[tailIndex] / 100m;
                if (integerOnly)
                {
                    profileFloor = decimal.Round(
                        profileFloor,
                        0,
                        MidpointRounding.AwayFromZero);
                }

                var authoredFloor = authoredTail?[tailIndex] ?? decimal.MinValue;
                var minimum = Math.Max(profileFloor, authoredFloor);
                Check.True(
                    value >= minimum,
                    $"{template.Id} G{grade} cannot regress below its authored/profile floor");
                Check.True(
                    value >= previous,
                    $"{template.Id} G{grade} remains monotonic");
                previous = value;
            }
        }
    }

    private static void CheckMountGearSnapshot()
    {
        var item = CompactItemEntry.Empty with
        {
            Id = 14504,
            Attribute1 = 343,
            Attribute2 = 363,
            Attribute3 = 403,
            Attribute4 = 423,
            Quality = 20,
            Grade = 25,
            Bound = 1,
            Stack = 1
        };
        var character = new GameCharacter
        {
            Profession = 0,
            Equipment = EquipmentSlots.SetSlot(
                GameDefaults.DefaultEquipment(0),
                0,
                EquipmentSlots.MountHead,
                item.ToCompactString())
        };

        var packet = PacketBuilder.EquipmentItemSnapshot(
            character,
            EquipmentSlots.MountHead);
        Check.Equal(92, packet.Length, "mount-head item snapshot length");
        Check.Equal(343, ReadInt32(packet, 24), "mount-head snapshot attribute 1");
        Check.Equal(363, ReadInt32(packet, 28), "mount-head snapshot attribute 2");
        Check.Equal(403, ReadInt32(packet, 32), "mount-head snapshot attribute 3");
        Check.Equal(423, ReadInt32(packet, 36), "mount-head snapshot attribute 4");
        Check.Equal((byte)20, packet[44], "mount-head snapshot full quality");
        Check.Equal((byte)25, packet[45], "mount-head snapshot full grade");
    }

    private static void CheckVector(
        int itemId,
        string key,
        decimal quality1,
        decimal quality10,
        decimal quality20)
    {
        var template = ItemTemplateSeeds.All.Single(template => template.Id == itemId);
        var values = TryReadVector(template, key)
            ?? throw new InvalidOperationException($"{itemId} lacks {key}.");

        Check.Equal(20, values.Length, $"{itemId} {key} quality vector length");
        Check.Equal(quality1, values[0], $"{itemId} {key} Q1");
        Check.Equal(quality10, values[9], $"{itemId} {key} native Q10");
        Check.Equal(quality20, values[19], $"{itemId} {key} designed Q20");
    }

    private static decimal[]? TryReadVector(ItemTemplateSeed template, string key)
    {
        using var document = JsonDocument.Parse(template.StatsJson);
        if (!document.RootElement.TryGetProperty(key, out var property))
        {
            return null;
        }

        return property
            .GetString()!
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => decimal.Parse(value, CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static void CheckAttribute(
        int attributeId,
        decimal grade1,
        decimal grade12,
        decimal grade25)
    {
        var template = ItemAttributeTemplateSeeds.All.Single(
            template => template.Id == attributeId);
        Check.Equal((short)25, template.MaxLevel, $"{attributeId} maximum grade");
        Check.Equal(25, template.LevelValues.Length, $"{attributeId} grade vector length");
        Check.Equal(grade1, template.LevelValues[0], $"{attributeId} G1");
        Check.Equal(grade12, template.LevelValues[11], $"{attributeId} native G12");
        Check.Equal(grade25, template.LevelValues[24], $"{attributeId} designed G25");
    }

    private static int ReadInt32(byte[] packet, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset, sizeof(int)));
}
