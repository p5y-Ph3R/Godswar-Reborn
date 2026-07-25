using System.Net.Security;
using System.Security.Cryptography;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureTlsTransportChecks
{
    private static async Task CheckUnsupportedBuildFailsClosedAsync()
    {
        using var certificate = SecureTlsTestCertificate.Create();
        var options = CreateRuntimeOptions();
        using var gate = new TlsHandshakeGate(1);
        var factory = CreateFactory(certificate, options, gate);
        await using var pair = await StartPairAsync(
            factory,
            NetworkEndpointRole.Login);

        await pair.ClientStream.AuthenticateAsClientAsync(
            certificate.CreateClientOptions());
        var unknownBuild = RandomNumberGenerator.GetBytes(
            SecureProtocolConstants.BuildHashBytes);
        await pair.ClientStream.WriteAsync(
            EncodeClientPreface(
                SecureEndpointRole.Login,
                unknownBuild));
        var response = await ReadExactlyAsync(
            pair.ClientStream,
            SecureProtocolConstants.ServerPrefaceBytes);
        Check.True(
            SecurePrefaceCodec.TryDecodeServer(
                response,
                SecureEndpointRole.Login,
                out var preface),
            "unsupported-build response is canonical");
        Check.Equal(
            (int)SecureServerPrefaceStatus.UnsupportedBuild,
            (int)preface!.Status,
            "unknown client build fails closed");
        await ExpectExceptionAsync<SecureTransportException>(
            async () => await pair.TransportTask,
            "unsupported build never creates a legacy transport");
    }

    private static async Task CheckWrongAlpnFailsClosedAsync()
    {
        using var certificate = SecureTlsTestCertificate.Create();
        var options = CreateRuntimeOptions();
        using var gate = new TlsHandshakeGate(1);
        var factory = CreateFactory(certificate, options, gate);
        await using var pair = await StartPairAsync(
            factory,
            NetworkEndpointRole.Login);

        var clientFailure = CaptureFailureAsync(
            () => pair.ClientStream.AuthenticateAsClientAsync(
                certificate.CreateClientOptions(
                    applicationProtocol: "wrong/1")));
        var serverFailure = CaptureFailureAsync(
            async () => await pair.TransportTask);
        var failures = await Task.WhenAll(clientFailure, serverFailure)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(
            failures.Any(static error => error is not null),
            "wrong ALPN closes the secure connection without downgrade");
        Check.True(
            pair.TransportTask.IsFaulted,
            "wrong ALPN cannot create a legacy transport");
    }

    private static async Task CheckHandshakeDeadlineAsync()
    {
        using var certificate = SecureTlsTestCertificate.Create();
        var options = CreateRuntimeOptions();
        options.TlsHandshakeTimeoutMilliseconds = 100;
        using var gate = new TlsHandshakeGate(1);
        var factory = CreateFactory(certificate, options, gate);
        await using var pair = await StartPairAsync(
            factory,
            NetworkEndpointRole.Login);

        var error = await ExpectExceptionAsync<NetworkDeadlineException>(
            async () => await pair.TransportTask,
            "silent TLS peer observes accepted-to-handshake deadline");
        Check.Equal(
            (int)NetworkTimeoutStage.TlsHandshake,
            (int)error.Stage,
            "TLS handshake timeout reports its finite stage");
    }

    private static async Task CheckGameFailsClosedBeforeHandlerAsync()
    {
        using var certificate = SecureTlsTestCertificate.Create();
        var options = CreateRuntimeOptions();
        using var gate = new TlsHandshakeGate(1);
        var factory = CreateFactory(certificate, options, gate);
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
            "secure game TLS and preface can be tested in slice 6");

        using var bind = new SecureGameBind(
            RandomNumberGenerator.GetBytes(
                SecureProtocolConstants.GrantIdBytes),
            RandomNumberGenerator.GetBytes(
                SecureProtocolConstants.TicketBytes));
        var payload = new byte[SecureProtocolConstants.GameBindBytes];
        Check.True(
            SecureGameControlCodec.TryEncodeBind(
                bind,
                payload,
                out _),
            "test game bind encodes");
        await WriteFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameType.GameBind,
            sequence: 1,
            payload);

        var resultFrame = await ReadFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expectedSequence: 1);
        Check.Equal(
            (int)SecureFrameType.BindResult,
            (int)resultFrame.Header.Type,
            "game pre-handler gate returns only a bind result");
        Check.True(
            SecureGameControlCodec.TryDecodeBindResult(
                resultFrame.Payload,
                out var result),
            "game bind rejection is canonical");
        Check.Equal(
            (int)SecureBindStatus.PolicyRejected,
            (int)result.Status,
            "game remains fail-closed until slice 7 ticket authority");
        await ExpectExceptionAsync<SecureTransportException>(
            async () => await pair.TransportTask,
            "game rejection never creates a legacy handler transport");
    }

    private static async Task<Exception?> CaptureFailureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }
}
