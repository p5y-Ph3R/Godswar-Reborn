namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Evidence-only Medusa Island rules. Variant labels and the shared attempt
/// rule come from Localization/en_us/UI/Base/LuaText.lua; map identities come
/// from the captured map catalog; gameplay and HardPoint rows come from
/// https://wiki.godsarena.online/books/godsarena-godswar/page/medusa-island.
/// This policy performs no persistence, transfer, or reward mutation.
/// </summary>
internal static class MedusaIslandPolicy
{
    public const int MinimumLevel = 90;
    public const int MinimumPartySize = 1;
    public const int MaximumPartySize = 5;
    public const int DailyAttemptLimit = 1;
    public const int VictoryScore = 3_000;

    public static readonly TimeSpan TimeLimit = TimeSpan.FromMinutes(40);

    public static readonly MedusaDailyAttemptRule DailyAttempt = new(
        DailyAttemptLimit,
        MedusaDailyAttemptScope.SharedAcrossVariants);

    public const MedusaDailyPeriodBoundary DailyPeriodBoundary =
        MedusaDailyPeriodBoundary.StartupPinnedRealmCalendarAtTrustedReceipt;

    public const MedusaAttemptConsumptionPoint AttemptConsumptionPoint =
        MedusaAttemptConsumptionPoint.AfterAtomicRosterCommitBeforeRunStart;

    public static readonly MedusaRunContract RunContract = new(
        TimeLimit,
        MedusaPlayerDeathRule.RespawnAtInstanceBeginning,
        MedusaVoluntaryExitRewardRule.NoRewardWhenLeavingBeforeTimeout);

    private static readonly IReadOnlyList<MedusaIslandVariantDefinition>
        PublishedVariants =
            Array.AsReadOnly<MedusaIslandVariantDefinition>(
            [
                new(
                    MedusaIslandVariant.Advanced,
                    new(200),
                    "Medusa_Island",
                    MedusaHardPointSchedule.Enhanced),
                new(
                    MedusaIslandVariant.Normal,
                    new(204),
                    "Medusa_Island2",
                    MedusaHardPointSchedule.Normal)
            ]);

    private static readonly IReadOnlyList<MedusaHardPointScheduleDefinition>
        PublishedHardPointSchedules =
            Array.AsReadOnly<MedusaHardPointScheduleDefinition>(
            [
                Schedule(
                    MedusaHardPointSchedule.Normal,
                    incomplete:
                    [
                        new(0, 300),
                        new(950, 375),
                        new(1_200, 450),
                        new(1_500, 525),
                        new(1_700, 600),
                        new(1_900, 675),
                        new(2_200, 750)
                    ],
                    completed:
                    [
                        Completed(40, 975),
                        Completed(30, 1_050),
                        Completed(25, 1_125),
                        Completed(20, 1_200),
                        Completed(15, 1_275),
                        Completed(10, 1_350)
                    ]),
                Schedule(
                    MedusaHardPointSchedule.Enhanced,
                    incomplete:
                    [
                        new(0, 300),
                        new(950, 600),
                        new(1_200, 750),
                        new(1_500, 900),
                        new(1_700, 1_050),
                        new(1_900, 1_200),
                        new(2_200, 1_350)
                    ],
                    completed:
                    [
                        Completed(40, 1_800),
                        Completed(30, 1_950),
                        Completed(25, 2_025),
                        Completed(20, 2_100),
                        Completed(15, 2_175),
                        Completed(10, 2_250)
                    ])
            ]);

