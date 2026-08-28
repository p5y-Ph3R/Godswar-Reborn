using System.Collections.Immutable;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game.WorldInstances;

internal sealed partial class MedusaEncounterMechanicsRuntime
{
    /// <summary>
    /// Resolves active effects for one exact ownership/life authority without
    /// mutating the encounter clock. A stale target is a valid empty view;
    /// it must never inherit controls from a previous session or life.
    /// </summary>
    public bool TryGetActiveCharacterEffectView(
        int characterId,
        PlayerOwnershipFence targetOwnership,
        long targetLifeRevision,
        DateTimeOffset observedAt,
        out MedusaActiveCharacterEffectView view) =>
        TryGetActiveCharacterEffectView(
            characterId,
            targetOwnership,
            targetLifeRevision,
            targetWorldMembershipEpoch:
                PureCompatibilityWorldMembershipEpoch,
            observedAt,
            out view);

    public bool TryGetActiveCharacterEffectView(
        int characterId,
        PlayerOwnershipFence targetOwnership,
        long targetLifeRevision,
        long targetWorldMembershipEpoch,
        DateTimeOffset observedAt,
        out MedusaActiveCharacterEffectView view)
    {
        if (targetLifeRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetLifeRevision));
        }
        if (targetWorldMembershipEpoch <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetWorldMembershipEpoch));
        }
        if (!_charactersById.TryGetValue(
                characterId,
                out var character))
        {
            view = null!;
            return false;
        }

        var requestedAt = observedAt.ToUniversalTime();
        var evaluatedAt = requestedAt < _lastObservedAt
            ? _lastObservedAt
            : requestedAt;
        var effects = character.Effects.Values
            .Where(effect =>
                effect.TargetOwnership == targetOwnership &&
                effect.TargetLifeRevision == targetLifeRevision &&
                effect.TargetWorldMembershipEpoch ==
                    targetWorldMembershipEpoch &&
                evaluatedAt < effect.ExpiresAt)
            .OrderBy(static effect => effect.Definition.Kind)
            .Select(static effect => effect.Snapshot())
            .ToImmutableArray();
        var control = effects.Aggregate(
            MedusaEncounterControlRestriction.None,
            static (current, effect) =>
                current | effect.Definition.ControlRestriction);

        view = new(
            characterId,
            new(
                targetOwnership,
                targetLifeRevision,
                targetWorldMembershipEpoch),
            evaluatedAt,
            Deadline,
            control,
            MultiplierFor(effects, MedusaDamageChannel.Physical),
            MultiplierFor(effects, MedusaDamageChannel.Magical),
            effects);
        return true;
    }
}
