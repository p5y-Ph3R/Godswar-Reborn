using System.Security.Cryptography;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureTlsTransportChecks
{
    private static async Task CheckUdpBindingGrantAssociationAsync()
    {
        using var certificate = SecureTlsTestCertificate.Create();
        var options = CreateRuntimeOptions();
        using var gate = new TlsHandshakeGate(1);
        using var ticketStore = new InMemoryGameTicketStore();
        using var udpAuthority = new SecureUdpSessionAuthority(
            capacity: 1,
            pendingTtl: TimeSpan.FromSeconds(30));
        var target = new SecureGameTarget(
            "game.reborn.test",
            "game.reborn.test",
            "reborn-game",
            routePort: 7_000,
            tlsPort: 7_443,
            serverId: 100);
        var clientInstanceId = Enumerable.Range(1, 16)
            .Select(static value => checked((byte)value))
            .ToArray();
        var loginContext = new SecureConnectionContext(
            SecureEndpointRole.Login,
            SecureProtocolConstants.ProtocolMajor,
            SecureProtocolConstants.ProtocolMinor,
            Enumerable.Repeat((byte)0x61, 16).ToArray(),
            clientInstanceId,
            Convert.FromHexString(
                SecureNetworkOptions.PredecessorOriginSha256));
        using var grantLease = IssueCommittedGrant(
            ticketStore,
            target,
            loginContext);
        var secureOptions = new SecureNetworkOptions();
        var factory = new TlsMuxLegacyTransportFactory(
            secureOptions,
            options,
            certificate.Context,
            gate,
            timeProvider: null,
            ticketStore: ticketStore,
            gameTarget: target,
            udpSessionAuthority: udpAuthority);
        await using var pair = await StartPairAsync(
            factory,
            NetworkEndpointRole.Game);
        var preface = await AuthenticateAndPrefaceAsync(
            pair.ClientStream,
            certificate,
            SecureEndpointRole.Game,
            targetHost: "game.reborn.test");
        await PresentGrantAsync(
            pair.ClientStream,
            grantLease.Grant);

        var bindResult = await ReadFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expectedSequence: 1);
        Check.True(
            SecureGameControlCodec.TryDecodeBindResult(
                bindResult.Payload,
                out var status) &&
            status.Status == SecureBindStatus.Accepted,
            "UDP association follows accepted game bind");
        var udpFrame = await ReadFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expectedSequence: 2);
        Check.Equal(
            (int)SecureFrameType.UdpBindingGrant,
            (int)udpFrame.Header.Type,
            "TLS delivers UDP material as isolated control frame");
        Check.True(
            SecureUdpBindingGrantCodec.TryDecode(
                udpFrame.Payload,
                out var udpGrant),
            "TLS UDP binding grant decodes");
        using (udpGrant)
        {
            var grantConnectionId = new byte[16];
            var proofKey = new byte[32];
            Check.True(
                udpGrant!.TryCopySecrets(
                    grantConnectionId,
                    proofKey) &&
                grantConnectionId.SequenceEqual(
                    preface.ConnectionId.Span),
                "TLS grant retains exact server-preface connection ID");
            Check.True(
                !SecureProtocolValidation.IsAllZero(proofKey),
                "TLS grant carries fresh nonzero proof material");
            CryptographicOperations.ZeroMemory(proofKey);
        }

        var transport =
            (TlsMuxLegacyTransport)await pair.TransportTask;
        Check.True(
            transport.ConnectionContext.ConnectionId.Span.SequenceEqual(
                preface.ConnectionId.Span),
            "server transport retains exact TLS connection ID");
        Check.Equal(
            1,
            udpAuthority.GetSnapshot().PendingSessions,
            "accepted game TLS owns one pending UDP registration");
        await transport.DisposeAsync();
        Check.Equal(
            0,
            udpAuthority.GetSnapshot().TrackedSessions,
            "TLS disposal immediately revokes UDP registration");
    }

    private static async Task CheckUdpCapacityFallsBackToTlsAsync()
    {
        using var certificate = SecureTlsTestCertificate.Create();
        var options = CreateRuntimeOptions();
        using var gate = new TlsHandshakeGate(1);
        using var ticketStore = new InMemoryGameTicketStore();
        using var udpAuthority = new SecureUdpSessionAuthority(
            capacity: 1,
            pendingTtl: TimeSpan.FromSeconds(30));
        var blockerContext = new SecureConnectionContext(
            SecureEndpointRole.Game,
            SecureProtocolConstants.ProtocolMajor,
            SecureProtocolConstants.ProtocolMinor,
            Enumerable.Repeat((byte)0x71, 16).ToArray(),
            Enumerable.Repeat((byte)0x72, 16).ToArray(),
            Convert.FromHexString(
                SecureNetworkOptions.PredecessorOriginSha256));
        var blocker = udpAuthority.Register(
            blockerContext,
            new SecureBoundGamePrincipal(
                99,
                "udp-capacity-blocker",
                SecureGamePermissions.EnterWorld,
                Guid.Parse(
                    "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE")));
        Check.True(
            blocker.IsRegistered,
            "TLS fallback fixture fills UDP authority");
        using var blockerLease = blocker.Lease!;

        var target = new SecureGameTarget(
            "game.reborn.test",
            "game.reborn.test",
            "reborn-game",
            routePort: 7_000,
            tlsPort: 7_443,
            serverId: 100);
        var loginContext = new SecureConnectionContext(
            SecureEndpointRole.Login,
            SecureProtocolConstants.ProtocolMajor,
            SecureProtocolConstants.ProtocolMinor,
            Enumerable.Repeat((byte)0x73, 16).ToArray(),
            Enumerable.Range(1, 16)
                .Select(static value => checked((byte)value))
                .ToArray(),
            Convert.FromHexString(
                SecureNetworkOptions.PredecessorOriginSha256));
        using var grantLease = IssueCommittedGrant(
            ticketStore,
            target,
            loginContext);
        var factory = new TlsMuxLegacyTransportFactory(
            new SecureNetworkOptions(),
            options,
            certificate.Context,
            gate,
            ticketStore: ticketStore,
            gameTarget: target,
            udpSessionAuthority: udpAuthority);
        await using var pair = await StartPairAsync(
            factory,
            NetworkEndpointRole.Game);
        _ = await AuthenticateAndPrefaceAsync(
            pair.ClientStream,
            certificate,
            SecureEndpointRole.Game,
            targetHost: "game.reborn.test");
        await PresentGrantAsync(
            pair.ClientStream,
            grantLease.Grant);
        var bindResult = await ReadFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expectedSequence: 1);
        Check.True(
            SecureGameControlCodec.TryDecodeBindResult(
                bindResult.Payload,
                out var bindStatus) &&
            bindStatus.Status == SecureBindStatus.Accepted,
            "UDP capacity does not reject TLS game bind");

        var transport =
            (TlsMuxLegacyTransport)await pair.TransportTask;
        var payload = Enumerable.Range(1, 19)
            .Select(static value => checked((byte)value))
            .ToArray();
        var write = transport.WriteAsync(
            payload,
            CancellationToken.None).AsTask();
        var frame = await ReadFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expectedSequence: 2);
        await write;
        Check.True(
            frame.Header.Type == SecureFrameType.LegacyBytes &&
            frame.Payload.SequenceEqual(payload),
            "full UDP authority omits grant and preserves TLS-only sequence");
        Check.Equal(
            1,
            udpAuthority.GetSnapshot().TrackedSessions,
            "TLS fallback does not displace established UDP registration");
        await transport.DisposeAsync();
    }

    private static SecureGameGrantLease IssueCommittedGrant(
        InMemoryGameTicketStore ticketStore,
        SecureGameTarget target,
        SecureConnectionContext loginContext)
    {
        var generation = ticketStore.BeginLogin(7, "test2");
        Check.True(
            generation.IsStarted,
            "UDP association login generation starts");
        var issued = ticketStore.Issue(
            generation.Generation!,
            loginContext,
            target);
        Check.True(
            issued.IsIssued,
            "UDP association game ticket issues");
        Check.True(
            issued.Lease!.Commit(),
            "UDP association game ticket commits");
        return issued.Lease!;
    }

    private static async Task PresentGrantAsync(
        Stream stream,
        SecureGameGrant grant)
    {
        var grantId = new byte[SecureProtocolConstants.GrantIdBytes];
        var ticket = new byte[SecureProtocolConstants.TicketBytes];
        var payload = new byte[SecureProtocolConstants.GameBindBytes];
        try
        {
            Check.True(
                grant.TryCopySecrets(grantId, ticket),
                "UDP association grant secrets copy");
            using var bind = new SecureGameBind(grantId, ticket);
            Check.True(
                SecureGameControlCodec.TryEncodeBind(
                    bind,
                    payload,
                    out var written) &&
                written == payload.Length,
                "UDP association bind encodes");
            await WriteFrameAsync(
                (System.Net.Security.SslStream)stream,
                SecureEndpointRole.Game,
                SecureFrameType.GameBind,
                sequence: 1,
                payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(grantId);
            CryptographicOperations.ZeroMemory(ticket);
            CryptographicOperations.ZeroMemory(payload);
        }
    }
}
