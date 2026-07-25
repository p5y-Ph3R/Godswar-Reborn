using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureTlsTransportChecks
{
    private static async Task CheckAuthenticatedHeartbeatAsync()
    {
        using var certificate = SecureTlsTestCertificate.Create();
        var timeProvider = new ManualTimeProvider();
        var options = CreateRuntimeOptions();
        using var gate = new TlsHandshakeGate(1);
        var factory = CreateFactory(
            certificate,
            options,
            gate,
            timeProvider);
        await using var pair = await StartPairAsync(
            factory,
            NetworkEndpointRole.Login,
            timeProvider: timeProvider);
        await AuthenticateAndPrefaceAsync(
            pair.ClientStream,
            certificate,
            SecureEndpointRole.Login);
        var transport = (TlsMuxLegacyTransport)await pair.TransportTask;
        Check.Equal(
            options.ControlQueueItems,
            transport.ControlSnapshot.CapacityItems,
            "heartbeat control queue uses configured item capacity");
        Check.Equal(
            (long)options.ControlQueueBytes,
            transport.ControlSnapshot.CapacityBytes,
            "heartbeat control queue uses configured byte capacity");

        await using var session = new ClientSession(
            transport,
            options,
            NetworkEndpointRole.Login,
            timeProvider);
        session.MarkAuthenticated();
        await WaitForScheduledTimerAsync(timeProvider);
        timeProvider.Advance(TimeSpan.FromSeconds(30));

        var ping = await ReadFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Login,
            SecureFrameDirection.ServerToClient,
            expectedSequence: 1);
        Check.Equal(
            (int)SecureFrameType.Ping,
            (int)ping.Header.Type,
            "authenticated send-idle produces the sole-server Ping");
        await WriteFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Login,
            SecureFrameType.Pong,
            sequence: 1,
            ping.Payload);
        await WaitForPongProcessedAsync(transport);

        await WaitForScheduledTimerAsync(timeProvider);
        timeProvider.Advance(TimeSpan.FromSeconds(30));
        var secondPing = await ReadFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Login,
            SecureFrameDirection.ServerToClient,
            expectedSequence: 2);
        Check.Equal(
            (int)SecureFrameType.Ping,
            (int)secondPing.Header.Type,
            "matching Pong clears the outstanding heartbeat");

        secondPing.Payload[0] ^= 0x80;
        await WriteFrameAsync(
            pair.ClientStream,
            SecureEndpointRole.Login,
            SecureFrameType.Pong,
            sequence: 2,
            secondPing.Payload);
        Check.True(
            await WaitForTlsCloseAsync(pair.ClientStream),
            "wrong Pong fails the secure connection");
    }

    private static async Task WaitForScheduledTimerAsync(
        ManualTimeProvider timeProvider)
    {
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        while (timeProvider.ScheduledTimerCount == 0)
        {
            await Task.Delay(1, deadline.Token);
        }
    }

    private static async Task WaitForPongProcessedAsync(
        TlsMuxLegacyTransport transport)
    {
        using var deadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        while (transport.PingOutstanding)
        {
            await Task.Delay(1, deadline.Token);
        }
    }

    private static async Task CheckHeartbeatWriteDeadlineAsync()
    {
        using var certificate = SecureTlsTestCertificate.Create();
        var timeProvider = new ManualTimeProvider();
        var options = CreateRuntimeOptions();
        using var gate = new TlsHandshakeGate(1);
        var factory = CreateFactory(
            certificate,
            options,
            gate,
            timeProvider);
        await using var pair = await StartPairAsync(
            factory,
            NetworkEndpointRole.Login,
            timeProvider: timeProvider);
        await AuthenticateAndPrefaceAsync(
            pair.ClientStream,
            certificate,
            SecureEndpointRole.Login);
        var transport = (TlsMuxLegacyTransport)await pair.TransportTask;
        await using var session = new ClientSession(
            transport,
            options,
            NetworkEndpointRole.Login,
            timeProvider);
        session.MarkAuthenticated();
        await Task.Yield();

        var blockedWrite = transport.WriteAsync(
            new byte[2 * 1024 * 1024],
            CancellationToken.None).AsTask();
        await Task.Delay(25);
        Check.True(
            !blockedWrite.IsCompleted,
            "flow-controlled TLS peer blocks the serialized writer");

        timeProvider.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(25);
        timeProvider.Advance(options.ReliableWriteTimeout);

        var completed = await Task.WhenAny(
            blockedWrite,
            Task.Delay(TimeSpan.FromSeconds(5)));
        Check.True(
            ReferenceEquals(completed, blockedWrite),
            "heartbeat write admission deadline closes a stalled TLS writer");
        try
        {
            await blockedWrite;
            throw new InvalidOperationException(
                "Assertion failed: stalled TLS writer unexpectedly succeeded.");
        }
        catch (InvalidOperationException error)
            when (!error.Message.StartsWith(
                "Assertion failed:",
                StringComparison.Ordinal))
        {
        }
        catch (IOException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }
}