    private static readonly IReadOnlyList
        <MedusaCompletionTitleEvidenceSchedule>
        PublishedCompletionTitleEvidence =
            Array.AsReadOnly<MedusaCompletionTitleEvidenceSchedule>(
            [
                TitleSchedule(
                    MedusaCompletionTitleEvidenceSource
                        .GodsArenaGuideRevision6,
                    Title(
                        20,
                        MedusaCompletionTitle.MedusaExecutioners,
                        "Medusa Executioners"),
                    Title(
                        15,
                        MedusaCompletionTitle.MedusaSlayers,
                        "Medusa Slayers"),
                    Title(
                        10,
                        MedusaCompletionTitle.MedusaChallengers,
                        "Medusa Challengers")),
                TitleSchedule(
                    MedusaCompletionTitleEvidenceSource.StockClientLua,
                    Title(
                        20,
                        MedusaCompletionTitle.MedusaChallengers,
                        "Medusa Challengers"),
                    Title(
                        15,
                        MedusaCompletionTitle.MedusaSlayers,
                        "Medusa Slayers"),
                    Title(
                        10,
                        MedusaCompletionTitle.MedusaExecutioners,
                        "Medusa Executioners"))
            ]);

    private static readonly IReadOnlyList<MedusaUnresolvedRule>
        PublishedUnresolvedRules =
            Array.AsReadOnly<MedusaUnresolvedRule>(
            [
                MedusaUnresolvedRule.FortyMinuteBoundaryInclusivity,
                MedusaUnresolvedRule
                    .IncompleteScoreBetweenPublishedMarkers,
                MedusaUnresolvedRule
                    .CompletionTimeBetweenPublishedMarkers,
                MedusaUnresolvedRule
                    .CompletionStateVersusScoreAuthority,
                MedusaUnresolvedRule
                    .CompletionHardPointsAdditiveOrTotal,
                MedusaUnresolvedRule
                    .CompletionTitleBoundaryInclusivity,
                MedusaUnresolvedRule.CompletionTitleStacking,
                MedusaUnresolvedRule.CompletionTitleSourceConflict
            ]);

    public static IReadOnlyList<MedusaIslandVariantDefinition> Variants =>
        PublishedVariants;

    public static IReadOnlyList<MedusaHardPointScheduleDefinition>
        HardPointSchedules => PublishedHardPointSchedules;

    public static IReadOnlyList<MedusaCompletionTitleEvidenceSchedule>
        CompletionTitleEvidence => PublishedCompletionTitleEvidence;

    /// <summary>
    /// Questions the available evidence does not settle. Runtime integration
    /// must resolve these from protocol captures or an explicit design choice
    /// before applying a reward or consuming/resetting an attempt.
    /// </summary>
    public static IReadOnlyList<MedusaUnresolvedRule> UnresolvedRules =>
        PublishedUnresolvedRules;

    public static MedusaAdmissionAssessment AssessAdmission(
        IReadOnlyCollection<MedusaPartyMemberAdmissionObservation> roster)
    {
        ArgumentNullException.ThrowIfNull(roster);

        var failures = MedusaAdmissionFailure.None;
        if (roster.Count < MinimumPartySize ||
            roster.Count > MaximumPartySize)
        {
            failures |= MedusaAdmissionFailure.PartySizeOutsideRange;
        }

        foreach (var member in roster)
        {
            if (member.AttemptsUsedAcrossVariantsInResolvedDailyPeriod < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(roster),
                    "Roster attempt observations cannot be negative.");
            }
            if (member.Level < MinimumLevel)
            {
                failures |= MedusaAdmissionFailure.BelowMinimumLevel;
            }
            if (member.AttemptsUsedAcrossVariantsInResolvedDailyPeriod >=
                DailyAttemptLimit)
            {
                failures |=
                    MedusaAdmissionFailure.SharedDailyAttemptExhausted;
            }
        }

