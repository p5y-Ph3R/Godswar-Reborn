using Godswar.Server.State;
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

    private ElementalMovementAuthority ResolveElementalMovementAuthority(
        float baseMovementMultiplier,
        DateTimeOffset observedAt)
    {
        if (_character is null)
        {
            return default;
        }

        var encoded = AuthoredElementalCombatV1.EncodeMovementMultiplier(
            baseMovementMultiplier);
        encoded = ElementalResonanceExecutionPolicy.ApplyPassiveBonuses(
            _character.ElementalEquipment,
            maximumHealth: 0,
            movementSpeed: encoded).MovementSpeed;
        var allowed = true;
        if (TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            var fence = new ElementalCombatSessionFence(
                _character.Id,
                _character.CurrentMap,
                ownership);
            if (_registry.TryGetElementalStatusAdjustment(
                    _session,
                    fence,
                    observedAt.ToUnixTimeMilliseconds(),
                    encoded,
                    physicalDefense: 0,
                    magicDefense: 0,
                    hitRating: 0,
                    healingReceived: 0,
                    out var status))
            {
                allowed = status.MovementAllowed;
                encoded = status.MovementSpeed;
            }
        }

        return new(
            allowed,
            AuthoredElementalCombatV1.DecodeMovementMultiplier(encoded),
            encoded);
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
