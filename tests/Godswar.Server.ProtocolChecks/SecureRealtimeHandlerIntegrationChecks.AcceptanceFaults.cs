using System.Net;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureRealtimeHandlerIntegrationChecks
{
    private static async Task
        CheckAcceptanceFaultCorrectionAsync()
    {
        await using var transport =
            new RealtimeMovementControlTransport();
        await using var session =
            new ClientSession(transport);
        var character = CreateCharacter(
            CharacterId + 30,
            AccountId + 30,
            "RealtimeAcceptanceFault");
        var registry = new GameSessionRegistry();
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [],
            TestTime);
        registry.JoinMap(
            session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id));
        var faultTime = new ManualTimeProvider();
        var faults = new SecurePhase4AcceptanceFaults(
            faultTime);
        var handler = CreateReadyHandler(
            session,
            registry,
            character,
            faults);

        await ProcessTickAsync(handler);
        var initial = transport.Snapshots.Single();
        const uint acceptedState = 0xCAFE_0001;
        transport.EnqueueMovement(
            CreateIngress(
                SecureRealtimeTransportSource.Udp,
                SecureRealtimeMovementIngressKind.Input,
                transportEpoch: 1,
                inputId: 1,
                initial.WorldGeneration,
                acceptedState,
                x: 0.5f,
                z: 0.25f,
                TimeSpan.FromMilliseconds(100)));
        var acceptedEffects =
            await ProcessTickAsync(handler);
        Check.True(
            GetEffectPacket(
                acceptedEffects,
                "ViewerMovement") is not null,
            "acceptance setup input is ordinarily accepted");
        var acceptedSnapshot = transport.Snapshots.Last();
        Check.True(
            acceptedSnapshot.Rejection ==
                SecureRealtimeMovementRejection.None &&
            acceptedSnapshot.AcknowledgedInputId == 1 &&
            acceptedSnapshot.PositionRevision == 1,
            "acceptance setup establishes an acknowledged UDP position");

        Check.True(
            SecureUdpConnectionKey.TryCreate(
                transport.ConnectionContext.ConnectionId.Span,
                out var connectionId),
            "test secure connection ID maps to the UDP authority key");
        var dispatch = new SecureRealtimeSnapshotDispatch(
            connectionId,
            new IPEndPoint(IPAddress.Loopback, 7444),
            BindingRevision: 1,
            acceptedSnapshot);
        Check.True(
            faults.ShouldDropSnapshot(dispatch),
            "accepted UDP ACK arms the controlled-host campaign");

        transport.EnqueueMovement(
            CreateIngress(
                SecureRealtimeTransportSource.Tls,
                SecureRealtimeMovementIngressKind
                    .TransportTransition,
                transportEpoch: 2,
                inputId: 1,
                initial.WorldGeneration,
                acceptedState,
                x: 0.5f,
                z: 0.25f,
                TimeSpan.FromMilliseconds(200)));
        var transitionEffects =
            await ProcessTickAsync(handler);
        AssertNoMovementEffects(
            transitionEffects,
            "acceptance TLS transition retry");

        transport.EnqueueMovement(
            CreateIngress(
                SecureRealtimeTransportSource.Tls,
                SecureRealtimeMovementIngressKind.Input,
                transportEpoch: 2,
                inputId: 2,
                initial.WorldGeneration,
                legacyState: 0xBEEF_0002,
                x: 0.75f,
                z: 0.5f,
                TimeSpan.FromMilliseconds(300)));
        var correctionEffects =
            await ProcessTickAsync(handler);
        Check.True(
            GetEffectPacket(
                correctionEffects,
                "ViewerMovement") is null,
            "forced correction cannot broadcast viewer movement");
        Check.True(
            correctionEffects.GetType()
                .GetProperty("PositionSave")?
                .GetValue(correctionEffects) is null,
            "forced correction cannot enqueue position persistence");
        var reliableCorrection =
            GetEffectPacket(
                correctionEffects,
                "ReliableCorrection")
            ?? throw new InvalidOperationException(
                "Acceptance fault produced no reliable correction.");
        AssertCanonicalWalk(
            reliableCorrection,
            LocalPlayerObjectId,
            acceptedState,
            expectedX: 0.5f,
            expectedZ: 0.25f,
            "acceptance NotReady correction");
        Check.True(
            character.PositionX == 0.5f &&
            character.PositionZ == 0.25f,
            "forced correction leaves authoritative position unchanged");
        var pendingEvidence = faults.GetSnapshot();
        Check.True(
            pendingEvidence.ForcedCorrections == 0 &&
            pendingEvidence.State ==
                SecurePhase4AcceptanceFaultState
                    .AwaitingTlsFallback,
            "correction intent is not counted before reliable egress");

        var correctionSnapshot = transport.Snapshots.Last();
        Check.True(
            (correctionSnapshot.Flags &
                SecureRealtimeSnapshotFlags.Correction) != 0 &&
            correctionSnapshot.Rejection ==
                SecureRealtimeMovementRejection.NotReady &&
            correctionSnapshot.TransportEpoch == 2 &&
            correctionSnapshot.AcknowledgedInputId == 2 &&
            correctionSnapshot.PositionRevision == 1 &&
            correctionSnapshot.X == 0.5f &&
            correctionSnapshot.Z == 0.25f,
            "forced fault reuses sequenced authoritative NotReady correction");
        await PublishEffectsAsync(
            handler,
            correctionEffects);
        Check.True(
            transport.TakeClearLegacyWrites()
                .SequenceEqual(reliableCorrection),
            "forced correction is delivered on the reliable TLS stream");
        var deliveredEvidence = faults.GetSnapshot();
        Check.True(
            deliveredEvidence.ForcedCorrections == 1 &&
            deliveredEvidence.State ==
                SecurePhase4AcceptanceFaultState
                    .CorrectionForced,
            "successful reliable write commits correction evidence");

        transport.EnqueueMovement(
            CreateIngress(
                SecureRealtimeTransportSource.Tls,
                SecureRealtimeMovementIngressKind.Input,
                transportEpoch: 2,
                inputId: 3,
                initial.WorldGeneration,
                legacyState: 0xBEEF_0003,
                x: 0.75f,
                z: 0.5f,
                TimeSpan.FromMilliseconds(400)));
        var resumedEffects =
            await ProcessTickAsync(handler);
        Check.True(
            GetEffectPacket(
                resumedEffects,
                "ViewerMovement") is not null &&
            GetEffectPacket(
                resumedEffects,
                "ReliableCorrection") is null &&
            resumedEffects.GetType()
                .GetProperty("PositionSave")?
                .GetValue(resumedEffects) is not null,
            "next TLS input resumes normal authoritative movement");
        Check.True(
            character.PositionX == 0.75f &&
            character.PositionZ == 0.5f,
            "post-correction TLS movement advances authoritative position");

        var evidence = faults.GetSnapshot();
        Check.True(
            evidence.State ==
                SecurePhase4AcceptanceFaultState.Complete &&
            evidence.TlsFallbackObserved &&
            evidence.ForcedCorrections == 1 &&
            evidence.TlsNoSwitchbackObserved,
            "handler path completes one fallback and one correction");

        registry.Remove(session);
    }

    private static async Task
        CheckFailedAcceptanceCorrectionEgressAsync()
    {
        await using var transport =
            new RealtimeMovementControlTransport();
        await using var session =
            new ClientSession(transport);
        var character = CreateCharacter(
            CharacterId + 31,
            AccountId + 31,
            "RealtimeFailedFaultEgress");
        var registry = new GameSessionRegistry();
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [],
            TestTime);
        registry.JoinMap(
            session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id));
        var faults = new SecurePhase4AcceptanceFaults(
            new ManualTimeProvider());
        var handler = CreateReadyHandler(
            session,
            registry,
            character,
            faults);

        await ProcessTickAsync(handler);
        var initial = transport.Snapshots.Single();
        transport.EnqueueMovement(
            CreateIngress(
                SecureRealtimeTransportSource.Udp,
                SecureRealtimeMovementIngressKind.Input,
                transportEpoch: 1,
                inputId: 1,
                initial.WorldGeneration,
                legacyState: 0xCAFE_0001,
                x: 0.5f,
                z: 0.25f,
                TimeSpan.FromMilliseconds(100)));
        await ProcessTickAsync(handler);
        Check.True(
            SecureUdpConnectionKey.TryCreate(
                transport.ConnectionContext.ConnectionId.Span,
                out var connectionId),
            "failed-egress test maps its secure connection ID");
        Check.True(
            faults.ShouldDropSnapshot(
                new SecureRealtimeSnapshotDispatch(
                    connectionId,
                    new IPEndPoint(IPAddress.Loopback, 7444),
                    BindingRevision: 1,
                    transport.Snapshots.Last())),
            "failed-egress test arms the acceptance campaign");

        transport.EnqueueMovement(
            CreateIngress(
                SecureRealtimeTransportSource.Tls,
                SecureRealtimeMovementIngressKind.Input,
                transportEpoch: 2,
                inputId: 2,
                initial.WorldGeneration,
                legacyState: 0xBEEF_0002,
                x: 0.75f,
                z: 0.5f,
                TimeSpan.FromMilliseconds(200)));
        var effects = await ProcessTickAsync(handler);
        Check.True(
            GetEffectPacket(
                effects,
                "ReliableCorrection") is not null,
            "failed-egress setup produces a reliable correction");

        transport.FailNextWrite();
        Exception? failure = null;
        try
        {
            await PublishEffectsAsync(handler, effects);
        }
        catch (Exception error)
        {
            failure = error;
        }
        Check.True(
            failure is IOException,
            "failed reliable correction write reaches the handler");
        var evidence = faults.GetSnapshot();
        Check.True(
            evidence.ForcedCorrections == 0 &&
            evidence.State ==
                SecurePhase4AcceptanceFaultState
                    .AwaitingTlsFallback,
            "failed reliable write cannot commit correction evidence");

        registry.Remove(session);
    }
}
