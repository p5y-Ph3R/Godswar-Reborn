using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureRealtimeSessionAuthorityChecks
{
    public static async Task RunAsync()
    {
        CheckDisabledCapability();
        CheckTlsIngressAndFallbackReconciliation();
        CheckQueuedTransitionPreservesUnappliedInput();
        CheckExpiredUdpOfferRetainsTlsFallback();
        await CheckInitialSnapshotAndEndpointEgressAsync();
        CheckSnapshotReadyQueueBound();
    }

    private static void CheckDisabledCapability()
    {
        using var authority = CreateAuthority(
            gameplayMovementEnabled: false,
            capacity: 1);
        using var registration = Register(
            authority,
            connectionSeed: 1);
        var lease = registration.Lease;
        var payload = EncodeTlsInput(
            SecureRealtimeMovementProtocolChecks.CreateInput());
        Check.True(
            lease.Capabilities ==
                SecureUdpBindingCapabilities.None &&
            !lease.SupportsRealtimeMovement &&
            !lease.IsRealtimeMovementActive &&
            lease.OfferTlsMovement(payload).Status ==
                SecureRealtimeMovementOfferStatus.FeatureDisabled &&
            !lease.TryTakeRealtimeMovement(out _) &&
            !lease.TryPublishRealtimeSnapshot(
                SecureRealtimeMovementProtocolChecks.CreateSnapshot(
                    SecureRealtimeSnapshotFlags.Keyframe,
                    SecureRealtimeMovementRejection.None)),
            "disabled authoritative movement preserves Phase 3 behavior");
    }

    private static void CheckTlsIngressAndFallbackReconciliation()
    {
        var time = new ManualTimeProvider();
        time.Advance(TimeSpan.FromSeconds(1));
        using var authority = CreateAuthority(
            gameplayMovementEnabled: true,
            capacity: 2,
            time);
        using var registration = Register(
            authority,
            connectionSeed: 17);
        var lease = registration.Lease;
        Check.True(
            lease.Capabilities ==
                SecureUdpBindingCapabilities.AuthoritativeMovement &&
            lease.SupportsRealtimeMovement &&
            !lease.IsRealtimeMovementActive,
            "capability is distinct from active source ownership");

        var initial = SecureRealtimeMovementProtocolChecks
            .CreateSnapshot(
                SecureRealtimeSnapshotFlags.Keyframe,
                SecureRealtimeMovementRejection.None) with
        {
            AcknowledgedInputId = 0,
            PositionRevision = 0
        };
        Check.True(
            lease.TryPublishRealtimeSnapshot(initial),
            "initial keyframe publishes before source ownership");

        time.Advance(TimeSpan.FromMilliseconds(125));
        var firstInput = SecureRealtimeMovementProtocolChecks
            .CreateInput(
                SecureRealtimeMovementFlags.CurrentWorld,
                epoch: 1,
                inputId: 10);
        var first = lease.OfferTlsMovement(
            EncodeTlsInput(firstInput));
        Check.True(
            first.Status ==
                SecureRealtimeMovementOfferStatus.Accepted &&
            lease.IsRealtimeMovementActive &&
            lease.TryTakeRealtimeMovement(out var ingress) &&
            ingress.Input == firstInput &&
            ingress.TransportSource ==
                SecureRealtimeTransportSource.Tls &&
            ingress.ServerReceiveElapsed ==
                TimeSpan.FromMilliseconds(125),
            "TLS offer stores inferred source and server monotonic time");

        var secondInput = firstInput with
        {
            InputId = 11,
            ClientMonotonicMilliseconds = 10_001
        };
        var latestInput = firstInput with
        {
            InputId = 12,
            ClientMonotonicMilliseconds = 10_002
        };
        Check.True(
            lease.OfferTlsMovement(
                EncodeTlsInput(secondInput)).Status ==
                    SecureRealtimeMovementOfferStatus.Accepted &&
            lease.OfferTlsMovement(
                EncodeTlsInput(latestInput)).Status ==
                    SecureRealtimeMovementOfferStatus.Replaced &&
            lease.TryTakeRealtimeMovement(out var latest) &&
            latest.Input == latestInput,
            "session ingress is capacity-one replace-stale");

        Check.True(
            lease.OfferTlsMovement(
                EncodeTlsInput(latestInput)).Status ==
                    SecureRealtimeMovementOfferStatus.Duplicate &&
            !lease.TryTakeRealtimeMovement(out _),
            "logical input IDs deduplicate without a second projection");

        using var directFallbackRegistration = Register(
            authority,
            connectionSeed: 81);
        var directFallbackInput =
            SecureRealtimeMovementProtocolChecks.CreateInput(
                SecureRealtimeMovementFlags.CurrentWorld,
                epoch: 2,
                inputId: 90);
        Check.True(
            directFallbackRegistration.Lease.OfferTlsMovement(
                EncodeTlsInput(directFallbackInput)).IsAccepted &&
            directFallbackRegistration.Lease
                .TryTakeRealtimeMovement(
                    out var directFallbackIngress) &&
            directFallbackIngress.Input == directFallbackInput,
            "lost first UDP datagram falls back directly to TLS epoch two");
    }

    private static async Task
        CheckInitialSnapshotAndEndpointEgressAsync()
    {
        using var authority = CreateAuthority(
            gameplayMovementEnabled: true,
            capacity: 2);
        using var registration = Register(
            authority,
            connectionSeed: 33);
        var lease = registration.Lease;
        var grant = CopyGrant(lease);
        try
        {
            using var client = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp);
            client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var remote = (IPEndPoint)client.LocalEndPoint!;
            var bindingRevision = Bind(
                authority,
                grant,
                remote);

            using var cookies = CreateCookies();
            using var validation =
                new SecureUdpAddressValidation(1_200, cookies);
            var limiter = new SecureUdpRateLimiter(
                globalLimit: 100,
                prefixLimit: 100,
                prefixCapacity: 8);
            var endpoint = new SecureUdpEndpointServer(
                "127.0.0.1",
                port: 0,
                maximumDatagramBytes: 1_200,
                new SecureUdpBindingCoordinator(
                    validation,
                    authority),
                limiter,
                authority);
            using var lifetime = new CancellationTokenSource(
                TimeSpan.FromSeconds(10));
            var runTask = endpoint.RunAsync(lifetime.Token);
            await endpoint.WaitUntilStartedAsync(lifetime.Token);

            using var protectedClient =
                new SecureUdpProtectedSession(
                    SecureUdpPeerRole.Client,
                    grant.ProofKey,
                    grant.ConnectionId,
                    ServerId,
                    TimeSpan.FromSeconds(10));
            var initial = SecureRealtimeMovementProtocolChecks
                .CreateSnapshot(
                    SecureRealtimeSnapshotFlags.Keyframe,
                    SecureRealtimeMovementRejection.None) with
            {
                AcknowledgedInputId = 0,
                PositionRevision = 0
            };
            Check.True(
                lease.TryPublishRealtimeSnapshot(initial),
                "bound initial keyframe enters snapshot egress");
            var datagram = await ReceiveDatagramAsync(
                client,
                lifetime.Token);
            var plaintext = new byte[1_120];
            Check.True(
                protectedClient.TryUnprotect(
                    datagram,
                    plaintext,
                    out var header,
                    out var payloadBytes,
                    out _) &&
                header.MessageType ==
                    SecureUdpProtectedMessageType.PositionSnapshot &&
                payloadBytes == 64 &&
                SecureRealtimeMovementProtocol
                    .TryDecodePositionSnapshot(
                        plaintext.AsSpan(0, payloadBytes),
                        out var decoded) &&
                decoded == initial,
                "snapshot egress resolves the live bound endpoint");

            var movement = SecureRealtimeMovementProtocolChecks
                .CreateInput(inputId: 20);
            var movementPayload = new byte[52];
            Check.True(
                SecureRealtimeMovementProtocol.TryEncodeMovementInput(
                    movement,
                    SecureRealtimeTransportSource.Udp,
                    movementPayload,
                    out _),
                "endpoint movement payload encodes");
            var protectedMovement = new byte[1_200];
            Check.True(
                protectedClient.TryProtect(
                    SecureUdpProtectedMessageType.MovementInput,
                    movementPayload,
                    protectedMovement,
                    out var movementBytes,
                    out _),
                "endpoint movement datagram protects");
            var response = new byte[1_200];
            var accepted = endpoint.ProcessDatagram(
                protectedMovement.AsSpan(0, movementBytes),
                remote,
                response);
            Check.True(
                accepted.Outcome ==
                    SecureUdpDatagramOutcome
                        .RealtimeMovementAccepted &&
                accepted.ResponseBytes == 0 &&
                lease.TryTakeRealtimeMovement(out var ingress) &&
                ingress.Input == movement &&
                ingress.TransportSource ==
                    SecureRealtimeTransportSource.Udp,
                "endpoint routes authenticated movement into one session mailbox");

            Check.True(
                protectedClient.TryProtect(
                    SecureUdpProtectedMessageType.MovementInput,
                    movementPayload,
                    protectedMovement,
                    out movementBytes,
                    out _),
                "duplicate logical movement re-encrypts");
            var duplicate = endpoint.ProcessDatagram(
                protectedMovement.AsSpan(0, movementBytes),
                remote,
                response);
            Check.True(
                duplicate.Outcome ==
                    SecureUdpDatagramOutcome
                        .RealtimeMovementDeduplicated &&
                !lease.TryTakeRealtimeMovement(out _),
                "new UDP sequence cannot bypass logical-ID dedupe");

            var tlsFallback = movement with
            {
                Flags =
                    SecureRealtimeMovementFlags.CurrentWorld,
                TransportEpoch = 2
            };
            Check.True(
                lease.OfferTlsMovement(
                    EncodeTlsInput(tlsFallback)).Status ==
                    SecureRealtimeMovementOfferStatus
                        .TransportChangedDuplicate &&
                lease.TryTakeRealtimeMovement(
                    out var transition) &&
                transition.Kind ==
                    SecureRealtimeMovementIngressKind
                        .TransportTransition &&
                transition.Input.InputId == movement.InputId &&
                transition.Input.TransportEpoch == 2 &&
                transition.TransportSource ==
                    SecureRealtimeTransportSource.Tls,
                "ambiguous UDP-to-TLS retry changes source without reapply");
            var tlsNext = tlsFallback with
            {
                InputId = 21,
                ClientMonotonicMilliseconds = 10_001
            };
            Check.True(
                lease.OfferTlsMovement(
                    EncodeTlsInput(tlsNext)).IsAccepted &&
                lease.TryTakeRealtimeMovement(out var fallbackIngress) &&
                fallbackIngress.TransportSource ==
                    SecureRealtimeTransportSource.Tls,
                "newer TLS fallback movement applies");
            var switchback = movement with
            {
                TransportEpoch = 3,
                InputId = 22,
                ClientMonotonicMilliseconds = 10_002
            };
            Check.True(
                authority.OfferUdpMovement(
                    SecureUdpConnectionKeyFrom(grant.ConnectionId),
                    bindingRevision,
                    switchback).Status ==
                    SecureRealtimeMovementOfferStatus
                        .TransportEpochRejected,
                "Phase 4 rejects authenticated UDP switchback churn");

            lifetime.Cancel();
            await runTask;
        }
        finally
        {
            grant.Clear();
        }
    }

    private static OwnedRegistration Register(
        SecureUdpSessionAuthority authority,
        int connectionSeed)
    {
        var connectionId = Enumerable.Range(
                connectionSeed,
                SecureProtocolConstants.ConnectionIdBytes)
            .Select(static value => unchecked((byte)value))
            .ToArray();
        if (connectionId.All(static value => value == 0))
        {
            connectionId[0] = 1;
        }
        var context = new SecureConnectionContext(
            SecureEndpointRole.Game,
            SecureProtocolConstants.ProtocolMajor,
            SecureProtocolConstants.ProtocolMinor,
            connectionId,
            Enumerable.Repeat((byte)0x31, 16).ToArray(),
            Convert.FromHexString(
                SecureNetworkOptions.PredecessorOriginSha256));
        var principal = new SecureBoundGamePrincipal(
            connectionSeed,
            $"account-{connectionSeed}",
            SecureGamePermissions.EnterWorld,
            Guid.NewGuid());
        var result = authority.Register(context, principal);
        Check.True(
            result.IsRegistered,
            "realtime authority fixture registers");
        return new OwnedRegistration(result.Lease!);
    }

    private static SecureUdpSessionAuthority CreateAuthority(
        bool gameplayMovementEnabled,
        int capacity,
        TimeProvider? timeProvider = null) =>
        new(
            capacity,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(2),
            ServerId,
            TimeSpan.FromSeconds(10),
            gameplayMovementEnabled,
            timeProvider);

    private static CapturedGrant CopyGrant(
        SecureUdpSessionLease lease)
    {
        var connectionId = new byte[16];
        var proofKey = new byte[32];
        Check.True(
            lease.TryCopyGrantMaterial(
                connectionId,
                proofKey,
                out _),
            "realtime grant material copies");
        return new CapturedGrant(connectionId, proofKey);
    }

    private static ulong Bind(
        SecureUdpSessionAuthority authority,
        CapturedGrant grant,
        IPEndPoint remote)
    {
        var nonce = Enumerable.Range(1, 16)
            .Select(static value => checked((byte)value))
            .ToArray();
        var cookie = Enumerable.Range(0x41, 32)
            .Select(static value => checked((byte)value))
            .ToArray();
        var challenge = new byte[128];
        Check.True(
            SecureUdpBindingCodec.TryEncode(
                SecureUdpBindingType.ServerChallenge,
                grant.ConnectionId,
                keyEpoch: 1,
                sequence: 0,
                nonce,
                issuedAtUnixSeconds: 1,
                cookie,
                challenge,
                out var written) &&
            written == challenge.Length,
            "realtime binding challenge encodes");
        Span<byte> proof = stackalloc byte[24];
        Check.True(
            SecureUdpTlsProofAuthenticator.TryCompute(
                grant.ProofKey,
                challenge,
                proof),
            "realtime TLS proof computes");
        var status = authority.TryBind(
            grant.ConnectionId,
            challenge,
            proof,
            remote,
            out _,
            out var revision);
        Check.True(
            status == SecureUdpSessionBindStatus.Bound &&
            revision == 1,
            "realtime session binds to the test endpoint");
        return revision;
    }

    private static byte[] EncodeTlsInput(
        SecureRealtimeMovementInput input)
    {
        var payload = new byte[52];
        Check.True(
            SecureRealtimeMovementProtocol.TryEncodeMovementInput(
                input,
                SecureRealtimeTransportSource.Tls,
                payload,
                out var written) &&
            written == payload.Length,
            "TLS movement fixture encodes");
        return payload;
    }

    private static SecureRealtimePositionSnapshot InitialSnapshot() =>
        SecureRealtimeMovementProtocolChecks.CreateSnapshot(
            SecureRealtimeSnapshotFlags.Keyframe,
            SecureRealtimeMovementRejection.None) with
        {
            AcknowledgedInputId = 0,
            PositionRevision = 0
        };

    private static SecureUdpConnectionKey SecureUdpConnectionKeyFrom(
        ReadOnlySpan<byte> connectionId)
    {
        Check.True(
            SecureUdpConnectionKey.TryCreate(
                connectionId,
                out var key),
            "realtime connection key fixture");
        return key;
    }

    private static SecureUdpCookieProtector CreateCookies() =>
        new(
            new SecureUdpCookiePolicy(
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(60)),
            ServerId,
            udpPort: 7_444,
            audience: "reborn-game");

    private static async Task<byte[]> ReceiveDatagramAsync(
        Socket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1_200];
        EndPoint template = new IPEndPoint(IPAddress.Any, 0);
        var result = await socket.ReceiveFromAsync(
            buffer,
            SocketFlags.None,
            template,
            cancellationToken);
        return buffer[..result.ReceivedBytes];
    }

    private const uint ServerId = 100;

    private sealed class OwnedRegistration(
        SecureUdpSessionLease lease) : IDisposable
    {
        public SecureUdpSessionLease Lease { get; } = lease;

        public void Dispose() => Lease.Dispose();
    }

    private sealed record CapturedGrant(
        byte[] ConnectionId,
        byte[] ProofKey)
    {
        public void Clear()
        {
            CryptographicOperations.ZeroMemory(ConnectionId);
            CryptographicOperations.ZeroMemory(ProofKey);
        }
    }
}
