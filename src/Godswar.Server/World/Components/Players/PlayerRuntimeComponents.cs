using System.Collections.Immutable;
using Godswar.Server.State;

namespace Godswar.Server.World.Components.Players;

/// <summary>
/// Wall-clock observation supplied by a session adapter. The simulation clock
/// only accepts forward observations, so stale session flushes cannot rewind
/// player runtime state.
/// </summary>
internal readonly record struct PlayerRuntimeTimeSourceComponent(
    DateTimeOffset ObservedAt);

internal struct PlayerRuntimeClockComponent
{
    public PlayerRuntimeClockComponent(DateTimeOffset currentAt)
    {
        CurrentAt = currentAt;
    }

    public DateTimeOffset CurrentAt;
}

/// <summary>
/// Recovery values copied from the level/profession catalog and the immutable
/// calculated-stat projection at the ECS boundary.
/// </summary>
internal readonly record struct PlayerRecoverySourceComponent
{
    private PlayerRecoverySourceComponent(int hpPerPulse, int mpPerPulse)
    {
        HpPerPulse = hpPerPulse;
        MpPerPulse = mpPerPulse;
    }

    public int HpPerPulse { get; }

    public int MpPerPulse { get; }

    public static PlayerRecoverySourceComponent Create(
        int level,
        byte profession,
        int bonusHpRecovery,
        int bonusMpRecovery)
    {
        return new PlayerRecoverySourceComponent(
            AddRecovery(
                PlayerRecoveryCatalog.GetBaseHp(level, profession),
                bonusHpRecovery),
            AddRecovery(
                PlayerRecoveryCatalog.GetBaseMp(level, profession),
                bonusMpRecovery));
    }

    private static int AddRecovery(int baseRecovery, int bonusRecovery) =>
        (int)Math.Min(
            int.MaxValue,
            (long)baseRecovery + Math.Max(0, bonusRecovery));
}

internal struct PlayerRecoveryTimerComponent
{
    public PlayerRecoveryTimerComponent(DateTimeOffset nextPulseAt)
    {
        NextPulseAt = nextPulseAt;
        PulsesObserved = 0;
    }

    public DateTimeOffset NextPulseAt;

    public long PulsesObserved;
}

/// <summary>
/// Immutable status inputs. Runtime statuses remain one-per-kind at the
/// session boundary, matching GameSessionRegistry's replacement semantics.
/// </summary>
internal readonly struct PlayerStatusSourceComponent
{
    public PlayerStatusSourceComponent(
        ImmutableArray<ActiveExperienceBoost> experienceBoosts,
        ImmutableArray<ActiveRuntimeStatus> runtimeStatuses)
    {
        ExperienceBoosts = experienceBoosts.IsDefault
            ? ImmutableArray<ActiveExperienceBoost>.Empty
            : experienceBoosts;
        RuntimeStatuses = runtimeStatuses.IsDefault
            ? ImmutableArray<ActiveRuntimeStatus>.Empty
            : runtimeStatuses;
    }

    public ImmutableArray<ActiveExperienceBoost> ExperienceBoosts { get; }

    public ImmutableArray<ActiveRuntimeStatus> RuntimeStatuses { get; }

    public PlayerStatusSourceComponent WithRuntimeStatuses(
        ImmutableArray<ActiveRuntimeStatus> runtimeStatuses) =>
        new(ExperienceBoosts, runtimeStatuses);
}

internal struct PlayerStatusTimerComponent
{
    public PlayerStatusTimerComponent(DateTimeOffset evaluatedAt)
    {
        LastEvaluatedAt = evaluatedAt;
        NextExpiryAt = null;
        Evaluations = 0;
    }

    public DateTimeOffset LastEvaluatedAt;

    public DateTimeOffset? NextExpiryAt;

    public long Evaluations;
}

internal readonly record struct PlayerComposedStatusEffect(
    uint StatusId,
    uint RemainingSeconds);

/// <summary>
/// Transport-neutral status projection. A later boundary adapter may translate
/// these values to the original-client packet model.
/// </summary>
internal readonly record struct PlayerComposedStatusComponent(
    ImmutableArray<PlayerComposedStatusEffect> Effects,
    ClientStatusAggregate Aggregate,
    string Fingerprint);

[Flags]
internal enum PlayerOnlineDurationTarget : byte
{
    None = 0,
    ProgressionBoosts = 1,
    Zodiac = 2
}

/// <summary>
/// Independent persistence watermarks for policies that consume online time.
/// A null watermark means that policy is not running for this world session.
/// </summary>
internal struct PlayerOnlineDurationClocksComponent
{
    public PlayerOnlineDurationClocksComponent(
        DateTimeOffset? progressionLastAccountedAt,
        DateTimeOffset? zodiacLastAccountedAt)
    {
        ProgressionLastAccountedAt = progressionLastAccountedAt;
        ZodiacLastAccountedAt = zodiacLastAccountedAt;
        ProgressionElapsedTicks = 0;
        ZodiacElapsedTicks = 0;
    }

    public DateTimeOffset? ProgressionLastAccountedAt;

    public DateTimeOffset? ZodiacLastAccountedAt;

    public long ProgressionElapsedTicks;

    public long ZodiacElapsedTicks;
}
