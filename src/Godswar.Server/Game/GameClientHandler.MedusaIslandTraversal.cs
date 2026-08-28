using Godswar.Server.Application.Characters;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> TryApplyMedusaIslandTraversalAsync(
        AcceptedMapMovementSegment movement,
        CancellationToken cancellationToken)
    {
        if (_pendingMapTransition is not null ||
            _account is null ||
            _character is null ||
            !_registered ||
            !_worldPresenceAnnounced ||
            movement.MapId != _character.CurrentMap ||
            !MedusaIslandTraversalDetector.TryResolve(
                movement,
                MapPortalTriggerRadius,
                out var traversal) ||
            _registry.ResolveMedusaCharacterEffectAuthority(
                    _session,
                    DateTimeOffset.UtcNow).Outcome !=
                MedusaCharacterEffectAuthorityOutcome.ResolvedActive)
        {
            return false;
        }
        if (!TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            RejectLostPlayerOwnership();
            return false;
        }

        await InterruptPendingSkillCastAsync(
            SkillCastInterruptionReason.MapTransition,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return false;
        }

        var sourceX = _character.PositionX;
        var sourceZ = _character.PositionZ;
        try
        {
            if (!await PersistRelocationCheckpointAsync(
                    _character.CurrentMap,
                    traversal.TargetX,
                    traversal.TargetZ,
                    cancellationToken))
            {
                return false;
            }
        }
        catch (PlayerOwnershipValidationException)
        {
            RejectLostPlayerOwnership();
            return false;
        }
        catch (Exception error)
            when (error is not OperationCanceledException ||
                  !cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine(
                "[instance] island transfer persistence rejected " +
                $"character={_character.Name}: {error.Message}");
            return false;
        }
        if (!RevalidateCurrentPlayerOwnership(ownership) ||
            _registry.ResolveMedusaCharacterEffectAuthority(
                    _session,
                    DateTimeOffset.UtcNow).Outcome !=
                MedusaCharacterEffectAuthorityOutcome.ResolvedActive)
        {
            return false;
        }

        _character.PositionX = traversal.TargetX;
        _character.PositionZ = traversal.TargetZ;
        _positionDirty = false;
        _lastPositionPersistUtc = DateTime.UtcNow;
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        if (!_registry.TryHideForSameWorldSceneTransition(
                _session,
                ownership,
                out _))
        {
            _session.Disconnect();
            return false;
        }

        _worldPresenceAnnounced = false;
        ClearLocalNpcCatalog();
        ClearForgeSelection();
        ClearGearEnhancerSelection();
        _warehouseAccessContext = null;
        ResetPlayerMovementEcs();
        RebaseRealtimeWorld();
        _nextBasicAttackAt = DateTimeOffset.MinValue;
        _nextSkillCastAt.Clear();

        var transition = new PendingMapTransition(
            _character.CurrentMap,
            _character.CurrentMap,
            traversal.TargetX,
            traversal.TargetZ);
        _pendingMapTransition = transition;
        _mapTransitionTimeoutTask = MonitorMapTransitionTimeoutAsync(
            transition,
            _realtimeMovementStop.Token);

        await _registry.BroadcastToCurrentWorldInstanceAsync(
            _session,
            PacketBuilder.RemoveWorldObjects(CurrentPlayerObjectId),
            cancellationToken,
            includeRoutingSession: false,
            "MedusaIslandTransferSourceRemove");
        await _session.SendAsync(
            PacketBuilder.SceneChange(
                LocalPlayerObjectId,
                traversal.TargetX,
                y: 0f,
                traversal.TargetZ,
                _character.CurrentMap),
            cancellationToken,
            "MedusaIslandSceneChange");
        await PublishPartyPositionRefreshAsync(cancellationToken);

        Console.WriteLine(
            "[instance] island transfer applied " +
            $"character={_character.Name} " +
            $"anchor={traversal.SourceAnchorId}" +
            $"->{traversal.TargetAnchorId} " +
            $"position={sourceX:F2},{sourceZ:F2}" +
            $"->{traversal.TargetX:F2},{traversal.TargetZ:F2}");
        return true;
    }
}
