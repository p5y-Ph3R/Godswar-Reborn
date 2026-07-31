using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureTlsTransportChecks
{
    private static async Task CheckRealtimeTlsFallbackRoutingAsync()
    {
        using var certificate = SecureTlsTestCertificate.Create();
        var runtimeOptions = CreateRuntimeOptions();
        using var gate = new TlsHandshakeGate(1);
        using var ticketStore = new InMemoryGameTicketStore();
        using var udpAuthority = new SecureUdpSessionAuthority(
            capacity: 1,
            pendingTtl: TimeSpan.FromSeconds(30),
            boundIdleTimeout: TimeSpan.FromSeconds(30),
            minimumRebindInterval: TimeSpan.FromSeconds(2),
            serverId: 100,
            previousEpochOverlap: TimeSpan.FromSeconds(10),
            gameplayMovementEnabled: true);
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
            Enumerable.Repeat((byte)0x51, 16).ToArray(),
            clientInstanceId,
            Convert.FromHexString(
                SecureNetworkOptions.PredecessorOriginSha256));
        await using var gameGrant = await IssueCommittedGrantAsync(
            ticketStore,
            target,
            loginContext);
        var secureOptions = new SecureNetworkOptions
        {
            Udp = new SecureUdpOptions
            {
                Port = 7_444,
                GameplayMovementEnabled = true
            }
        };
        var factory = new TlsMuxLegacyTransportFactory(
            secureOptions,
            runtimeOptions,
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
            gameGrant.Grant);

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
            "realtime TLS fixture binds game ticket");
        var udpGrantFrame = await ReadFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expectedSequence: 2);
        Check.True(
            SecureUdpBindingGrantCodec.TryDecode(
                udpGrantFrame.Payload,
                out var udpGrant) &&
            udpGrant!.Capabilities.HasFlag(
                SecureUdpBindingCapabilities
                    .AuthoritativeMovement),
            "TLS grant negotiates authoritative movement");
        udpGrant!.Dispose();

        var transport =
            (TlsMuxLegacyTransport)await pair.TransportTask;
        Check.True(
            transport.SupportsRealtimeMovement &&
            !transport.IsRealtimeMovementActive,
            "TLS transport distinguishes capability from active ingress");
        var input =
            SecureRealtimeMovementProtocolChecks.CreateInput(
                SecureRealtimeMovementFlags.CurrentWorld,
                epoch: 1,
                inputId: 500);
        var payload = new byte[52];
        Check.True(
            SecureRealtimeMovementProtocol.TryEncodeMovementInput(
                input,
                SecureRealtimeTransportSource.Tls,
                payload,
                out var written) &&
            written == payload.Length,
            "TLS fallback movement payload encodes");
        await WriteFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameType.RealtimeMovementInput,
            sequence: 2,
            payload);

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        SecureRealtimeMovementIngress ingress;
        while (!transport.TryTakeRealtimeMovement(out ingress))
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(10),
                timeout.Token);
        }
        Check.True(
            transport.IsRealtimeMovementActive &&
            ingress.Kind ==
                SecureRealtimeMovementIngressKind.Input &&
            ingress.TransportSource ==
                SecureRealtimeTransportSource.Tls &&
            ingress.Input == input,
            "TLS reader routes frame 0x0300 into shared ingress");
        await transport.DisposeAsync();
    }
}