        return new(failures);
    }

    public static int ScoreForDefeat(MedusaMonsterRank rank) =>
        rank switch
        {
            MedusaMonsterRank.Normal => 1,
            MedusaMonsterRank.Elite => 50,
            MedusaMonsterRank.Boss => 500,
            _ => throw new ArgumentOutOfRangeException(nameof(rank))
        };

    public static bool HasVictoryScore(int teamScore) =>
        teamScore >= VictoryScore;

    public static bool TryGetVariant(
        MedusaIslandVariant variant,
        out MedusaIslandVariantDefinition definition)
    {
        foreach (var candidate in PublishedVariants)
        {
            if (candidate.Variant == variant)
            {
                definition = candidate;
                return true;
            }
        }

        definition = default;
        return false;
    }

    public static bool TryGetVariantByMap(
        short mapId,
        out MedusaIslandVariantDefinition definition)
    {
        foreach (var candidate in PublishedVariants)
        {
            if (candidate.MapId.Value == mapId)
            {
                definition = candidate;
                return true;
            }
        }

        definition = default;
        return false;
    }

    /// <summary>
    /// Resolves exact published evidence only. It intentionally returns false
    /// for a score between two guide markers instead of inventing a bucket.
    /// </summary>
    public static bool TryGetDocumentedIncompleteHardPoints(
        MedusaHardPointSchedule schedule,
        int exactScoreMarker,
        out int hardPoints)
    {
        if (TryGetSchedule(schedule, out var definition))
        {
            foreach (var marker in definition.IncompleteMarkers)
            {
                if (marker.Score == exactScoreMarker)
                {
                    hardPoints = marker.HardPoints;
                    return true;
                }
            }
        }

        hardPoints = default;
        return false;
    }

    /// <summary>
    /// Resolves exact published completion evidence only. It neither derives
    /// completion from score nor treats a displayed HardPoint value as an
    /// additive bonus.
    /// </summary>
    public static bool TryGetDocumentedCompletedHardPoints(
        MedusaHardPointSchedule schedule,
        int exactScoreMarker,
        TimeSpan exactCompletionTimeMarker,
        out int hardPoints)
    {
        if (TryGetSchedule(schedule, out var definition))
        {
            foreach (var marker in definition.CompletedMarkers)
            {
                if (marker.Score == exactScoreMarker &&
                    marker.CompletionTime == exactCompletionTimeMarker)
                {
                    hardPoints = marker.HardPoints;
                    return true;
                }
            }
        }

        hardPoints = default;
        return false;
    }

    /// <summary>
    /// Fails closed while the guide and stock client disagree about the 10-
    /// and 20-minute title names. A capture or explicit design selection must
    /// choose an authoritative schedule before settlement can use titles.
    /// </summary>
    public static bool TryResolveAuthoritativeCompletionTitle(
        MedusaIslandVariant variant,
        int exactScoreMarker,
        TimeSpan exactCompletionTimeMarker,
        out MedusaCompletionTitleMarker marker)
    {
        marker = default;
        return false;
    }

    private static bool TryGetSchedule(
        MedusaHardPointSchedule schedule,
        out MedusaHardPointScheduleDefinition definition)
    {
        foreach (var candidate in PublishedHardPointSchedules)
        {
            if (candidate.Schedule == schedule)
            {
                definition = candidate;
                return true;
            }
        }

        definition = null!;
        return false;
    }

    private static MedusaHardPointScheduleDefinition Schedule(
        MedusaHardPointSchedule schedule,
        MedusaIncompleteHardPointMarker[] incomplete,
        MedusaCompletedHardPointMarker[] completed) =>
        new(
            schedule,
            Array.AsReadOnly(incomplete),
            Array.AsReadOnly(completed));

    private static MedusaCompletedHardPointMarker Completed(
        int minutes,
        int hardPoints) =>
        new(VictoryScore, TimeSpan.FromMinutes(minutes), hardPoints);

    private static MedusaCompletionTitleMarker Title(
        int minutes,
        MedusaCompletionTitle title,
        string displayName) =>
        new(
            MedusaIslandVariant.Advanced,
            VictoryScore,
            TimeSpan.FromMinutes(minutes),
            title,
            displayName);

    private static MedusaCompletionTitleEvidenceSchedule TitleSchedule(
        MedusaCompletionTitleEvidenceSource source,
        params MedusaCompletionTitleMarker[] markers) =>
        new(source, Array.AsReadOnly(markers));
}
