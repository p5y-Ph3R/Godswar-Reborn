using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game.WorldInstances;

internal sealed partial class MedusaEncounterMechanicsRuntime
{
    /// <summary>
    /// Purely previews an active utility-carrier amplifier over an already
    /// resolved typed outgoing hit. It never observes time, expires effects,
    /// or consumes periodic ticks. Physical and magical effects never cross
    /// channels; multiplication saturates at UInt32.MaxValue.
    /// </summary>
    public MedusaOutgoingDamageResult PreviewOutgoingDamage(
        int attackingCharacterId,
        in CombatResolution source) =>
        PreviewOutgoingDamageCore(
            attackingCharacterId,
            attackingOwnership: PureCompatibilityOwnership,
            attackingLifeRevision: 0,
            attackingWorldMembershipEpoch:
                PureCompatibilityWorldMembershipEpoch,
            observedAt: null,
            source);

    public MedusaOutgoingDamageResult PreviewOutgoingDamage(
        int attackingCharacterId,
        PlayerOwnershipFence attackingOwnership,
        long attackingLifeRevision,
        in CombatResolution source) =>
        PreviewOutgoingDamageCore(
            attackingCharacterId,
            attackingOwnership,
            attackingLifeRevision,
            attackingWorldMembershipEpoch:
                PureCompatibilityWorldMembershipEpoch,
            observedAt: null,
            source);

    /// <summary>
    /// Timestamp-aware pure preview for authoritative damage commits. Expired
    /// amplifiers are ignored even when no clock consumer has evicted their
    /// stored snapshot; no periodic tick or expiry state is consumed here.
    /// </summary>
    public MedusaOutgoingDamageResult PreviewOutgoingDamage(
        int attackingCharacterId,
        DateTimeOffset observedAt,
        in CombatResolution source) =>
        PreviewOutgoingDamageCore(
            attackingCharacterId,
            attackingOwnership: PureCompatibilityOwnership,
            attackingLifeRevision: 0,
            attackingWorldMembershipEpoch:
                PureCompatibilityWorldMembershipEpoch,
            observedAt.ToUniversalTime(),
            source);

    public MedusaOutgoingDamageResult PreviewOutgoingDamage(
        int attackingCharacterId,
        PlayerOwnershipFence attackingOwnership,
        long attackingLifeRevision,
        DateTimeOffset observedAt,
        in CombatResolution source) => PreviewOutgoingDamage(
        attackingCharacterId,
        attackingOwnership,
        attackingLifeRevision,
        attackingWorldMembershipEpoch:
            PureCompatibilityWorldMembershipEpoch,
        observedAt,
        source);

    public MedusaOutgoingDamageResult PreviewOutgoingDamage(
        int attackingCharacterId,
        PlayerOwnershipFence attackingOwnership,
        long attackingLifeRevision,
        long attackingWorldMembershipEpoch,
        DateTimeOffset observedAt,
        in CombatResolution source) =>
        PreviewOutgoingDamageCore(
            attackingCharacterId,
            attackingOwnership,
            attackingLifeRevision,
            attackingWorldMembershipEpoch,
            observedAt.ToUniversalTime(),
            source);

    private MedusaOutgoingDamageResult PreviewOutgoingDamageCore(
        int attackingCharacterId,
        PlayerOwnershipFence attackingOwnership,
        long attackingLifeRevision,
        long attackingWorldMembershipEpoch,
        DateTimeOffset? observedAt,
        in CombatResolution source)
    {
        if (attackingLifeRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attackingLifeRevision));
        }
        if (attackingWorldMembershipEpoch <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attackingWorldMembershipEpoch));
        }
        if (!_charactersById.TryGetValue(
                attackingCharacterId,
                out var character))
        {
            return RejectedOutgoing(
                MedusaOutgoingDamageOutcome.CharacterNotAdmitted,
                source);
        }
        if (observedAt is { } authoritativeAt &&
            authoritativeAt < _lastObservedAt)
        {
            return RejectedOutgoing(
                MedusaOutgoingDamageOutcome.TimestampMovedBackward,
                source);
        }

        var channel = source.Channel switch
        {
            CombatDamageChannel.Physical => MedusaDamageChannel.Physical,
            CombatDamageChannel.Magic => MedusaDamageChannel.Magical,
            _ => default
        };
        if (channel == default)
        {
            return RejectedOutgoing(
                MedusaOutgoingDamageOutcome.UnknownDamageChannel,
                source);
        }
        if (source.Outcome is not CombatHitOutcome.Normal and
            not CombatHitOutcome.Critical and
            not CombatHitOutcome.Miss)
        {
            return RejectedOutgoing(
                MedusaOutgoingDamageOutcome.UnknownHitOutcome,
                source);
        }

        var multiplier = source.Outcome == CombatHitOutcome.Miss ||
                         source.Damage == 0
            ? 1
            : character.Effects.Values
                .Where(effect =>
                    effect.TargetOwnership == attackingOwnership &&
                    effect.TargetLifeRevision == attackingLifeRevision &&
                    effect.TargetWorldMembershipEpoch ==
                        attackingWorldMembershipEpoch &&
                    effect.Definition.OutgoingDamageChannel == channel &&
                    (observedAt is null ||
                     observedAt.Value < effect.ExpiresAt))
                .Select(effect =>
                    effect.Definition.OutgoingDamageMultiplier)
                .DefaultIfEmpty(1)
                .Max();
        var resolved = multiplier == 1
            ? source
            : source with
            {
                Damage = SaturatingMultiply(source.Damage, multiplier)
            };

        return new(
            MedusaOutgoingDamageOutcome.Resolved,
            multiplier,
            resolved);
    }

    private static uint SaturatingMultiply(uint value, int multiplier)
    {
        if (multiplier <= 0)
        {
            throw new InvalidOperationException(
                "An authored outgoing multiplier must be positive.");
        }

        var product = (ulong)value * (uint)multiplier;
        return product > uint.MaxValue ? uint.MaxValue : (uint)product;
    }

    private static MedusaOutgoingDamageResult RejectedOutgoing(
        MedusaOutgoingDamageOutcome outcome,
        in CombatResolution source) => new(
        outcome,
        AppliedMultiplier: 1,
        source);
}
