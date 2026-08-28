using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaIslandPolicyChecks
{
    public const string CheckName =
        "Medusa Island variants, admission, and scoring policy";

    public static Task RunAsync()
    {
        CheckVariants();
        CheckAdmission();
        CheckSharedAttemptRule();
        CheckRunContract();
        CheckScoringAndVictory();
        CheckUnresolvedBoundaries();
        return Task.CompletedTask;
    }

    private static void CheckVariants()
    {
        Check.Equal(2, MedusaIslandPolicy.Variants.Count,
            "exactly two Medusa variants are published");
        Check.True(
            MedusaIslandPolicy.TryGetVariant(
                MedusaIslandVariant.Advanced,
                out var advanced) &&
            advanced.MapId.Value == 200 &&
            advanced.SceneKey == "Medusa_Island" &&
            advanced.HardPointSchedule ==
                MedusaHardPointSchedule.Enhanced,
            "client Advanced maps to map 200 and the Enhanced guide table");
        Check.True(
            MedusaIslandPolicy.TryGetVariant(
                MedusaIslandVariant.Normal,
                out var normal) &&
            normal.MapId.Value == 204 &&
            normal.SceneKey == "Medusa_Island2" &&
            normal.HardPointSchedule == MedusaHardPointSchedule.Normal,
            "client Normal maps to map 204 and the Normal guide table");
        Check.True(
            MedusaIslandPolicy.TryGetVariantByMap(200, out advanced) &&
            advanced.Variant == MedusaIslandVariant.Advanced &&
            MedusaIslandPolicy.TryGetVariantByMap(204, out normal) &&
            normal.Variant == MedusaIslandVariant.Normal &&
            !MedusaIslandPolicy.TryGetVariantByMap(203, out _) &&
            !MedusaIslandPolicy.TryGetVariant(
                (MedusaIslandVariant)byte.MaxValue,
                out _),
            "only the two reviewed map and variant identities resolve");
    }

    private static void CheckAdmission()
    {
        Check.True(
            MedusaIslandPolicy.MinimumLevel == 90 &&
            MedusaIslandPolicy.MinimumPartySize == 1 &&
            MedusaIslandPolicy.MaximumPartySize == 5,
            "Medusa admits level 90+ groups of one through five");
        Check.True(
            MedusaIslandPolicy.AssessAdmission([Member(90)]).IsEligible &&
            MedusaIslandPolicy.AssessAdmission(
                [Member(90), Member(110), Member(150), Member(190), Member(200)])
                .IsEligible,
            "both documented admission boundaries are inclusive");

        var belowLevel =
            MedusaIslandPolicy.AssessAdmission([Member(100), Member(89)]);
        var partyTooSmall =
            MedusaIslandPolicy.AssessAdmission([]);
        var partyTooLarge =
            MedusaIslandPolicy.AssessAdmission(
            [
                Member(90),
                Member(90),
                Member(90),
                Member(90),
                Member(90),
                Member(90)
            ]);
        Check.True(
            belowLevel.HasFailure(
                MedusaAdmissionFailure.BelowMinimumLevel) &&
            partyTooSmall.HasFailure(
                MedusaAdmissionFailure.PartySizeOutsideRange) &&
            partyTooLarge.HasFailure(
                MedusaAdmissionFailure.PartySizeOutsideRange),
            "level and party-size failures retain their distinct evidence");

        var combined =
            MedusaIslandPolicy.AssessAdmission(
            [
                Member(90),
                Member(89),
                Member(90),
                Member(90),
                Member(90, attempts: 1),
                Member(90)
            ]);
        Check.True(
            !combined.IsEligible &&
            combined.HasFailure(
                MedusaAdmissionFailure.BelowMinimumLevel) &&
            combined.HasFailure(
                MedusaAdmissionFailure.PartySizeOutsideRange) &&
            combined.HasFailure(
                MedusaAdmissionFailure.SharedDailyAttemptExhausted),
            "admission reports every failure without inventing precedence");
        Check.Throws<ArgumentOutOfRangeException>(
            () => MedusaIslandPolicy.AssessAdmission([Member(90, -1)]),
            "attempt observations cannot be negative");
    }

    private static void CheckSharedAttemptRule()
    {
        Check.True(
            MedusaIslandPolicy.DailyAttempt.Limit == 1 &&
            MedusaIslandPolicy.DailyAttempt.Scope ==
                MedusaDailyAttemptScope.SharedAcrossVariants &&
            MedusaIslandPolicy.AssessAdmission([Member(90)]).IsEligible &&
            MedusaIslandPolicy.AssessAdmission(
                [Member(90), Member(90, attempts: 1)]).HasFailure(
                MedusaAdmissionFailure.SharedDailyAttemptExhausted),
            "any member's Advanced or Normal use exhausts party admission");
    }

    private static void CheckRunContract()
    {
        Check.True(
            MedusaIslandPolicy.RunContract.TimeLimit ==
                TimeSpan.FromMinutes(40) &&
            MedusaIslandPolicy.RunContract.PlayerDeath ==
                MedusaPlayerDeathRule.RespawnAtInstanceBeginning &&
            MedusaIslandPolicy.RunContract.VoluntaryExitBeforeTimeout ==
                MedusaVoluntaryExitRewardRule
                    .NoRewardWhenLeavingBeforeTimeout,
            "death, voluntary exit, and timeout retain the guide contract");
    }

    private static void CheckScoringAndVictory()
    {
        Check.True(
            MedusaIslandPolicy.ScoreForDefeat(
                MedusaMonsterRank.Normal) == 1 &&
            MedusaIslandPolicy.ScoreForDefeat(
                MedusaMonsterRank.Elite) == 50 &&
            MedusaIslandPolicy.ScoreForDefeat(
                MedusaMonsterRank.Boss) == 500,
            "normal, elite, and boss defeats retain their team scores");
        Check.Throws<ArgumentOutOfRangeException>(
            () => MedusaIslandPolicy.ScoreForDefeat(
                (MedusaMonsterRank)byte.MaxValue),
            "unknown monster ranks cannot award inferred points");
        Check.True(
            !MedusaIslandPolicy.HasVictoryScore(2_999) &&
            MedusaIslandPolicy.HasVictoryScore(3_000) &&
            MedusaIslandPolicy.HasVictoryScore(3_500),
            "the team wins when it reaches the 3,000-point target");
        Check.Equal(
            TimeSpan.FromMinutes(40),
            MedusaIslandPolicy.TimeLimit,
            "the documented run duration is forty minutes");
    }

    private static void CheckUnresolvedBoundaries()
    {
        var expected = new[]
        {
            MedusaUnresolvedRule.FortyMinuteBoundaryInclusivity,
            MedusaUnresolvedRule.IncompleteScoreBetweenPublishedMarkers,
            MedusaUnresolvedRule.CompletionTimeBetweenPublishedMarkers,
            MedusaUnresolvedRule.CompletionStateVersusScoreAuthority,
            MedusaUnresolvedRule.CompletionHardPointsAdditiveOrTotal,
            MedusaUnresolvedRule.CompletionTitleBoundaryInclusivity,
            MedusaUnresolvedRule.CompletionTitleStacking,
            MedusaUnresolvedRule.CompletionTitleSourceConflict
        };
        Check.True(
            MedusaIslandPolicy.UnresolvedRules.SequenceEqual(expected),
            "undocumented reset, boundary, and reward semantics stay explicit");
        Check.True(
            MedusaIslandPolicy.DailyPeriodBoundary ==
                MedusaDailyPeriodBoundary
                    .StartupPinnedRealmCalendarAtTrustedReceipt &&
            MedusaIslandPolicy.AttemptConsumptionPoint ==
                MedusaAttemptConsumptionPoint
                    .AfterAtomicRosterCommitBeforeRunStart,
            "daily day and attempt consumption use the authored durable saga boundary");
    }

    private static MedusaPartyMemberAdmissionObservation Member(
        int level,
        int attempts = 0) =>
        new(level, attempts);
}
