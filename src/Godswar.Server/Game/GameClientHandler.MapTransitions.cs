using Godswar.Server.Game.Maps;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private const float MapPortalTriggerRadius = 6f;
    private static readonly TimeSpan DefaultMapTransitionReadyTimeout =
        TimeSpan.FromSeconds(60);

    private readonly CharacterPositionPersistenceCoordinator
        _positionPersistence = new();
    private readonly TimeSpan _mapTransitionReadyTimeout;
    private PendingMapTransition? _pendingMapTransition;
    private Task? _mapTransitionTimeoutTask;

    private bool IsMapTransitionPending =>
        _pendingMapTransition is not null;

    private static bool IsAllowedDuringMapTransition(ushort opcode) =>
        opcode is
            Opcodes.ClientReady or
            Opcodes.PlayerDetailRequest or
            Opcodes.Ping or
            Opcodes.UiHeartbeat;

    private async Task<bool> TryBeginMapTransitionAsync(
        AcceptedMapMovementSegment movement,
        CancellationToken cancellationToken)
    {
        if (_pendingMapTransition is not null)
        {
            return true;
        }

        if (_character is null ||
            movement.MapId != _character.CurrentMap ||
            !MapTraversalDetector.TryDetectAndResolve(
                MapTraversalCatalog.Default,
                movement,
                MapPortalTriggerRadius,
                out var resolution) ||
            resolution.SourceMapId is < byte.MinValue or > byte.MaxValue ||
            resolution.TargetMapId is < byte.MinValue or > byte.MaxValue)
        {
            return false;
        }

        var targetMapId = checked((byte)resolution.TargetMapId);
        return await TryBeginMapTransitionAsync(
            targetMapId,
            resolution.TargetArrival.X,
            resolution.TargetArrival.Z,
            resolution.Source,
            cancellationToken);
    }

    private async Task<bool> TryBeginMapTransitionAsync(
        byte targetMapId,
        float targetX,
        float targetZ,
        string source,
        CancellationToken cancellationToken)
    {
        if (_pendingMapTransition is not null ||
            _account is null ||
            _character is null ||
            !_registered ||
            !_worldPresenceAnnounced ||
            _character.CurrentMap == targetMapId ||
            !MapTraversalCatalog.Default.TryGetMap(
                _character.CurrentMap,
                out _) ||
            !MapTraversalCatalog.Default.TryGetMap(targetMapId, out _) ||
            !MapTraversalLimits.IsFiniteAndBounded(
                new MapTraversalPosition(targetX, targetZ)))
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
            await _positionPersistence.AdvanceAndPersistAsync(
                token => _store.SaveCharacterPositionAsync(
                    accountId,
                    characterId,
                    targetMapId,
                    targetX,
                    targetZ,
                    token),
                cancellationToken);
        }
        catch (Exception error)
            when (error is not OperationCanceledException ||
                  !cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine(
                $"[map] transition persistence rejected " +
                $"character={_character.Name} " +
                $"map={sourceMapId}->{targetMapId}: {error.Message}");
            return false;
        }

        bool transferred;
        try
        {
            transferred = _registry.TryTransferMap(
                _session,
                sourceMapId,
                targetMapId,
                targetX,
                targetZ);
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
                $"registry transfer failed: {error.Message}",
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
                "registry rejected the authoritative source state",
                CancellationToken.None);
            return false;
        }

        _positionDirty = false;
        _lastPositionPersistUtc = DateTime.UtcNow;
        _worldPresenceAnnounced = false;
        ClearLocalNpcCatalog();
        ClearForgeSelection();
        ClearGearEnhancerSelection();
        ResetPlayerMovementEcs();
        _nextBasicAttackAt = DateTimeOffset.MinValue;
        _nextSkillCastAt.Clear();

        var transition = new PendingMapTransition(
            sourceMapId,
            targetMapId,
            targetX,
            targetZ);
        _pendingMapTransition = transition;
        _mapTransitionTimeoutTask =
            MonitorMapTransitionTimeoutAsync(
                transition,
                _realtimeMovementStop.Token);

        try
        {
            await _registry.BroadcastToMapAsync(
                sourceMapId,
                PacketBuilder.RemoveWorldObjects(
                    WorldObjectIds.ForPlayer(characterId)),
                cancellationToken,
                _session,
                "MapTransitionSourceRemove");
            await _session.SendAsync(
                PacketBuilder.SceneChange(
                    LocalPlayerObjectId,
                    targetX,
                    y: 0f,
                    targetZ,
                    targetMapId),
                cancellationToken,
                "SceneChange");
        }
        catch
        {
            // The destination is already the persisted authority. A reconnect
            // will enter it cleanly; continuing on an old client scene would
            // instead create two conflicting worlds.
            _session.Disconnect();
            throw;
        }

        Console.WriteLine(
            $"[map] scene change queued character={_character.Name} " +
            $"map={sourceMapId}->{targetMapId} " +
            $"arrival={targetX:F2},{targetZ:F2} " +
            $"source={source}");
        return true;
    }

    private async Task RestoreSourcePositionAfterRejectedTransferAsync(
        int accountId,
        int characterId,
        byte sourceMapId,
        float sourceX,
        float sourceZ,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await _positionPersistence.AdvanceAndPersistAsync(
                token => _store.SaveCharacterPositionAsync(
                    accountId,
                    characterId,
                    sourceMapId,
                    sourceX,
                    sourceZ,
                    token),
                cancellationToken);
            _positionDirty = false;
            _lastPositionPersistUtc = DateTime.UtcNow;
            Console.WriteLine(
                $"[map] restored source position after rejected transition " +
                $"character={_character?.Name ?? characterId.ToString()} " +
                $"map={sourceMapId} reason={reason}");
        }
        catch (Exception compensationError)
        {
            Console.WriteLine(
                $"[map] source-position compensation failed " +
                $"character={_character?.Name ?? characterId.ToString()} " +
                $"map={sourceMapId} reason={reason}: " +
                compensationError.Message);
            _session.Disconnect();
            throw;
        }
    }

    private async Task HandleClientReadyAsync(
        CancellationToken cancellationToken)
    {
        if (_pendingMapTransition is { } transition)
        {
            transition.ClientReadyReceived = true;
            Console.WriteLine(
                $"[map] transition ClientReady " +
                $"character={_character?.Name ?? "<none>"} " +
                $"map={transition.SourceMapId}->{transition.TargetMapId}");
            await TryCompleteMapTransitionAsync(cancellationToken);
            return;
        }

        _clientReadyReceived = true;
        Console.WriteLine(
            $"[game] ClientReady " +
            $"character={_character?.Name ?? "<none>"}");
        await SendPostEnterBootstrapAsync(cancellationToken);
    }

    private async Task<bool>
        HandleMapTransitionPlayerDetailSentAsync(
            CancellationToken cancellationToken)
    {
        if (_pendingMapTransition is not { } transition)
        {
            return false;
        }

        transition.PlayerDetailSent = true;
        Console.WriteLine(
            $"[map] transition PlayerDetail " +
            $"character={_character?.Name ?? "<none>"} " +
            $"map={transition.SourceMapId}->{transition.TargetMapId}");
        await TryCompleteMapTransitionAsync(cancellationToken);
        return true;
    }

    private async Task TryCompleteMapTransitionAsync(
        CancellationToken cancellationToken)
    {
        var transition = _pendingMapTransition;
        if (transition is null ||
            !transition.ClientReadyReceived ||
            !transition.PlayerDetailSent)
        {
            return;
        }

        if (_character is null ||
            _character.CurrentMap != transition.TargetMapId)
        {
            _session.Disconnect();
            throw new InvalidOperationException(
                "Map transition authority changed before client readiness.");
        }

        if (!transition.TryStartCompletion())
        {
            return;
        }

        try
        {
            await SendMapWorldObjectsAsync(cancellationToken);
            await _session.SendAsync(
                BuildLocalPlayerStatusUpdate(),
                cancellationToken,
                "MapTransitionPlayerStatus");
            await SendExperienceBoostStatusAsync(
                "map-transition",
                cancellationToken);

            // A secure realtime client receives one destination keyframe only
            // after the reliable scene/AOI handoff is ready. This preserves
            // the triggering input acknowledgement while advancing the world
            // generation so old-map UDP input cannot mutate the new map.
            EnsureRealtimeWorld();
            PublishRealtimeSnapshotIfDue(decision: null);
            _pendingMapTransition = null;
            transition.MarkCompleted();
        }
        catch
        {
            _session.Disconnect();
            throw;
        }

        Console.WriteLine(
            $"[map] transition complete character={_character.Name} " +
            $"map={transition.SourceMapId}->{transition.TargetMapId} " +
            $"arrival={transition.TargetX:F2},{transition.TargetZ:F2}");
    }

    private async Task MonitorMapTransitionTimeoutAsync(
        PendingMapTransition transition,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutLifetime =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    transition.TimeoutCancellation);
            await Task.Delay(
                _mapTransitionReadyTimeout,
                timeoutLifetime.Token);
            if (!transition.TryMarkTimedOut())
            {
                return;
            }

            Console.WriteLine(
                $"[map] transition readiness timed out " +
                $"character={_character?.Name ?? "<none>"} " +
                $"map={transition.SourceMapId}->{transition.TargetMapId}");
            _session.Disconnect();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested ||
                  transition.TimeoutCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            transition.DisposeTimeoutCancellation();
        }
    }

    private sealed class PendingMapTransition(
        byte sourceMapId,
        byte targetMapId,
        float targetX,
        float targetZ)
    {
        private const int AwaitingReadiness = 0;
        private const int Completing = 1;
        private const int Completed = 2;
        private const int TimedOut = 3;

        private readonly object _timeoutGate = new();
        private readonly CancellationTokenSource _timeoutCancellation =
            new();
        private int _state = AwaitingReadiness;

        public byte SourceMapId { get; } = sourceMapId;

        public byte TargetMapId { get; } = targetMapId;

        public float TargetX { get; } = targetX;

        public float TargetZ { get; } = targetZ;

        public bool ClientReadyReceived { get; set; }

        public bool PlayerDetailSent { get; set; }

        public CancellationToken TimeoutCancellation =>
            _timeoutCancellation.Token;

        public bool TryStartCompletion()
        {
            if (Interlocked.CompareExchange(
                    ref _state,
                    Completing,
                    AwaitingReadiness) != AwaitingReadiness)
            {
                return false;
            }

            lock (_timeoutGate)
            {
                _timeoutCancellation.Cancel();
            }
            return true;
        }

        public bool TryMarkTimedOut() =>
            Interlocked.CompareExchange(
                ref _state,
                TimedOut,
                AwaitingReadiness) == AwaitingReadiness;

        public void MarkCompleted()
        {
            if (Interlocked.CompareExchange(
                    ref _state,
                    Completed,
                    Completing) != Completing)
            {
                throw new InvalidOperationException(
                    "Map transition completion state changed unexpectedly.");
            }
        }

        public void DisposeTimeoutCancellation()
        {
            lock (_timeoutGate)
            {
                _timeoutCancellation.Dispose();
            }
        }
    }
}
