using Godswar.Server.Packets;

namespace Godswar.Server.State;

internal sealed record ActiveRuntimeStatus(
    uint StatusId,
    int Kind,
    int Priority,
    bool Beneficial,
    DateTimeOffset ExpiresAt,
    ClientStatusAggregate Modifiers,
    long Revision)
{
    public uint RemainingSeconds(DateTimeOffset now) =>
        (uint)Math.Clamp(
            (long)Math.Ceiling((ExpiresAt - now).TotalSeconds),
            0L,
            uint.MaxValue);
}

internal sealed record PlayerStatusSnapshot(
    IReadOnlyList<ClientStatusEffect> Effects,
    ClientStatusAggregate Aggregate,
    string Fingerprint);

/// <summary>
/// Composes MSG_STATUS as a complete replacement snapshot. The original server
/// never sent status deltas, so every producer must merge its source layer with
/// all other active sources before publishing opcode 10167.
/// </summary>
internal static class PlayerStatusComposer
{
    internal const int MaximumBeneficialStatuses = 10;
    internal const int MaximumTotalStatuses = 20;

    public static PlayerStatusSnapshot Compose(
        ExperienceBoostState experienceBoosts,
        IEnumerable<ActiveRuntimeStatus> runtimeStatuses,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(experienceBoosts);
        ArgumentNullException.ThrowIfNull(runtimeStatuses);

        var activeExperience = experienceBoosts.ActiveBoosts
            .Where(boost => boost.ExpiresAt is null || boost.ExpiresAt > now)
            .OrderBy(static boost => boost.StatusId)
            .ToArray();
        var activeRuntime = runtimeStatuses
            .Where(status => status.ExpiresAt > now)
            .OrderBy(static status => status.StatusId)
            .ToArray();

        var beneficialCount = activeExperience.Length +
            activeRuntime.Count(static status => status.Beneficial);
        var totalCount = activeExperience.Length + activeRuntime.Length;
        if (beneficialCount > MaximumBeneficialStatuses)
        {
            throw new InvalidOperationException(
                $"The client supports at most {MaximumBeneficialStatuses} beneficial statuses.");
        }

        if (totalCount > MaximumTotalStatuses)
        {
            throw new InvalidOperationException(
                $"The client supports at most {MaximumTotalStatuses} total statuses.");
        }

        var effects = activeExperience
            .Select(boost => new ClientStatusEffect(
                checked((uint)boost.StatusId),
                boost.RemainingSeconds(now)))
            .Concat(activeRuntime.Select(status => new ClientStatusEffect(
                status.StatusId,
                status.RemainingSeconds(now))))
            .OrderBy(static effect => effect.StatusId)
            .ToArray();

        var experienceBonusBasisPoints = activeExperience.Aggregate(
            0L,
            static (sum, boost) => sum + boost.BonusBasisPoints);
        var hit = activeRuntime.Aggregate(
            0L,
            static (sum, status) => sum + status.Modifiers.Hit);
        var criticalAppend = activeRuntime.Aggregate(
            0L,
            static (sum, status) => sum + status.Modifiers.CriticalAppend);
        var aggregate = new ClientStatusAggregate(
            (int)Math.Clamp(hit, int.MinValue, int.MaxValue),
            (int)Math.Clamp(criticalAppend, int.MinValue, int.MaxValue),
            (float)(experienceBonusBasisPoints / 10_000d));

        // Remaining seconds intentionally do not participate. Otherwise the
        // periodic reconciliation loop would resend an unchanged status set.
        var experienceFingerprint = string.Join(
            '|',
            activeExperience.Select(static boost =>
                $"exp:{boost.StatusId}:{boost.Kind}:{boost.BonusBasisPoints}:{boost.Priority}:" +
                $"{boost.ExpiresAt?.UtcTicks ?? long.MaxValue}:{boost.Source}"));
        var runtimeFingerprint = string.Join(
            '|',
            activeRuntime.Select(static status =>
                $"runtime:{status.StatusId}:{status.Kind}:{status.Priority}:{status.Beneficial}:" +
                $"{status.ExpiresAt.UtcTicks}:{status.Modifiers.Hit}:" +
                $"{status.Modifiers.CriticalAppend}:{status.Revision}"));
        var fingerprint = $"{experienceFingerprint}#{runtimeFingerprint}#" +
            $"{aggregate.Hit}:{aggregate.CriticalAppend}:{aggregate.ExperienceBonus:R}";

        return new PlayerStatusSnapshot(effects, aggregate, fingerprint);
    }
}
