using Godswar.Server.State;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private bool IsElementalMovementAllowed(DateTimeOffset observedAt)
    {
        if (_character is null)
        {
            return false;
        }

        if (!IsHostileStatusMovementAllowed(observedAt))
        {
            return false;
        }

        var runtime = _registry.GetRuntimeStatusAggregate(
            _session,
            observedAt);
        var authority = ResolveElementalMovementAuthority(
            runtime.MovementSpeedMultiplier,
            observedAt);
        if (!authority.MovementAllowed)
        {
            Console.WriteLine(
                "[elemental] movement blocked by Shock " +
                $"character={_character.Name}");
        }

        return authority.MovementAllowed;
    }

    private bool IsHostileStatusMovementAllowed(DateTimeOffset observedAt)
    {
        if (_character is null)
        {
            return false;
        }

        var control = _registry.GetTrainingDummyHostileControl(
            _session,
            observedAt);
        if ((control & HostileStatusControlFlags.NonMoving) != 0)
        {
            Console.WriteLine(
                "[status] movement blocked " +
                $"character={_character.Name} control={control}");
            return false;
        }

        return IsMedusaActionAllowed(
            MedusaEncounterControlRestriction.Movement,
            observedAt,
            "movement");
    }

    private bool IsNonWalkMovementBlocked() =>
        _character is not null &&
        !IsElementalMovementAllowed(DateTimeOffset.UtcNow);

    private ElementalMovementAuthority ResolveElementalMovementAuthority(
        float baseMovementMultiplier,
        DateTimeOffset observedAt)
    {
        if (_character is null)
        {
            return default;
        }

        TryCaptureCurrentPlayerOwnership(out var ownership);
        return _registry.ResolveElementalMovementAuthority(
            _session,
            _character,
            ownership,
            baseMovementMultiplier,
            observedAt);
    }

    private ClientStatusAggregate ApplyElementalMovementStatus(
        ClientStatusAggregate status,
        DateTimeOffset observedAt)
    {
        var authority = ResolveElementalMovementAuthority(
            status.MovementSpeedMultiplier,
            observedAt);
        return authority.EncodedMovementMultiplier > 0
            ? status with
            {
                MovementSpeedMultiplier = authority.MovementMultiplier
            }
            : status;
    }

    private void CommitAcceptedElementalMovement(
        in AcceptedMapMovementSegment movement,
        DateTimeOffset acceptedAt)
    {
        if (_character is null ||
            movement.MapId != _character.CurrentMap ||
            _character.PositionRevision <= 0 ||
            !TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            return;
        }

        var fence = new ElementalCombatSessionFence(
            _character.Id,
            _character.CurrentMap,
            ownership);
        var movementEvent =
            AuthoredElementalCombatV1.AcceptedMovementEvent(
                _character.Id,
                _character.CurrentMap,
                _character.PositionRevision,
                acceptedAt);
        var runtime = _registry.GetRuntimeStatusAggregate(
            _session,
            acceptedAt);
        var baseMovement =
            ElementalResonanceExecutionPolicy.ApplyPassiveBonuses(
                _character.ElementalEquipment,
                maximumHealth: 0,
                movementSpeed: AuthoredElementalCombatV1.EncodeMovementMultiplier(
                    runtime.MovementSpeedMultiplier))
            .MovementSpeed;
        var distance = AuthoredElementalCombatV1
            .AcceptedDistanceMillimeters(
                movement.Start.X,
                movement.Start.Z,
                movement.End.X,
                movement.End.Z);
        _registry.TryProcessAcceptedElementalMovement(
            _session,
            fence,
            movementEvent,
            _character.ElementalEquipment,
            AuthoredElementalCombatV1.EffectTuning,
            distance,
            baseMovement,
            out _);
    }
}
