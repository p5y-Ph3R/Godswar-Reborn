using System.Security.Cryptography;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureTlsTransportChecks
{
    private static async Task CheckGameTicketBindRoundTripAsync()
    {
        using var certificate = SecureTlsTestCertificate.Create();
        var options = CreateRuntimeOptions();
        using var gate = new TlsHandshakeGate(1);
        using var ticketStore = new InMemoryGameTicketStore();
        var target = new SecureGameTarget(
            "game.reborn.test",
            "game.reborn.test",
            "reborn-game",
            routePort: 7000,
            tlsPort: 7443,
            serverId: 100);
        var clientInstanceId = Enumerable.Range(
                1,
                SecureProtocolConstants.ClientInstanceIdBytes)
            .Select(static value => checked((byte)value))
            .ToArray();
        var buildHash = Convert.FromHexString(
            SecureNetworkOptions.PredecessorOriginSha256);
        var loginContext = new SecureConnectionContext(
            SecureEndpointRole.Login,
            SecureProtocolConstants.ProtocolMajor,
            SecureProtocolConstants.ProtocolMinor,
            clientInstanceId,
            clientInstanceId,
            buildHash);
        var generation = await ticketStore.BeginLoginAsync(
            7,
            "test2",
            SecureTicketOperationDeadline.Default);
        Check.True(
            generation.IsStarted,
            "test secure login generation starts");
        var issued = await ticketStore.IssueAsync(
            generation.Generation!,
            loginContext,
            target,
            SecureTicketOperationDeadline.Default);
        Check.True(issued.IsIssued, "test secure game ticket issues");
        await using var grantLease = issued.Lease!;

        var factory = new TlsMuxLegacyTransportFactory(
            new SecureNetworkOptions(),
            options,
            certificate.Context,
            gate,
            timeProvider: null,
            ticketStore: ticketStore,
            gameTarget: target);
        await using var pair = await StartPairAsync(
            factory,
            NetworkEndpointRole.Game);
        var serverPreface = await AuthenticateAndPrefaceAsync(
            pair.ClientStream,
            certificate,
            SecureEndpointRole.Game,
            targetHost: "game.reborn.test");
        Check.Equal(
            (int)SecureServerPrefaceStatus.Ok,
            (int)serverPreface.Status,
            "ticket-bound game preface is accepted");

        var grantId =
            new byte[SecureProtocolConstants.GrantIdBytes];
        var ticket =
            new byte[SecureProtocolConstants.TicketBytes];
        try
        {
            Check.True(
                grantLease.Grant.TryCopySecrets(grantId, ticket),
                "test grant secrets remain owned until presentation");
            using var bind = new SecureGameBind(grantId, ticket);
            var bindPayload =
                new byte[SecureProtocolConstants.GameBindBytes];
            try
            {
                Check.True(
                    SecureGameControlCodec.TryEncodeBind(
                        bind,
                        bindPayload,
                        out var written) &&
                    written == bindPayload.Length,
                    "ticket bind payload encodes");
                await WriteFrameAsync(
                    pair.ClientStream,
                    SecureEndpointRole.Game,
                    SecureFrameType.GameBind,
                    sequence: 1,
                    bindPayload);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bindPayload);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(grantId);
            CryptographicOperations.ZeroMemory(ticket);
        }

        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Check.True(
            await grantLease.CommitAsync(
                SecureTicketOperationDeadline.Default),
            "post-redirect activation wins the bounded bind race");
        var resultFrame = await ReadFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expectedSequence: 1);
        Check.True(
            SecureGameControlCodec.TryDecodeBindResult(
                resultFrame.Payload,
                out var result),
            "accepted bind result decodes");
        Check.Equal(
            (int)SecureBindStatus.Accepted,
            (int)result.Status,
            "single-use game ticket is accepted");

        var transport = await pair.TransportTask;
        var secure = (ISecureControlChannel)transport;
        Check.Equal(
            7,
            secure.BoundGamePrincipal!.AccountId,
            "ticket account is attached before the game handler");

        var inbound = RandomNumberGenerator.GetBytes(23);
        await WriteFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameType.LegacyBytes,
            sequence: 2,
            inbound);
        var receivedInbound = await ReadExactlyFromTransportAsync(
            transport,
            inbound.Length);
        Check.True(
            inbound.SequenceEqual(receivedInbound),
            "post-bind client sequence starts at two");

        var outbound = RandomNumberGenerator.GetBytes(19);
        var outboundWrite = transport
            .WriteAsync(outbound, CancellationToken.None)
            .AsTask();
        var outboundFrame = await ReadFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expectedSequence: 2);
        await outboundWrite;
        Check.Equal(
            (int)SecureFrameType.LegacyBytes,
            (int)outboundFrame.Header.Type,
            "post-bind server sequence starts at two");
        Check.True(
            outbound.SequenceEqual(outboundFrame.Payload),
            "post-bind legacy bytes remain unchanged");
    }
}
