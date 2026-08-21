using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.ProtocolChecks;

internal static class HostileStatusProcChecks
{
    public static void Run()
    {
        CheckNeutralAndContestedChance();
        CheckDeterministicStageIsolation();
        CheckFixedDurationAndContractValidation();
    }

    private static void CheckNeutralAndContestedChance()
    {
        var neutral = Resolve(
            hit: 500,
            dodge: 500,
            statusHit: 20,
            resistance: 20,
            eventId: 1);
        var favorable = Resolve(
            hit: 1_500,
            dodge: 100,
            statusHit: 400,
            resistance: 50,
            eventId: 1);
        var unfavorable = Resolve(
            hit: 100,
            dodge: 1_500,
            statusHit: 50,
            resistance: 400,
            eventId: 1);
        var sanitized = Resolve(
            hit: -1,
            dodge: -2,
            statusHit: -3,
            resistance: -4,
            eventId: 1);
        Check.True(
            neutral.ChanceBasisPoints == 5_000 &&
            favorable.ChanceBasisPoints > neutral.ChanceBasisPoints &&
            unfavorable.ChanceBasisPoints < neutral.ChanceBasisPoints &&
            favorable.ChanceBasisPoints +
                unfavorable.ChanceBasisPoints == 10_000 &&
            sanitized.ChanceBasisPoints == 5_000,
            "hostile status starts at 50 percent and contests landing ratings symmetrically");
        Check.True(
            favorable.ChanceBasisPoints is >= 500 and <= 9_500 &&
            unfavorable.ChanceBasisPoints is >= 500 and <= 9_500,
            "hostile status chance stays inside the authored 5-to-95-percent bounds");

        var hitBuff = Resolve(560, 500, 20, 20, 1);
        var dodgeBuff = Resolve(500, 560, 20, 20, 1);
        var statusTalent = Resolve(500, 500, 80, 20, 1);
        var resistanceTalent = Resolve(500, 500, 20, 80, 1);
        Check.True(
            hitBuff.ChanceBasisPoints > neutral.ChanceBasisPoints &&
            dodgeBuff.ChanceBasisPoints < neutral.ChanceBasisPoints &&
            statusTalent.ChanceBasisPoints > neutral.ChanceBasisPoints &&
            resistanceTalent.ChanceBasisPoints < neutral.ChanceBasisPoints,
            "Hit/StatusHit raise and Dodge/StatusResistance lower status landing");
    }

    private static void CheckDeterministicStageIsolation()
    {
        var first = Resolve(900, 700, 50, 25, 0x11223344UL, 3);
        var replay = Resolve(900, 700, 50, 25, 0x11223344UL, 3);
        var hitRoll = DeterministicCombatRandom.RollBasisPoints(
            0x11223344UL,
            3,
            CombatRandomStage.Hit);
        var criticalRoll = DeterministicCombatRandom.RollBasisPoints(
            0x11223344UL,
            3,
            CombatRandomStage.Critical);
        Check.Equal(
            first,
            replay,
            "hostile status proc evidence is replay deterministic");
        Check.True(
            first.RollBasisPoints != hitRoll &&
            first.RollBasisPoints != criticalRoll,
            "status proc uses an independent deterministic random stage");

        var threw = false;
        try
        {
            _ = Resolve(0, 0, 0, 0, 1, -1);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }
        Check.True(threw, "negative status target order fails closed");
    }

    private static void CheckFixedDurationAndContractValidation()
    {
        var now = DateTimeOffset.Parse("2026-08-17T10:00:00Z");
        var duration = TimeSpan.FromSeconds(20);
        Check.Equal(
            now + duration,
            HostileStatusDurationPolicy.ResolveExpiry(now, duration),
            "duration ratings never extend the stock hostile-status timer");

        Check.True(
            TrainingDummyHostileStatusSkillCatalog.TryGet(
                334,
                out var injury) &&
            injury.NativeStatusOddsRating == 300 &&
            injury.Duration == duration &&
            injury.PhysicalDamageTakenIncreaseBasisPoints == 1_500,
            "Meteor Injury preserves native odds as metadata and stock 20-second duration");
        injury.Validate();
    }

    private static HostileStatusProcDecision Resolve(
        int hit,
        int dodge,
        int statusHit,
        int resistance,
        ulong eventId,
        int targetOrder = 0) =>
        HostileStatusProcPolicy.Evaluate(
            new HostileStatusProcRatings(
                AttackerLevel: 160,
                TargetLevel: 160,
                EffectiveAttackerHit: hit,
                EffectiveTargetDodge: dodge,
                AttackerStatusHit: statusHit,
                TargetStatusResistance: resistance),
            eventId,
            targetOrder);
}
