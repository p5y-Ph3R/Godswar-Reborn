using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Operations;
using Godswar.Server.Packets;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    internal const int RealtimeSnapshotTicks = 2;
    internal const int RealtimeKeyframeTicks = 20;
    private const uint RealtimeNeutralMovementState =
        0x0002_0000u;

    private readonly CancellationTokenSource _realtimeMovementStop =
        new();
    private CancellationTokenSource? _realtimeMovementLifetime;
    private Task? _realtimeMovementTask;
    private AuthoritativePlayerMovementSystem?
        _authoritativePlayerMovement;
    private int _realtimeCharacterId;
    private byte _realtimeMapId;
    private uint _realtimeWorldGeneration;
    private ulong _realtimeServerTick;
    private ulong _realtimeSnapshotSequence;
    private TimeSpan _realtimeLastIngressElapsed;
    private bool _realtimeSnapshotDirty;
    private bool _realtimeKeyframePending;
    private bool _phase4UdpEvidencePending;
    private ulong _phase4UdpEvidenceInputId;
    private uint _phase4UdpEvidenceTransportEpoch;

    private void StartRealtimeMovement(
        CancellationToken hostCancellation)
    {
        if (!_session.SupportsRealtimeMovement ||
            _realtimeMovementTask is not null)
        {
            return;
        }

        _realtimeMovementLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                hostCancellation,
                _realtimeMovementStop.Token);
        var lifetime = _realtimeMovementLifetime.Token;
        _realtimeMovementTask =
            RunRealtimeMovementAsync(lifetime);
    }

    private async Task StopRealtimeMovementAsync()
    {
        _realtimeMovementStop.Cancel();
        await ObserveRealtimeTaskAsync(
            _realtimeMovementTask,
            "simulation");
        await ObserveRealtimeTaskAsync(
            _mapTransitionTimeoutTask,
            "map-transition deadline");
        _realtimeMovementLifetime?.Dispose();
        _realtimeMovementLifetime = null;
        _realtimeMovementTask = null;
        _mapTransitionTimeoutTask = null;
        _realtimeMovementStop.Dispose();
    }

    private async Task RunRealtimeMovementAsync(
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(
            AuthoritativePlayerMovementPolicy.FixedStep);
        using var observation = new SimulationLoopObservation(
            SimulationLoopKind.RealtimeMovement,
            AuthoritativePlayerMovementPolicy.FixedStep);
        try
        {
            while (await timer.WaitForNextTickAsync(
                       cancellationToken))
            {
                var tick = observation.BeginTick();
                RealtimeMovementEffects effects;
                await _characterStateGate.WaitAsync(
                    cancellationToken);
                try
                {
                    effects =
                        await ProcessRealtimeMovementTickAsync(
                            cancellationToken);
                }
                finally
                {
                    _characterStateGate.Release();
                }

                await PublishRealtimeMovementEffectsAsync(
                    effects,
                    cancellationToken);
                tick.Complete();
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            observation.MarkCancelled();
        }
        catch (Exception error)
        {
            observation.MarkFaulted();
            Console.WriteLine(
                $"[realtime] movement pump failed: {error.Message}");
            _session.Disconnect();
        }
    }

    private async Task<RealtimeMovementEffects>
        ProcessRealtimeMovementTickAsync(
            CancellationToken cancellationToken)
    {
        _realtimeServerTick =
            IncrementSaturated(_realtimeServerTick);
        if (_character is null ||
            _account is null ||
            !_registered ||
            !_worldPresenceAnnounced)
        {
            ResetRealtimeWorldIfNeeded();
            return default;
        }

        if (!RevalidateCurrentWorldEffectOwnership(
                "realtime_movement_tick"))
        {
            return default;
        }

        EnsureRealtimeWorld();
        AuthoritativePlayerMovementDecision? decision = null;
        byte[]? viewerMovement = null;
        byte[]? reliableCorrection = null;
        RealtimePositionSave? positionSave = null;
        ulong? acceptanceCorrectionInputId = null;

        if (_session.TryTakeRealtimeMovement(out var ingress))
        {
            if (ingress.ServerReceiveElapsed >
                _realtimeLastIngressElapsed)
            {
                _realtimeLastIngressElapsed =
                    ingress.ServerReceiveElapsed;
            }
            EnsureRealtimeAuthority(ingress);
            var input = BuildAuthoritativeInput(ingress);
            var movement = _authoritativePlayerMovement!;
            var processInput = true;
            if (input.TransportEpoch !=
                    movement.Snapshot.TransportEpoch &&
                !movement.TryAdvanceTransportEpoch(
                    input.TransportEpoch))
            {
                movement.AdvanceWithoutInput();
                decision = RejectRealtimeTransportEpoch(
                    input,
                    movement.Snapshot);
                processInput = false;
            }
            else if (ingress.Kind ==
                         SecureRealtimeMovementIngressKind
                             .TransportTransition &&
                     movement.Snapshot.AcknowledgedInputId >=
                         input.InputId)
            {
                movement.AdvanceWithoutInput();
                _realtimeSnapshotDirty = true;
                _realtimeKeyframePending = true;
                processInput = false;
            }

            if (processInput)
            {
                var forceAcceptanceCorrection =
                    ShouldForcePhase4AcceptanceCorrection(
                        ingress);
                if (forceAcceptanceCorrection)
                {
                    acceptanceCorrectionInputId =
                        ingress.Input.InputId;
                }
                var movementObservedAt = DateTimeOffset.UtcNow;
                var status = _registry.GetRuntimeStatusAggregate(
                    _session,
                    movementObservedAt);
                var elementalMovement =
                    ResolveElementalMovementAuthority(
                        status.MovementSpeedMultiplier,
                        movementObservedAt);
                var world =
                    new AuthoritativePlayerMovementWorldContext(
                        input.TransportEpoch,
                        _realtimeWorldGeneration,
                        _character.CurrentMap,
                        LocalPlayerObjectId,
                        IsReady: !forceAcceptanceCorrection &&
                            IsHostileStatusMovementAllowed(
                                movementObservedAt) &&
                            elementalMovement.MovementAllowed,
                        IsAlive: _character.CurrentHp > 0,
                        elementalMovement.MovementMultiplier,
                        AuthoritativePlayerMovementSource.Tls |
                        AuthoritativePlayerMovementSource.Udp);
                decision = movement.ProcessLatest(
                    input,
                    world,
                    ingress.ServerReceiveElapsed);
            }

            if (decision is { } movementDecision &&
                movementDecision.Accepted)
            {
                var accepted =
                    await ApplyAcceptedRealtimeMovementAsync(
                        movementDecision,
                        cancellationToken);
                if (accepted.TransitionStarted)
                {
                    // Preserve the accepted-input acknowledgement in the
                    // movement authority, but hold all world egress until the
                    // native client completes its fresh scene-ready handshake.
                    return new RealtimeMovementEffects(
                        _character.CurrentMap,
                        null,
                        null,
                        null,
                        acceptanceCorrectionInputId);
                }

                viewerMovement = accepted.ViewerMovement;
                positionSave = accepted.PositionSave;
            }
            else if (decision is { } rejectedMovement)
            {
                reliableCorrection =
                    BuildRealtimeLegacyMovement(
                        rejectedMovement.OpaqueState,
                        rejectedMovement.AuthoritativeX,
                        rejectedMovement.AuthoritativeZ,
                        rejectedMovement.AuthoritativeAuxiliary,
                        LocalPlayerObjectId);
            }
        }
        else
        {
            _authoritativePlayerMovement?
                .AdvanceWithoutInput();
        }

        PublishRealtimeSnapshotIfDue(decision);
        return new RealtimeMovementEffects(
            _character.CurrentMap,
            viewerMovement,
            reliableCorrection,
            positionSave,
            acceptanceCorrectionInputId);
    }

    private void EnsureRealtimeWorld()
    {
        if (_character is null)
        {
            return;
        }
        if (_realtimeCharacterId == _character.Id &&
            _realtimeMapId == _character.CurrentMap &&
            _realtimeWorldGeneration != 0)
        {
            return;
        }

        _realtimeCharacterId = _character.Id;
        _realtimeMapId = _character.CurrentMap;
        _realtimeWorldGeneration =
            NextNonzero(_realtimeWorldGeneration);
        var prior = _authoritativePlayerMovement?.Snapshot;
        if (prior is { } priorSnapshot)
        {
            _authoritativePlayerMovement =
                new AuthoritativePlayerMovementSystem(
                    new AuthoritativePlayerMovementBaseline(
                        priorSnapshot.TransportEpoch,
                        _realtimeWorldGeneration,
                        _character.CurrentMap,
                        LocalPlayerObjectId,
                        RealtimeNeutralMovementState,
                        _character.PositionX,
                        _character.PositionZ,
                        Auxiliary: 1f,
                        ServerTimestamp:
                            _realtimeLastIngressElapsed,
                        AcknowledgedInputId:
                            priorSnapshot.AcknowledgedInputId,
                        PositionRevision: checked(
                            priorSnapshot.Revision + 1),
                        SimulationTick:
                            priorSnapshot.SimulationTick));
        }
        _realtimeSnapshotDirty = true;
        _realtimeKeyframePending = true;
    }

    private void ResetRealtimeWorldIfNeeded()
    {
        if (_character is not null &&
            _registered &&
            _worldPresenceAnnounced)
        {
            return;
        }

        _realtimeCharacterId = 0;
        _realtimeSnapshotDirty = false;
        _realtimeKeyframePending = false;
        _phase4UdpEvidencePending = false;
    }

    private void EnsureRealtimeAuthority(
        in SecureRealtimeMovementIngress ingress)
    {
        if (_authoritativePlayerMovement is not null ||
            _character is null)
        {
            return;
        }

        var baselineTimestamp =
            ingress.ServerReceiveElapsed >=
                AuthoritativePlayerMovementPolicy.FixedStep
                ? ingress.ServerReceiveElapsed -
                    AuthoritativePlayerMovementPolicy.FixedStep
                : TimeSpan.Zero;
        _authoritativePlayerMovement =
            new AuthoritativePlayerMovementSystem(
                new AuthoritativePlayerMovementBaseline(
                    ingress.Input.TransportEpoch,
                    _realtimeWorldGeneration,
                    _character.CurrentMap,
                    LocalPlayerObjectId,
                    RealtimeNeutralMovementState,
                    _character.PositionX,
                    _character.PositionZ,
                    Auxiliary: 1f,
                    baselineTimestamp));
    }

    private AuthoritativePlayerMovementInput
        BuildAuthoritativeInput(
            in SecureRealtimeMovementIngress ingress)
    {
        var currentWorld =
            (ingress.Input.Flags &
                SecureRealtimeMovementFlags.CurrentWorld) != 0;
        return new AuthoritativePlayerMovementInput(
            ingress.Input.TransportEpoch,
            ingress.Input.InputId,
            currentWorld
                ? _realtimeWorldGeneration
                : ingress.Input.WorldGeneration,
            currentWorld
                ? _character!.CurrentMap
                : ingress.Input.MapId,
            ingress.Input.LegacyState,
            ingress.Input.X,
            ingress.Input.Z,
            ingress.Input.Auxiliary,
            LocalPlayerObjectId,
            ingress.TransportSource ==
                SecureRealtimeTransportSource.Tls
                ? AuthoritativePlayerMovementSource.Tls
                : AuthoritativePlayerMovementSource.Udp,
            TargetsCurrentWorld: currentWorld ||
                ingress.Input.WorldGeneration ==
                    _realtimeWorldGeneration &&
                ingress.Input.MapId ==
                    _character!.CurrentMap);
    }

    private AuthoritativePlayerMovementDecision
        RejectRealtimeTransportEpoch(
            in AuthoritativePlayerMovementInput input,
            in AuthoritativePlayerMovementSnapshot snapshot)
    {
        return new AuthoritativePlayerMovementDecision(
            Accepted: false,
            AuthoritativePlayerMovementRejectionReason
                .TransportEpoch,
            snapshot.SimulationTick,
            snapshot.Revision,
            input.InputId,
            snapshot.AcknowledgedInputId,
            snapshot.TransportEpoch,
            snapshot.WorldGeneration,
            snapshot.MapId,
            snapshot.OpaqueState,
            snapshot.AuthoritativeX,
            snapshot.AuthoritativeZ,
            snapshot.AuthoritativeAuxiliary,
            input.Source);
    }

    private void PublishRealtimeSnapshotIfDue(
        AuthoritativePlayerMovementDecision? decision)
    {
        if (_character is null ||
            _realtimeWorldGeneration == 0)
        {
            return;
        }

        var correction =
            decision is { Accepted: false };
        var keyframe =
            _realtimeKeyframePending ||
            _realtimeServerTick == 1 ||
            _realtimeServerTick %
                RealtimeKeyframeTicks == 0;
        var changedSnapshot =
            _realtimeSnapshotDirty &&
            _realtimeServerTick %
                RealtimeSnapshotTicks == 0;
        if (!correction && !keyframe && !changedSnapshot)
        {
            return;
        }

        var movement =
            _authoritativePlayerMovement?.Snapshot;
        var rejection = decision is { Accepted: false }
            ? (SecureRealtimeMovementRejection)
                (byte)decision.Value.RejectionReason
            : SecureRealtimeMovementRejection.None;
        var flags = SecureRealtimeSnapshotFlags.None;
        if (keyframe)
        {
            flags |= SecureRealtimeSnapshotFlags.Keyframe;
        }
        if (correction)
        {
            flags |= SecureRealtimeSnapshotFlags.Correction;
        }

        _realtimeSnapshotSequence =
            IncrementSaturated(_realtimeSnapshotSequence);
        var snapshot = new SecureRealtimePositionSnapshot(
            flags,
            movement?.TransportEpoch ?? 1,
            decision?.AcknowledgedInputId ??
                movement?.AcknowledgedInputId ?? 0,
            Math.Max(1, _realtimeServerTick),
            movement?.Revision ?? 0,
            Math.Max(1, _realtimeSnapshotSequence),
            _realtimeWorldGeneration,
            movement?.OpaqueState ??
                RealtimeNeutralMovementState,
            movement?.AuthoritativeX ??
                _character.PositionX,
            movement?.AuthoritativeZ ??
                _character.PositionZ,
            movement?.AuthoritativeAuxiliary ?? 1f,
            _character.CurrentMap,
            rejection);
        if (!RevalidateCurrentWorldEffectOwnership(
                "realtime_snapshot_publish"))
        {
            return;
        }
        if (_session.TryPublishRealtimeSnapshot(snapshot))
        {
            if (_phase4UdpEvidencePending &&
                snapshot.Rejection ==
                    SecureRealtimeMovementRejection.None &&
                snapshot.TransportEpoch ==
                    _phase4UdpEvidenceTransportEpoch &&
                snapshot.AcknowledgedInputId >=
                    _phase4UdpEvidenceInputId)
            {
                ControlledHostPrivacyEvidence.RecordIfActive(
                    ControlledHostEvidenceEvent
                        .AuthoritativeUdpSnapshotQueued);
                _phase4UdpEvidencePending = false;
            }
            if (keyframe)
            {
                _realtimeKeyframePending = false;
            }
            if (!correction)
            {
                _realtimeSnapshotDirty = false;
            }
        }
    }

    private async Task RejectLegacyWalkAfterRealtimeCutoverAsync(
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return;
        }

        await _session.SendAsync(
            PacketBuilder.PlayerWorldPosition(
                _character,
                LocalPlayerObjectId),
            cancellationToken,
            "RealtimeLegacyWalkRejected");
    }

}
