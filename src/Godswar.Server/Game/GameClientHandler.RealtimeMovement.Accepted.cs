using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Operations;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<RealtimeAcceptedMovementResult>
        ApplyAcceptedRealtimeMovementAsync(
            AuthoritativePlayerMovementDecision decision,
            CancellationToken cancellationToken)
    {
        if (_character is null || _account is null)
        {
            return default;
        }

        var previousX = _character.PositionX;
        var previousZ = _character.PositionZ;
        var previousMapId = _character.CurrentMap;
        await InterruptPendingSkillCastAsync(
            SkillCastInterruptionReason.Movement,
            cancellationToken);
        if ((decision.Source &
                AuthoritativePlayerMovementSource.Udp) != 0)
        {
            ControlledHostPrivacyEvidence.RecordIfActive(
                ControlledHostEvidenceEvent
                    .AuthoritativeUdpMovementAccepted);
            _phase4UdpEvidencePending = true;
            _phase4UdpEvidenceInputId = decision.InputId;
            _phase4UdpEvidenceTransportEpoch =
                decision.TransportEpoch;
        }

        _character.PositionX = decision.AuthoritativeX;
        _character.PositionZ = decision.AuthoritativeZ;
        _character.MarkPositionChanged();
        _positionDirty = true;
        _realtimeSnapshotDirty = true;
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);

        var acceptedSegment = new AcceptedMapMovementSegment(
            previousMapId,
            new MapTraversalPosition(previousX, previousZ),
            new MapTraversalPosition(
                decision.AuthoritativeX,
                decision.AuthoritativeZ));
        if (await TryBeginMapTransitionAsync(
                acceptedSegment,
                cancellationToken))
        {
            return new RealtimeAcceptedMovementResult(
                TransitionStarted: true,
                null,
                null);
        }

        await RefreshNearbyWorldObjectsAsync(
            "realtime-walk",
            cancellationToken);
        var viewerMovement = BuildRealtimeLegacyMovement(
            decision.OpaqueState,
            decision.AuthoritativeX,
            decision.AuthoritativeZ,
            decision.AuthoritativeAuxiliary,
            WorldObjectIds.ForPlayer(_character.Id));
        var positionSave = new RealtimePositionSave(
            _account.Id,
            _character.Id,
            _character.CurrentMap,
            _character.PositionX,
            _character.PositionZ,
            _character.PositionRevision);
        return new RealtimeAcceptedMovementResult(
            TransitionStarted: false,
            viewerMovement,
            positionSave);
    }

    private readonly record struct RealtimeAcceptedMovementResult(
        bool TransitionStarted,
        byte[]? ViewerMovement,
        RealtimePositionSave? PositionSave);

    private readonly record struct RealtimePositionSave(
        int AccountId,
        int CharacterId,
        byte MapId,
        float X,
        float Z,
        long Revision);
}
