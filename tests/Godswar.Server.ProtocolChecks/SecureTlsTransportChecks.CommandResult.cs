using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureTlsTransportChecks
{
    private static async Task CheckLegacyCommandResultEgressAsync()
    {
        await CheckRawTransportRejectsCommandResultAsync();
        await CheckLoginTransportRejectsCommandResultAsync();

        await using var fixture = await StartBoundGamePairAsync();
        await using var session = new ClientSession(fixture.Transport);
        var result = new SecureLegacyCommandResult(
            SecureLegacyCommandDisposition.Applied,
            commandFamily: 7,
            resultCode: 1017,
            authoritativeRevision: 42,
            Guid.Parse("10213243-5465-7687-98a9-bacbdcedfe0f"));

        await ExpectExceptionAsync<InvalidOperationException>(
            async () => await session.SendLegacyCommandResultAsync(
                result,
                CancellationToken.None),
            "command result rejects before game authentication");

        session.MarkAuthenticated();
        var stockResponse = MakeLegacyPacket(0x5511, 0xAA, 0xBB);
        var stockWrite = session.SendAsync(
            stockResponse,
            CancellationToken.None,
            "CommandResultOrderingFixture");
        var stockFrame = await ReadFrameAsync(
            fixture.Pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expectedSequence: 2);
        await stockWrite;
        Check.True(
            stockFrame.Header.Type == SecureFrameType.LegacyBytes,
            "preceding stock response remains a legacy frame");

        var resultWrite = session.SendLegacyCommandResultAsync(
            result,
            CancellationToken.None).AsTask();
        var resultFrame = await ReadFrameAsync(
            fixture.Pair.ClientStream,
            SecureEndpointRole.Game,
            SecureFrameDirection.ServerToClient,
            expectedSequence: 3);
        await resultWrite;
        Check.True(
            resultFrame.Header.Type ==
                SecureFrameType.LegacyCommandResult,
            "command result uses its dedicated outer frame");
        Check.True(
            SecureLegacyCommandResultCodec.TryDecode(
                resultFrame.Payload,
                out var decoded) &&
            decoded == result,
            "TLS command result payload round trips");
    }

    private static async Task
        CheckLoginTransportRejectsCommandResultAsync()
    {
        using var certificate = SecureTlsTestCertificate.Create();
        var options = CreateRuntimeOptions();
        using var gate = new TlsHandshakeGate(1);
        var factory = CreateFactory(certificate, options, gate);
        await using var pair = await StartPairAsync(
            factory,
            NetworkEndpointRole.Login);
        _ = await AuthenticateAndPrefaceAsync(
            pair.ClientStream,
            certificate,
            SecureEndpointRole.Login);
        await using var session = new ClientSession(
            await pair.TransportTask);
        session.MarkAuthenticated();

        var result = new SecureLegacyCommandResult(
            SecureLegacyCommandDisposition.Rejected,
            commandFamily: 1,
            resultCode: 1002,
            authoritativeRevision: 0,
            Guid.Parse("30415263-7485-96a7-b8c9-daebfc0d1e2f"));
        await ExpectExceptionAsync<InvalidOperationException>(
            async () => await session.SendLegacyCommandResultAsync(
                result,
                CancellationToken.None),
            "secure login channel rejects game command results");
    }

    private static async Task
        CheckRawTransportRejectsCommandResultAsync()
    {
        await using var session = new ClientSession(
            new ScriptedLegacyByteTransport());
        var result = new SecureLegacyCommandResult(
            SecureLegacyCommandDisposition.Rejected,
            commandFamily: 1,
            resultCode: 1002,
            authoritativeRevision: 0,
            Guid.Parse("20314253-6475-8697-a8b9-cadbecfd0e1f"));

        await ExpectExceptionAsync<InvalidOperationException>(
            async () => await session.SendLegacyCommandResultAsync(
                result,
                CancellationToken.None),
            "raw legacy session explicitly rejects secure command results");
    }
}
