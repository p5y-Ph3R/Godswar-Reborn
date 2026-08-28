using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal enum MedusaIslandVariant : byte
{
    Advanced = 1,
    Normal = 2
}

/// <summary>
/// The stock client calls the harder variant "Advanced" while the published
/// reward table calls its corresponding schedule "Enhanced". Keeping both
/// names prevents either evidence source from being silently rewritten.
/// </summary>
internal enum MedusaHardPointSchedule : byte
{
    Enhanced = 1,
    Normal = 2
}

internal enum MedusaMonsterRank : byte
{
    Normal = 1,
    Elite = 2,
    Boss = 3
}

internal enum MedusaDailyAttemptScope : byte
{
    SharedAcrossVariants = 1
}

internal enum MedusaPlayerDeathRule : byte
{
    RespawnAtInstanceBeginning = 1
}

internal enum MedusaVoluntaryExitRewardRule : byte
{
    NoRewardWhenLeavingBeforeTimeout = 1
}

internal enum MedusaCompletionTitle : byte
{
    MedusaChallengers = 1,
    MedusaSlayers = 2,
    MedusaExecutioners = 3
}

internal enum MedusaCompletionTitleEvidenceSource : byte
{
    GodsArenaGuideRevision6 = 1,
    StockClientLua = 2
}

[Flags]
internal enum MedusaAdmissionFailure : byte
{
    None = 0,
    BelowMinimumLevel = 1 << 0,
    PartySizeOutsideRange = 1 << 1,
    SharedDailyAttemptExhausted = 1 << 2
}

internal enum MedusaUnresolvedRule : byte
{
    DailyPeriodBoundary = 1,
    AttemptConsumptionPoint = 2,
    FortyMinuteBoundaryInclusivity = 3,
    IncompleteScoreBetweenPublishedMarkers = 4,
    CompletionTimeBetweenPublishedMarkers = 5,
    CompletionStateVersusScoreAuthority = 6,
    CompletionHardPointsAdditiveOrTotal = 7,
    CompletionTitleBoundaryInclusivity = 8,
    CompletionTitleStacking = 9,
    CompletionTitleSourceConflict = 10
}

internal enum MedusaDailyPeriodBoundary : byte
{
    StartupPinnedRealmCalendarAtTrustedReceipt = 1
}

internal enum MedusaAttemptConsumptionPoint : byte
{
    AfterAtomicRosterCommitBeforeRunStart = 1
}

internal readonly record struct MedusaIslandVariantDefinition(
    MedusaIslandVariant Variant,
    MapId MapId,
    string SceneKey,
    MedusaHardPointSchedule HardPointSchedule);

internal readonly record struct MedusaDailyAttemptRule(
    int Limit,
    MedusaDailyAttemptScope Scope);

internal readonly record struct MedusaRunContract(
    TimeSpan TimeLimit,
    MedusaPlayerDeathRule PlayerDeath,
    MedusaVoluntaryExitRewardRule VoluntaryExitBeforeTimeout);

/// <summary>
/// Admission evidence for one roster member. Attempts are the member's total
/// across both variants in a daily period already resolved by the caller.
/// </summary>
internal readonly record struct MedusaPartyMemberAdmissionObservation(
    int Level,
    int AttemptsUsedAcrossVariantsInResolvedDailyPeriod);

internal readonly record struct MedusaAdmissionAssessment(
    MedusaAdmissionFailure Failures)
{
    public bool IsEligible => Failures == MedusaAdmissionFailure.None;

    public bool HasFailure(MedusaAdmissionFailure failure) =>
        failure != MedusaAdmissionFailure.None &&
        (Failures & failure) == failure;
}

/// <summary>
/// One exact score/HardPoint marker from the guide's Incomplete rows. This is
/// deliberately not a range: the guide does not publish how scores between
/// adjacent markers are resolved.
/// </summary>
internal readonly record struct MedusaIncompleteHardPointMarker(
    int Score,
    int HardPoints);

/// <summary>
/// One exact completion marker from the guide. Completion, the 3,000 score,
/// and the displayed time are retained independently so later runtime work
/// does not have to infer one from another.
/// </summary>
internal readonly record struct MedusaCompletedHardPointMarker(
    int Score,
    TimeSpan CompletionTime,
    int HardPoints);

/// <summary>
/// One exact Advanced-variant title marker retained separately from the
/// HardPoint table. The evidence does not establish boundary or stacking
/// behavior for times that qualify for more than one displayed marker.
/// </summary>
internal readonly record struct MedusaCompletionTitleMarker(
    MedusaIslandVariant Variant,
    int Score,
    TimeSpan CompletionTime,
    MedusaCompletionTitle Title,
    string DisplayName);

internal sealed record MedusaCompletionTitleEvidenceSchedule(
    MedusaCompletionTitleEvidenceSource Source,
    IReadOnlyList<MedusaCompletionTitleMarker> Markers);

internal sealed record MedusaHardPointScheduleDefinition(
    MedusaHardPointSchedule Schedule,
    IReadOnlyList<MedusaIncompleteHardPointMarker> IncompleteMarkers,
    IReadOnlyList<MedusaCompletedHardPointMarker> CompletedMarkers);
