using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class PlayerCombatEcsAdapter
{
    private void AdjustElementalPveDamageReservations(
        GameSessionRegistry registry,
        ClientSession session,
        GameCharacter character,
        in PlayerCombatEcsRequest request,
        ulong intentId,
        PlayerCombatTargetResolvedEvent[] resolvedTargets,
        PlayerCombatDamageIntentEvent[] damageIntents)
    {
        if (damageIntents.Length == 0 ||
            !_world!.Has<PlayerCombatReservationComponent>(_player))
        {
            return;
        }

        var provenance = request.Kind == PlayerCombatIntentKind.BasicAttack
            ? CombatEventProvenance.DirectBasicAttack
            : CombatEventProvenance.DirectSkill;
        var adjustedDamage = new Dictionary<int, uint>();
        for (var index = 0; index < resolvedTargets.Length; index++)
        {
            var resolved = resolvedTargets[index];
            if (!resolved.Resolution.Hit ||
                resolved.Resolution.Damage == 0 ||
                !registry.TryGetMonsterSnapshot(
                    session,
                    character.CurrentMap,
                    resolved.TargetObjectId,
                    out var target) ||
                target.SpawnGeneration !=
                    resolved.ExpectedSpawnGeneration ||
                target.HealthRevision !=
                    resolved.ExpectedHealthRevision)
            {
                continue;
            }

            var adjustment = registry.AdjustPveOutgoingResolution(
                session,
                character,
                target,
                provenance,
                request.RequestedAt,
                resolved.Resolution,
                intentId);
            if (adjustment.Damage == resolved.Resolution.Damage)
            {
                continue;
            }

            resolvedTargets[index] = resolved with
            {
                Resolution = adjustment
            };
            adjustedDamage[resolved.TargetOrder] = adjustment.Damage;
        }

        if (adjustedDamage.Count == 0)
        {
            return;
        }

        for (var index = 0; index < damageIntents.Length; index++)
        {
            var intent = damageIntents[index];
            if (adjustedDamage.TryGetValue(
                    intent.TargetOrder,
                    out var damage))
            {
                damageIntents[index] = intent with
                {
                    RequestedDamage = damage
                };
            }
        }

        ref var reservation = ref _world.Get<
            PlayerCombatReservationComponent>(_player);
        var targets = reservation.Targets.ToBuilder();
        for (var index = 0; index < targets.Count; index++)
        {
            var target = targets[index];
            if (adjustedDamage.TryGetValue(
                    target.TargetOrder,
                    out var damage))
            {
                targets[index] = target with
                {
                    RequestedDamage = damage
                };
            }
        }

        reservation.Targets = targets.MoveToImmutable();
    }
}
