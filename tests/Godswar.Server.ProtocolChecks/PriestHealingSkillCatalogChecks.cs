using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PriestHealingSkillCatalogChecks
{
    public const string CheckName =
        "Authoritative Priest healing skill rules";

    private static readonly (int SkillId, int HealAmount)[] SingleTargetRanks =
    [
        (750, 500),
        (751, 900),
        (752, 1_600),
        (753, 2_500),
        (754, 4_000)
    ];

    private static readonly (int SkillId, int HealAmount)[] AreaRanks =
    [
        (760, 300),
        (761, 500),
        (762, 800),
        (763, 1_400),
        (764, 2_200)
    ];

    public static Task RunAsync()
    {
        CheckAllPublishedRanks(
            SingleTargetRanks,
            PriestHealingSkillKind.SingleTarget);
        CheckAllPublishedRanks(
            AreaRanks,
            PriestHealingSkillKind.Area);
        CheckInvalidDefinitions();
        CheckCombatTextAmounts();
        return Task.CompletedTask;
    }

    private static void CheckCombatTextAmounts()
    {
        Check.Equal(
            4_000,
            PriestHealingMath.ResolveCombatTextAmount(
                resolvedHeal: 4_000,
                appliedHeal: 750),
            "Priest healing reports resolved healing before the HP cap");
        Check.Equal(
            4_000,
            PriestHealingMath.ResolveCombatTextAmount(
                resolvedHeal: 4_000,
                appliedHeal: 0),
            "Priest healing remains visible when the target is already at full HP");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = PriestHealingMath.ResolveCombatTextAmount(500, 501),
            "Priest healing rejects an applied amount above the resolution");

        Check.Equal(
            4_000,
            PriestHealingMath.ResolveHealAmount(
                baseHeal: 4_000,
                outgoingHealingBonusBasisPoints: 0,
                receivedHealingBonusBasisPoints: 0),
            "Priest healing preserves its skill base without modifiers");
        Check.Equal(
            1_025,
            PriestHealingMath.ResolveHealAmount(
                baseHeal: 500,
                outgoingHealingBonusBasisPoints: 6_000,
                receivedHealingBonusBasisPoints: 4_500),
            "Priest healing adds outgoing and received bonuses like the reference server");
        Check.Equal(
            3_257,
            PriestHealingMath.ResolveHealAmount(
                baseHeal: 1_600,
                outgoingHealingBonusBasisPoints: 9_000,
                receivedHealingBonusBasisPoints: 1_360),
            "Priest healing matches the captured Heal 3 amount and truncation");
        Check.Equal(
            67_648,
            PriestHealingMath.ResolveHealAmount(
                baseHeal: 4_000,
                outgoingHealingBonusBasisPoints: 159_120,
                receivedHealingBonusBasisPoints: 0),
            "Priest healing applies test10's outgoing Healing stat");
        Check.Equal(
            0,
            PriestHealingMath.ResolveHealAmount(
                baseHeal: 4_000,
                outgoingHealingBonusBasisPoints: -10_000,
                receivedHealingBonusBasisPoints: 0),
            "Priest healing supports complete outgoing suppression");
        Check.Equal(
            int.MaxValue,
            PriestHealingMath.ResolveHealAmount(
                baseHeal: int.MaxValue,
                outgoingHealingBonusBasisPoints: int.MaxValue,
                receivedHealingBonusBasisPoints: int.MaxValue),
            "Priest healing clamps an overflowing combat amount");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = PriestHealingMath.ResolveHealAmount(0, 0, 0),
            "Priest healing rejects a non-positive skill base");
    }

    private static void CheckAllPublishedRanks(
        IReadOnlyList<(int SkillId, int HealAmount)> expectedRanks,
        PriestHealingSkillKind expectedKind)
    {
        foreach (var expected in expectedRanks)
        {
            Check.True(
                GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(
                    expected.SkillId,
                    out var combat),
                $"Priest healing skill {expected.SkillId} combat data exists");
            Check.True(
                PriestHealingSkillCatalog.TryResolve(
                    combat,
                    out var definition),
                $"Priest healing skill {expected.SkillId} resolves");
            Check.Equal(
                expected.SkillId,
                definition.SkillId,
                $"Priest healing skill {expected.SkillId} identity");
            Check.True(
                definition.Kind == expectedKind,
                $"Priest healing skill {expected.SkillId} kind");
            Check.Equal(
                expected.HealAmount,
                definition.HealAmount,
                $"Priest healing skill {expected.SkillId} Power2 heal amount");
        }
    }

    private static void CheckInvalidDefinitions()
    {
        var single = GetCombat(750);
        var area = GetCombat(760);

        Check.True(
            PriestHealingSkillCatalog.TryResolve(
                single with { Power2 = 777m },
                out var retuned),
            "Priest healing accepts a positive integral Power2 retune");
        Check.Equal(
            777,
            retuned.HealAmount,
            "Priest healing amount is sourced from Power2");

        CheckRejected(single with { SkillId = 755 }, "unknown family gap");
        CheckRejected(single with { Target = 1 }, "single target mask");
        CheckRejected(single with { AffectObj = 28 }, "single affect mask");
        CheckRejected(single with { Distance = 10f }, "single distance");
        CheckRejected(single with { Range = 12f }, "single range");

        CheckRejected(area with { SkillId = 765 }, "unknown area rank");
        CheckRejected(area with { Target = 3 }, "area target mask");
        CheckRejected(area with { AffectObj = 1 }, "area affect mask");
        CheckRejected(area with { Distance = 11f }, "area distance");
        CheckRejected(area with { Range = 0f }, "area range");

        CheckRejected(single with { Property = 1 }, "non-healing property");
        CheckRejected(single with { Power1 = 0m }, "non-healing coefficient");
        CheckRejected(single with { Power2 = 0m }, "zero heal amount");
        CheckRejected(single with { Power2 = -1m }, "negative heal amount");
        CheckRejected(single with { Power2 = 1.5m }, "fractional heal amount");
        CheckRejected(
            single with { Power2 = (decimal)int.MaxValue + 1m },
            "overflowing heal amount");
    }

    private static SkillCombatDefinition GetCombat(int skillId)
    {
        Check.True(
            GameplayContentTestFixtures.Runtime.SkillCombat.TryGet(
                skillId,
                out var combat),
            $"Priest healing fixture {skillId} exists");
        return combat;
    }

    private static void CheckRejected(
        SkillCombatDefinition combat,
        string reason) =>
        Check.True(
            !PriestHealingSkillCatalog.TryResolve(combat, out _),
            $"Priest healing catalog rejects {reason}");
}
