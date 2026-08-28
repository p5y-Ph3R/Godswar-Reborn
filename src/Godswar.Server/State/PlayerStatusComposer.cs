using Godswar.Server.Packets;

namespace Godswar.Server.State;

internal sealed record ActiveRuntimeStatus(
    uint StatusId,
    int Kind,
    int Priority,
    bool Beneficial,
    DateTimeOffset ExpiresAt,
    ClientStatusAggregate Modifiers,
    long Revision,
    decimal PhysicalDamageReduction = 0m,
    decimal MagicDamageReduction = 0m,
    float MovementSpeedBonus = 0f)
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
    string Fingerprint)
{
    public IReadOnlyList<ClientStatusPresentation> Presentations
        { get; init; } = Effects
            .Select(static effect => new ClientStatusPresentation(
                effect,
                Beneficial: false,
                Priority: 0,
                ClientStatusPresentationClass.AuthoritativeBaseline))
            .ToArray();
}

internal enum ClientStatusPresentationClass : byte
{
    AuthoritativeControl = 1,
    MedusaAmplifier = 2,
    AuthoritativeBaseline = 3,
    DisplayOnly = 4
}

internal readonly record struct ClientStatusPresentation(
    ClientStatusEffect Effect,
    bool Beneficial,
    int Priority,
    ClientStatusPresentationClass PresentationClass,
    ClientStatusPresentationSource Source =
        ClientStatusPresentationSource.AuthoritativeBaseline);

internal enum ClientStatusPresentationSource : byte
{
    AuthoritativeBaseline = 0,
    Medusa = 1
}

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

        var presentations = activeExperience
            .Select(boost => new ClientStatusPresentation(
                new(
                    checked((uint)boost.StatusId),
                    boost.RemainingSeconds(now)),
                Beneficial: true,
                boost.Priority,
                ClientStatusPresentationClass.AuthoritativeBaseline))
            .Concat(activeRuntime.Select(status =>
                new ClientStatusPresentation(
                    new(
                        status.StatusId,
                        status.RemainingSeconds(now)),
                    status.Beneficial,
                    status.Priority,
                    ClientStatusPresentationClass
                        .AuthoritativeBaseline)))
            .ToArray();

        var experienceBonusBasisPoints = activeExperience
            .Where(static boost => boost.Kind != ExperienceBoostKinds.Talent)
            .Aggregate(
                0L,
                static (sum, boost) => sum + boost.BonusBasisPoints);
        var hit = activeRuntime.Aggregate(
            0L,
            static (sum, status) => sum + status.Modifiers.Hit);
        var criticalAppend = activeRuntime.Aggregate(
            0L,
            static (sum, status) => sum + status.Modifiers.CriticalAppend);
        var physicalDefense = activeRuntime.Aggregate(
            0L,
            static (sum, status) => sum + status.Modifiers.PhysicalDefense);
        var magicDefense = activeRuntime.Aggregate(
            0L,
            static (sum, status) => sum + status.Modifiers.MagicDefense);
        var dodge = activeRuntime.Aggregate(
            0L,
            static (sum, status) => sum + status.Modifiers.Dodge);
        var criticalResistance = activeRuntime.Aggregate(
            0L,
            static (sum, status) =>
                sum + status.Modifiers.CriticalResistance);
        var movementSpeedBonus = activeRuntime.Aggregate(
            0d,
            static (sum, status) => sum + status.MovementSpeedBonus);
        var isRiding = activeRuntime.Any(
            static status => status.Kind == MountCatalog.RuntimeStatusKind);
        var control = activeRuntime.Aggregate(
            HostileStatusControlFlags.None,
            static (current, status) =>
                current | status.Modifiers.Control);
        var aggregate = new ClientStatusAggregate(
            (int)Math.Clamp(hit, int.MinValue, int.MaxValue),
            (int)Math.Clamp(criticalAppend, int.MinValue, int.MaxValue),
            (float)(experienceBonusBasisPoints / 10_000d),
            (float)Math.Clamp(1d + movementSpeedBonus, 0.1d, 10d),
            isRiding,
            (int)Math.Clamp(physicalDefense, int.MinValue, int.MaxValue),
            (int)Math.Clamp(magicDefense, int.MinValue, int.MaxValue),
            (int)Math.Clamp(dodge, int.MinValue, int.MaxValue),
            (int)Math.Clamp(
                criticalResistance,
                int.MinValue,
                int.MaxValue),
            Control: control);

        // Remaining seconds intentionally do not participate. Otherwise the
        // periodic reconciliation loop would resend an unchanged status set.
        var experienceFingerprint = string.Join(
            '|',
            activeExperience.Select(static boost =>
                $"exp:{boost.StatusId}:{boost.Kind}:{boost.BonusBasisPoints}:{boost.Priority}:" +
                boost.Source));
        var runtimeFingerprint = string.Join(
            '|',
            activeRuntime.Select(static status =>
                $"runtime:{status.StatusId}:{status.Kind}:{status.Priority}:{status.Beneficial}:" +
                $"{status.ExpiresAt.UtcTicks}:{status.Modifiers.Hit}:" +
                $"{status.Modifiers.CriticalAppend}:" +
                $"{status.Modifiers.PhysicalDefense}:" +
                $"{status.Modifiers.MagicDefense}:" +
                $"{status.Modifiers.Dodge}:" +
                $"{status.Modifiers.CriticalResistance}:" +
                $"{status.Modifiers.Control}:" +
                $"{status.PhysicalDamageReduction}:" +
                $"{status.MagicDamageReduction}:{status.MovementSpeedBonus:R}:" +
                $"{status.Revision}"));
        var fingerprint = $"{experienceFingerprint}#{runtimeFingerprint}#" +
            $"{aggregate.Hit}:{aggregate.CriticalAppend}:{aggregate.ExperienceBonus:R}:" +
            $"{aggregate.MovementSpeedMultiplier:R}:{aggregate.IsRiding}:" +
            $"{aggregate.PhysicalDefense}:{aggregate.MagicDefense}:" +
            $"{aggregate.Dodge}:{aggregate.CriticalResistance}:" +
            $"{aggregate.Control}";

        return PlayerStatusCapacityPolicy.Apply(
            new PlayerStatusSnapshot([], aggregate, fingerprint)
            {
                Presentations = presentations
            });
    }
}
