using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private void RebaseRealtimeWorld()
    {
        if (_character is null)
        {
            return;
        }

        _realtimeCharacterId = _character.Id;
        _realtimeMapId = _character.CurrentMap;
        _realtimeWorldGeneration =
            NextNonzero(_realtimeWorldGeneration);
        if (_authoritativePlayerMovement?.Snapshot is { } prior)
        {
            _authoritativePlayerMovement =
                new AuthoritativePlayerMovementSystem(
                    new AuthoritativePlayerMovementBaseline(
                        prior.TransportEpoch,
                        _realtimeWorldGeneration,
                        _character.CurrentMap,
                        LocalPlayerObjectId,
                        RealtimeNeutralMovementState,
                        _character.PositionX,
                        _character.PositionZ,
                        Auxiliary: 1f,
                        ServerTimestamp: _realtimeLastIngressElapsed,
                        AcknowledgedInputId: prior.AcknowledgedInputId,
                        PositionRevision: checked(prior.Revision + 1),
                        SimulationTick: prior.SimulationTick));
        }
        _realtimeSnapshotDirty = true;
        _realtimeKeyframePending = true;
    }
}
