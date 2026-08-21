using System.Buffers.Binary;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolySpiritEffectivenessPolicyChecks
{
    public const string CheckName =
        "Authoritative Holy Spirit effectiveness brackets";

    public static Task RunAsync()
    {
        AssertReviewedCatalog();
        AssertGradeBracketsPartitionEveryRange();
        AssertCooledReductionBalance();
        AssertBoundaryRollsAndGoddessFloor();
        AssertLegacyCompatibilityValues();
        AssertNativeImplementationErrors();
        AssertCompactAndWireValuePreservation();
        AssertInvalidInputFailsClosed();
        return Task.CompletedTask;
    }

    private static void AssertCompactAndWireValuePreservation()
    {
        var gear = CompactItemEntry.Empty with
        {
            Id = 1035,
            Quality = 1,
            Grade = 25,
            Bound = 1,
            Stack = 1,
            SocketCount = 4,
            Socket1EffectId = 1,
            Socket1Level = 10,
            Socket1Value = 797,
            Socket2EffectId = 5,
            Socket2Level = 7,
            Socket2Value = 213,
            Socket3EffectId = 7,
            Socket3Level = 9,
            Socket3Value = 511,
            Socket4EffectId = 8,
            Socket4Level = 10,
            Socket4Value = 991
        };
        Check.Equal(
            gear,
            CompactItemEntry.Parse(gear.ToCompactString()),
            "compact item round trip preserves all four final rolls");

        var character = new GameCharacter
        {
            KitBag = KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                0,
                gear.ToCompactString())
        };
        var record = PacketBuilder.KitBagDetailPages(character)[0]
            .AsSpan(24, 72);
        Check.True(
            BinaryPrimitives.ReadInt16LittleEndian(record.Slice(44, 2)) ==
                797 &&
            BinaryPrimitives.ReadInt16LittleEndian(record.Slice(46, 2)) ==
                213 &&
            BinaryPrimitives.ReadInt16LittleEndian(record.Slice(48, 2)) ==
                511 &&
            BinaryPrimitives.ReadInt16LittleEndian(record.Slice(50, 2)) ==
                991,
            "wire projection emits every persisted final roll unchanged");
    }

    private static void AssertLegacyCompatibilityValues()
    {
        Check.True(
            HolySpiritLegacyEffectiveness.TryResolve(1, 10, out var value) &&
            value == 1400,
            "legacy-null Destruction resolves its former Grade-10 value");
        Check.True(
            !HolySpiritLegacyEffectiveness.TryResolve(99, 10, out _) &&
            !HolySpiritLegacyEffectiveness.TryResolve(1, 11, out _),
            "unknown legacy effects and grades fail closed");
    }

    private static void AssertNativeImplementationErrors()
    {
        Check.Equal(
            1600,
            HolyStoneNativeResults.GetResultSubId(
                HolyStoneCommandOperation.ImplementSpirit,
                HolyStoneCommandResultStatus.TargetNotHolyStone),
            "Implement slot one requires a Holy Stone");
        Check.Equal(
            2100,
            HolyStoneNativeResults.GetResultSubId(
                HolyStoneCommandOperation.ImplementSpirit,
                HolyStoneCommandResultStatus.StoneNotHolyStone),
            "Implement slot two requires a Holy Spirit");
    }

    private static void AssertReviewedCatalog()
    {
        Check.Equal(3, HolyStoneAffinityCatalog.All.Count,
            "reviewed Holy Stone affinity count");
        Check.True(
            HolyStoneAffinityCatalog.TryGetByItemId(9030, out var heated) &&
            heated.Affinity == HolyStoneAffinity.Heated &&
            heated.AllowedEquipmentSlots.Contains(EquipmentSlots.Weapon) &&
            !heated.AllowedEquipmentSlots.Contains(EquipmentSlots.Armor),
            "Heated Holy Stone owns offensive equipment slots");
        Check.True(
            HolyStoneAffinityCatalog.TryGetByItemId(9031, out var cooled) &&
            cooled.Affinity == HolyStoneAffinity.Cooled &&
            cooled.AllowedEquipmentSlots.Contains(EquipmentSlots.Armor) &&
            !cooled.AllowedEquipmentSlots.Contains(EquipmentSlots.Weapon),
            "Cooled Holy Stone owns defensive equipment slots");
        Check.True(
            HolyStoneAffinityCatalog.TryGetByItemId(9032, out var zephyr) &&
            zephyr.Affinity == HolyStoneAffinity.Zephyr &&
            zephyr.AllowedEquipmentSlots.Contains(
                EquipmentSlots.MountHead) &&
            zephyr.AllowedEquipmentSlots.Contains(
                EquipmentSlots.MountAmulet) &&
            !zephyr.AllowedEquipmentSlots.Contains(EquipmentSlots.Weapon),
            "Zephyr Holy Stone owns only mount-gear slots");
        Check.True(
            !HolyStoneAffinityCatalog.TryGetByItemId(9033, out _) &&
            !HolyStoneAffinityCatalog.TryGetItemId(
                (HolyStoneAffinity)byte.MaxValue,
                out _) &&
            !HolyStoneAffinityCatalog.IsCompatibleWithEquipmentSlot(
                EquipmentSlots.MountHead,
                9030),
            "unknown affinities and mount gear fail closed");

        var expected = new[]
        {
            E(9060, HolyStoneAffinity.Heated, 1, 32, 80,
                HolySpiritValueKind.HundredthPercent),
            E(9061, HolyStoneAffinity.Heated, 2, 32, 80,
                HolySpiritValueKind.HundredthPercent),
            E(9062, HolyStoneAffinity.Heated, 5, 16, 40,
                HolySpiritValueKind.Flat),
            E(9063, HolyStoneAffinity.Heated, 6, 12, 30,
                HolySpiritValueKind.Flat),
            E(9064, HolyStoneAffinity.Heated, 7, 24, 60,
                HolySpiritValueKind.HundredthPercent),
            E(9065, HolyStoneAffinity.Heated, 8, 40, 100,
                HolySpiritValueKind.Flat),
            E(9066, HolyStoneAffinity.Heated, 3, 20, 50,
                HolySpiritValueKind.HundredthPercent),
            E(9067, HolyStoneAffinity.Heated, 4, 24, 60,
                HolySpiritValueKind.HundredthPercent),
            E(9080, HolyStoneAffinity.Cooled, 9, 22, 80,
                HolySpiritValueKind.HundredthPercent),
            E(9081, HolyStoneAffinity.Cooled, 10, 22, 80,
                HolySpiritValueKind.HundredthPercent),
            E(9082, HolyStoneAffinity.Cooled, 11, 16, 40,
                HolySpiritValueKind.Flat),
            E(9083, HolyStoneAffinity.Cooled, 12, 14, 35,
                HolySpiritValueKind.Flat),
            E(9084, HolyStoneAffinity.Cooled, 19, 16, 40,
                HolySpiritValueKind.HundredthPercent),
            E(9085, HolyStoneAffinity.Cooled, 20, 16, 40,
                HolySpiritValueKind.Flat),
            E(9086, HolyStoneAffinity.Cooled, 13, 28, 70,
                HolySpiritValueKind.HundredthPercent),
            E(9087, HolyStoneAffinity.Cooled, 14, 40, 100,
                HolySpiritValueKind.Flat),
            E(9090, HolyStoneAffinity.Zephyr, 21, 15, 30,
                HolySpiritValueKind.HundredthPercent),
            E(9091, HolyStoneAffinity.Zephyr, 22, 10, 20,
                HolySpiritValueKind.HundredthPercent),
            E(9092, HolyStoneAffinity.Zephyr, 23, 100, 200,
                HolySpiritValueKind.HundredthPercent),
            E(9093, HolyStoneAffinity.Zephyr, 24, 75, 150,
                HolySpiritValueKind.HundredthPercent)
        };

        Check.Equal(20, HolySpiritEffectivenessPolicy.All.Count,
            "reviewed Holy Spirit definition count");
        Check.Equal(
            20,
            HolySpiritEffectivenessPolicy.All
                .Select(static value => value.ItemId)
                .Distinct()
                .Count(),
            "Holy Spirit item IDs are unique");
        Check.Equal(
            20,
            HolySpiritEffectivenessPolicy.All
                .Select(static value => value.EffectId)
                .Distinct()
                .Count(),
            "native Holy Spirit effect IDs are unique");

        foreach (var value in expected)
        {
            Check.True(
                HolySpiritEffectivenessPolicy.TryGetDefinition(
                    value.ItemId,
                    out var definition),
                $"Holy Spirit {value.ItemId} exists");
            Check.True(
                definition.Affinity == value.Affinity &&
                definition.EffectId == value.EffectId &&
                definition.GradeOneMinimumValue == value.Minimum &&
                definition.GradeOneMaximumValue == value.Maximum &&
                definition.ValueKind == value.ValueKind,
                $"Holy Spirit {value.ItemId} keeps reviewed effect values");
        }

        Check.True(
            HolySpiritEffectivenessPolicy.All
                .Where(static value => value.Affinity == HolyStoneAffinity.Heated)
                .All(static value => value.ItemId is >= 9060 and <= 9067),
            "offensive spirits retain heated-stone affinity");
        Check.True(
            HolySpiritEffectivenessPolicy.All
                .Where(static value => value.Affinity == HolyStoneAffinity.Cooled)
                .All(static value => value.ItemId is >= 9080 and <= 9087),
            "defensive spirits retain cooled-stone affinity");
        Check.True(
            HolySpiritEffectivenessPolicy.All
                .Where(static value => value.Affinity == HolyStoneAffinity.Zephyr)
                .All(static value => value.ItemId is >= 9090 and <= 9093),
            "mount-gear spirits retain Zephyr-stone affinity");
        Check.True(
            HolySpiritEffectivenessPolicy.IsCompatibleWithHolyStone(9060, 9030) &&
            !HolySpiritEffectivenessPolicy.IsCompatibleWithHolyStone(9060, 9031) &&
            HolySpiritEffectivenessPolicy.IsCompatibleWithHolyStone(9080, 9031) &&
            !HolySpiritEffectivenessPolicy.IsCompatibleWithHolyStone(9080, 9030) &&
            HolySpiritEffectivenessPolicy.IsCompatibleWithHolyStone(9090, 9032) &&
            !HolySpiritEffectivenessPolicy.IsCompatibleWithHolyStone(9090, 9030),
            "heated, cooled, and Zephyr compatibility is authoritative");
        Check.True(
            !HolySpiritEffectivenessPolicy.TryGetDefinition(9068, out _) &&
            !HolySpiritEffectivenessPolicy.TryGetDefinition(9069, out _) &&
            !HolySpiritEffectivenessPolicy.TryGetDefinition(9088, out _) &&
            !HolySpiritEffectivenessPolicy.TryGetDefinition(9089, out _),
            "extra client spirits remain unsupported until ranges are reviewed");
    }

    private static void AssertGradeBracketsPartitionEveryRange()
    {
        foreach (var definition in HolySpiritEffectivenessPolicy.All)
        {
            var previousLower = 0;
            var previousUpper = 0;
            for (var grade = 1; grade <= 10; grade++)
            {
                Check.True(
                    HolySpiritEffectivenessPolicy.TryGetGradeBracket(
                        definition.ItemId,
                        grade,
                        out var lower,
                        out var upper),
                    $"Holy Spirit {definition.ItemId} grade {grade} resolves");
                Check.True(
                    lower == definition.GradeOneMinimumValue * grade &&
                    upper == definition.GradeOneMaximumValue * grade &&
                    upper >= lower,
                    $"Holy Spirit {definition.ItemId} grade {grade} scales " +
                    "the native Grade-1 bracket");
                if (grade > 1)
                {
                    Check.True(
                        lower > previousLower && upper > previousUpper,
                        $"Holy Spirit {definition.ItemId} brackets grow " +
                        "monotonically");
                }
                if (grade == 9)
                {
                    Check.True(
                        upper < definition.GradeOneMaximumValue * 10,
                        $"Holy Spirit {definition.ItemId} grade 9 cannot roll cap");
                }

                previousLower = lower;
                previousUpper = upper;
            }

            Check.Equal(
                definition.GradeOneMaximumValue * 10,
                previousUpper,
                $"Holy Spirit {definition.ItemId} grade 10 includes cap");
        }

        Check.True(
            HolySpiritEffectivenessPolicy.TryGetGradeBracket(
                9060,
                1,
                out var levelOneLower,
                out var levelOneUpper) &&
            levelOneLower == 32 &&
            levelOneUpper == 80,
            "Destruction Grade 1 uses native 0.32%-0.80% bracket");
        Check.True(
            HolySpiritEffectivenessPolicy.TryGetGradeBracket(
                9060,
                10,
                out var levelTenLower,
                out var levelTenUpper) &&
            levelTenLower == 320 &&
            levelTenUpper == 800,
            "Destruction Grade 10 uses the requested 3.20%-8.00% bracket");
    }

    private static void AssertBoundaryRollsAndGoddessFloor()
    {
        var minimumSource = new BoundaryRandomSource(useMaximum: false);
        var ordinary = HolySpiritEffectivenessPolicy.Roll(
            9060,
            10,
            hasGoddessStone: false,
            minimumSource);
        Check.Equal(
            ordinary.Bracket.MinimumValue,
            ordinary.Value,
            "ordinary implementation may roll grade bracket minimum");

        var goddessSource = new BoundaryRandomSource(useMaximum: false);
        var goddess = HolySpiritEffectivenessPolicy.Roll(
            9060,
            10,
            hasGoddessStone: true,
            goddessSource);
        Check.True(
            goddess.Bracket.MinimumValue > ordinary.Bracket.MinimumValue &&
            goddess.Bracket.MaximumValue == ordinary.Bracket.MaximumValue &&
            goddess.Value == goddess.Bracket.MinimumValue,
            "Goddess Stone raises only the random lower limit");

        var maximumSource = new BoundaryRandomSource(useMaximum: true);
        var maximum = HolySpiritEffectivenessPolicy.Roll(
            9060,
            10,
            hasGoddessStone: false,
            maximumSource);
        Check.Equal(800, maximum.Value,
            "grade 10 random bracket can reach 8.00% cap");

        var levelNineMaximum = HolySpiritEffectivenessPolicy.Roll(
            9060,
            9,
            hasGoddessStone: true,
            maximumSource);
        Check.True(levelNineMaximum.Value < 800,
            "Goddess Stone cannot let grade 9 reach absolute cap");
    }

    private static void AssertInvalidInputFailsClosed()
    {
        Check.True(
            !HolySpiritEffectivenessPolicy.TryGetDefinition(999_999, out _) &&
            !HolySpiritEffectivenessPolicy.TryGetGradeBracket(
                999_999, 10, out _, out _) &&
            !HolySpiritEffectivenessPolicy.TryGetGradeBracket(
                9060, 0, out _, out _) &&
            !HolySpiritEffectivenessPolicy.TryGetGradeBracket(
                9060, 11, out _, out _),
            "unknown spirits and invalid grades fail closed");

        Check.Throws<ArgumentOutOfRangeException>(
            () => HolySpiritEffectivenessPolicy.Roll(
                999_999,
                10,
                false,
                new BoundaryRandomSource(false)),
            "unknown spirit roll is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => HolySpiritEffectivenessPolicy.Roll(
                9060,
                11,
                false,
                new BoundaryRandomSource(false)),
            "invalid Holy Stone grade roll is rejected");
        Check.Throws<InvalidOperationException>(
            () => HolySpiritEffectivenessPolicy.Roll(
                9060,
                10,
                false,
                new OutOfRangeRandomSource()),
            "out-of-contract random source fails closed");
    }

    private static ExpectedDefinition E(
        uint itemId,
        HolyStoneAffinity affinity,
        short effectId,
        int minimum,
        int maximum,
        HolySpiritValueKind valueKind) =>
        new(itemId, affinity, effectId, minimum, maximum, valueKind);

    private sealed record ExpectedDefinition(
        uint ItemId,
        HolyStoneAffinity Affinity,
        short EffectId,
        int Minimum,
        int Maximum,
        HolySpiritValueKind ValueKind);

    private sealed class BoundaryRandomSource(bool useMaximum) :
        IHolySpiritEffectivenessRandomSource
    {
        public int NextInclusive(int minimumInclusive, int maximumInclusive) =>
            useMaximum ? maximumInclusive : minimumInclusive;
    }

    private sealed class OutOfRangeRandomSource :
        IHolySpiritEffectivenessRandomSource
    {
        public int NextInclusive(int minimumInclusive, int maximumInclusive) =>
            checked(maximumInclusive + 1);
    }
}
