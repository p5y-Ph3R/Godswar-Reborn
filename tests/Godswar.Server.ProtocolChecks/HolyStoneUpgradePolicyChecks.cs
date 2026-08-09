using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class HolyStoneUpgradePolicyChecks
{
    public const string CheckName =
        "Authoritative Holy Stone Upgrade policy";

    public static Task RunAsync()
    {
        AssertEclipseTiersAndRates();
        AssertGoddessBonusKeepsDowngrade();
        AssertExactSignetsProtectFailure();
        AssertHighLevelSignetsAreUnavailable();
        AssertOutcomeBoundariesAndConsumption();
        AssertZephyrUsesSharedUpgradeRules();
        return Task.CompletedTask;
    }

    private static void AssertEclipseTiersAndRates()
    {
        foreach (var (level, eclipseId, rate) in new[]
                 {
                     (1, 9040u, 90), (2, 9040u, 90), (3, 9040u, 90),
                     (4, 9041u, 25), (5, 9041u, 25), (6, 9041u, 25),
                     (7, 9042u, 10), (8, 9042u, 10), (9, 9042u, 10)
                 })
        {
            var failure = HolyStoneUpgradePolicy.TryPrepare(
                Item(9030, checked((short)level)),
                Item(eclipseId),
                CompactItemEntry.Empty,
                out var attempt);
            Check.True(
                failure == HolyStoneUpgradeEligibilityFailure.None &&
                attempt.RequiredEclipseStoneId == eclipseId &&
                attempt.SuccessRatePercent == rate &&
                !attempt.PreventsDowngrade,
                $"level {level} uses Eclipse {eclipseId} at {rate}%");
        }

        Check.Equal(
            (int)HolyStoneUpgradeEligibilityFailure.MaximumLevel,
            (int)HolyStoneUpgradePolicy.TryPrepare(
                Item(9031, 10),
                Item(9042),
                CompactItemEntry.Empty,
                out _),
            "level 10 is the authoritative maximum");
        Check.Equal(
            (int)HolyStoneUpgradeEligibilityFailure.EclipseStone,
            (int)HolyStoneUpgradePolicy.TryPrepare(
                Item(9030, 4),
                Item(9040),
                CompactItemEntry.Empty,
                out _),
            "wrong Eclipse tier is rejected before randomness");
    }

    private static void AssertGoddessBonusKeepsDowngrade()
    {
        var target = Item(9031, 8);
        var eclipse = Item(9042, stack: 3);
        var goddess = Item(9050, stack: 2);
        Check.Equal(
            (int)HolyStoneUpgradeEligibilityFailure.None,
            (int)HolyStoneUpgradePolicy.TryPrepare(
                target,
                eclipse,
                goddess,
                out var attempt),
            "Goddess Stone is a valid catalyst");
        Check.True(
            attempt.SuccessRatePercent == 20 &&
            !attempt.PreventsDowngrade,
            "Goddess Stone adds exactly 10 points without protection");
        var failed = attempt.Resolve(target, eclipse, goddess, roll: 20);
        Check.True(
            failed.Outcome ==
                HolyStoneUpgradeOutcome.FailedDowngraded &&
            failed.TargetAfter.Grade == 7 &&
            failed.EclipseStoneAfter.Stack == 2 &&
            failed.CatalystAfter.Stack == 1,
            "Goddess failure downgrades and consumes both materials");
    }

    private static void AssertExactSignetsProtectFailure()
    {
        for (short level = 4; level <= 6; level++)
        {
            var eclipseId =
                HolyStoneUpgradePolicy.RequiredEclipseStone(level);
            var signetId = HolyStoneUpgradePolicy.RequiredSignet(level);
            var target = Item(9030, level);
            var eclipse = Item(eclipseId);
            var signet = Item(signetId, stack: 2);
            Check.Equal(
                (int)HolyStoneUpgradeEligibilityFailure.None,
                (int)HolyStoneUpgradePolicy.TryPrepare(
                    target,
                    eclipse,
                    signet,
                    out var attempt),
                $"signet {signetId} matches {level}->{level + 1}");
            var failed = attempt.Resolve(
                target,
                eclipse,
                signet,
                roll: attempt.SuccessRatePercent);
            Check.True(
                attempt.PreventsDowngrade &&
                failed.Outcome ==
                    HolyStoneUpgradeOutcome.FailedProtected &&
                failed.TargetAfter.Grade == level &&
                failed.EclipseStoneAfter.IsEmpty &&
                failed.CatalystAfter.Stack == 1,
                $"signet {signetId} adds 10 points and protects failure");
        }

        Check.Equal(
            (int)HolyStoneUpgradeEligibilityFailure.SignetTransition,
            (int)HolyStoneUpgradePolicy.TryPrepare(
                Item(9030, 6),
                Item(9041),
                Item(9052),
                out _),
            "a signet for another exact transition is rejected");
    }

    private static void AssertHighLevelSignetsAreUnavailable()
    {
        for (short level = 7; level <= 9; level++)
        {
            var target = Item(9030, level);
            var eclipse = Item(9042);
            var legacySignet = Item(
                checked((uint)(9054 + level - 7)));
            Check.Equal(
                0u,
                HolyStoneUpgradePolicy.RequiredSignet(level),
                $"level {level} has no protective signet");
            Check.Equal(
                (int)HolyStoneUpgradeEligibilityFailure
                    .SignetProtectionUnavailable,
                (int)HolyStoneUpgradePolicy.TryPrepare(
                    target,
                    eclipse,
                    legacySignet,
                    out _),
                $"level {level} rejects every legacy high-level signet");

            Check.Equal(
                (int)HolyStoneUpgradeEligibilityFailure.None,
                (int)HolyStoneUpgradePolicy.TryPrepare(
                    target,
                    eclipse,
                    CompactItemEntry.Empty,
                    out var riskyAttempt),
                $"level {level} still permits an unprotected upgrade");
            var failed = riskyAttempt.Resolve(
                target,
                eclipse,
                CompactItemEntry.Empty,
                roll: 10);
            Check.True(
                failed.Outcome ==
                    HolyStoneUpgradeOutcome.FailedDowngraded &&
                failed.TargetAfter.Grade == level - 1,
                $"level {level} failure remains a rollback risk");
        }
    }

    private static void AssertOutcomeBoundariesAndConsumption()
    {
        var target = Item(9030, 3) with
        {
            Attribute1 = 17,
            Bound = 1
        };
        var eclipse = Item(9040, stack: 2);
        HolyStoneUpgradePolicy.TryPrepare(
            target,
            eclipse,
            CompactItemEntry.Empty,
            out var attempt);
        var success = attempt.Resolve(target, eclipse,
            CompactItemEntry.Empty, roll: 89);
        var failure = attempt.Resolve(target, eclipse,
            CompactItemEntry.Empty, roll: 90);
        Check.True(
            success.Outcome == HolyStoneUpgradeOutcome.Succeeded &&
            success.TargetAfter.Grade == 4 &&
            success.TargetAfter.Attribute1 == 17 &&
            success.TargetAfter.Bound == 1 &&
            success.EclipseStoneAfter.Stack == 1,
            "rate-minus-one succeeds and preserves unrelated target state");
        Check.True(
            failure.Outcome ==
                HolyStoneUpgradeOutcome.FailedDowngraded &&
            failure.TargetAfter.Grade == 2,
            "roll equal to rate fails and downgrades");

        HolyStoneUpgradePolicy.TryPrepare(
            Item(9030, 1),
            Item(9040),
            CompactItemEntry.Empty,
            out var levelOne);
        var floorFailure = levelOne.Resolve(
            Item(9030, 1),
            Item(9040),
            CompactItemEntry.Empty,
            roll: 99);
        Check.True(
            floorFailure.Outcome ==
                HolyStoneUpgradeOutcome.FailedProtected &&
            floorFailure.TargetAfter.Grade == 1,
            "level-one failure reports no-downgrade outcome at the floor");
    }

    private static void AssertZephyrUsesSharedUpgradeRules()
    {
        Check.True(
            HolyStoneUpgradePolicy.IsHolyStone(9032) &&
            HolyStoneCombinationPolicy.IsHolyStone(9032),
            "Zephyr is a native Holy Stone across upgrade policies");
        var failure = HolyStoneUpgradePolicy.TryPrepare(
            Item(9032, 4),
            Item(9041),
            Item(9051),
            out var attempt);
        Check.True(
            failure == HolyStoneUpgradeEligibilityFailure.None &&
            attempt.SuccessRatePercent == 35 &&
            attempt.PreventsDowngrade,
            "Zephyr uses the reviewed Eclipse and signet rules");
    }

    private static CompactItemEntry Item(
        uint id,
        short grade = 1,
        short stack = 1) =>
        CompactItemEntry.Empty with
        {
            Id = id,
            Quality = 1,
            Grade = grade,
            Stack = stack
        };
}
