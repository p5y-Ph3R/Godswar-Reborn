using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> HandlePartyInstanceTransitionAsync(
        MedusaInstanceTransitionCommand command,
        CancellationToken cancellationToken)
    {
        await _characterStateGate.WaitAsync(cancellationToken);
        try
        {
            return await TryBeginMedusaInstanceTransitionAsync(
                command,
                cancellationToken);
        }
        finally
        {
            _characterStateGate.Release();
        }
    }

    private async Task<bool> TryBeginMedusaInstanceTransitionAsync(
        MedusaInstanceTransitionCommand command,
        CancellationToken cancellationToken)
    {
        if (_pendingMapTransition is not null ||
            _account is null ||
            _character is null ||
            !_registered ||
            !_worldPresenceAnnounced ||
            _character.Id != command.CharacterId ||
            _character.CurrentMap != command.ExpectedSourceMapId ||
            !IsSupportedMedusaTransition(command) ||
            command.ExpectedSourceWorldInstanceId ==
                command.TargetWorldInstanceId ||
            !command.TargetWorldInstanceId.IsValid ||
            !MapTraversalLimits.IsFiniteAndBounded(
                new MapTraversalPosition(
                    command.TargetX,
                    command.TargetZ)) ||
            !_registry.TryGetSessionWorldInstanceId(
                _session,
                out var currentWorldInstanceId) ||
            currentWorldInstanceId !=
                command.ExpectedSourceWorldInstanceId ||
            !_registry.TryGetWorldInstance(
                command.TargetWorldInstanceId,
                out var target) ||
            target.MapId.Value != command.TargetMapId ||
            target.LifecycleState != WorldInstanceLifecycleState.Active ||
            !_registry.EnsurePlayerStatusState(_session) ||
            !TryCaptureCurrentPlayerOwnership(out var ownership) ||
            ownership != command.ExpectedOwnership ||
            _playerCoordination?.IsEnabled == true &&
                _playerCoordinationLease?.IsCurrent != true)
        {
            return false;
        }

        await InterruptPendingSkillCastAsync(
            SkillCastInterruptionReason.MapTransition,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return false;
        }

        var sourceMapId = _character.CurrentMap;
        var sourceX = _character.PositionX;
        var sourceZ = _character.PositionZ;
        var accountId = _account.Id;
        var characterId = _character.Id;
        try
        {
            if (!await PersistRelocationCheckpointAsync(
                    command.TargetMapId,
                    command.TargetX,
                    command.TargetZ,
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
                "[instance] relocation checkpoint rejected " +
                $"character={_character.Name}: {error.Message}");
            return false;
        }
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return false;
        }

        bool transferred;
        try
        {
            transferred = _registry.TryTransferWorldInstance(
                _session,
                command.ExpectedSourceWorldInstanceId,
                command.TargetWorldInstanceId,
                command.TargetX,
                command.TargetZ);
        }
        catch (Exception error)
            when (error is not OperationCanceledException ||
                  !cancellationToken.IsCancellationRequested)
        {
            await RestoreSourcePositionAfterRejectedTransferAsync(
                accountId,
                characterId,
                sourceMapId,
                sourceX,
                sourceZ,
                $"instance transfer failed: {error.Message}",
                ownership,
                CancellationToken.None);
            return false;
        }
        if (!transferred)
        {
            await RestoreSourcePositionAfterRejectedTransferAsync(
                accountId,
                characterId,
                sourceMapId,
                sourceX,
                sourceZ,
                "registry rejected the Medusa transfer",
                ownership,
                CancellationToken.None);
            return false;
        }
        if (!await PublishPlayerCoordinationEnteringAsync(
                command.TargetMapId,
                cancellationToken))
        {
            _session.Disconnect();
            return false;
        }

        _positionDirty = false;
        _lastPositionPersistUtc = DateTime.UtcNow;
        _worldPresenceAnnounced = false;
        ClearLocalNpcCatalog();
        ClearForgeSelection();
        ClearGearEnhancerSelection();
        _warehouseAccessContext = null;
        ResetPlayerMovementEcs();
        _nextBasicAttackAt = DateTimeOffset.MinValue;
        _nextSkillCastAt.Clear();

        var transition = new PendingMapTransition(
            sourceMapId,
            command.TargetMapId,
            command.TargetX,
            command.TargetZ);
        _pendingMapTransition = transition;
        _mapTransitionTimeoutTask = MonitorMapTransitionTimeoutAsync(
            transition,
            _realtimeMovementStop.Token);

        try
        {
            await _registry.BroadcastToWorldInstanceAsync(
                command.ExpectedSourceWorldInstanceId,
                PacketBuilder.RemoveWorldObjects(CurrentPlayerObjectId),
                cancellationToken,
                _session,
                "InstanceTransitionSourceRemove");
            await _session.SendAsync(
                PacketBuilder.SceneChange(
                    LocalPlayerObjectId,
                    command.TargetX,
                    y: 0f,
                    command.TargetZ,
                    command.TargetMapId),
                cancellationToken,
                "MedusaSceneChange");
            await PublishPartyDeliveriesAsync(
                _registry.GetPartyRefreshDeliveries(_session),
                CancellationToken.None);
        }
        catch
        {
            _session.Disconnect();
            throw;
        }

        Console.WriteLine(
            "[instance] Medusa scene change queued " +
            $"character={_character.Name} map={sourceMapId}" +
            $"->{command.TargetMapId} instance=" +
            command.TargetWorldInstanceId);
        return true;
    }

    private bool IsSupportedMedusaTransition(
        in MedusaInstanceTransitionCommand command)
    {
        var entering = command.ExpectedSourceMapId is not (200 or 204) &&
            command.TargetMapId is 200 or 204;
        if (entering)
        {
            return true;
        }

        if (command.ExpectedSourceMapId is not (200 or 204) ||
            _character is null)
        {
            return false;
        }

        var capitalMapId = _character.Camp == GameDefaults.SpartaCamp
            ? GameDefaults.SpartaCapitalMap
            : GameDefaults.AthensCapitalMap;
        return command.TargetMapId == capitalMapId;
    }
}
