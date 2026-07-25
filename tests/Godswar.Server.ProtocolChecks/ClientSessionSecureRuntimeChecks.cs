using Godswar.Server.Networking;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ClientSessionRuntimeChecks
{
    internal static async Task RunSecureAuthenticationIdleTransitionAsync()
    {
        var options = CreateOptions();
        var unauthenticatedTime = new ManualTimeProvider();
        var unauthenticatedTransport =
            new ControlledSecureLegacyByteTransport();
        unauthenticatedTransport.QueueInbound(
            EncryptInOrder(CreatePacket(0x3381, 0x58)));
        await using (var unauthenticatedSession = new ClientSession(
                         unauthenticatedTransport,
                         options,
                         NetworkEndpointRole.Login,
                         unauthenticatedTime))
        {
            Check.True(
                await unauthenticatedSession.ReadPacketAsync(
                    CancellationToken.None) is not null,
                "secure first packet enters pre-authentication idle tracking");
            var idleRead = unauthenticatedSession.ReadPacketAsync(
                CancellationToken.None);
            await unauthenticatedTransport.WaitForReadCallsAsync(4);
            unauthenticatedTime.Advance(options.IdleTimeout);
            var error =
                await ExpectExceptionAsync<NetworkDeadlineException>(
                    idleRead,
                    "secure pre-authentication connection keeps the idle deadline")
                    .WaitAsync(TimeSpan.FromSeconds(5));
            Check.True(
                error.Stage == NetworkTimeoutStage.Idle,
                "secure pre-authentication timeout reports the idle stage");
        }

        var authenticatedTime = new ManualTimeProvider();
        var authenticatedTransport =
            new ControlledSecureLegacyByteTransport();
        authenticatedTransport.QueueInbound(
            EncryptInOrder(CreatePacket(0x3382, 0x59)));
        await using var authenticatedSession = new ClientSession(
            authenticatedTransport,
            options,
            NetworkEndpointRole.Login,
            authenticatedTime);
        Check.True(
            await authenticatedSession.ReadPacketAsync(
                CancellationToken.None) is not null,
            "secure authentication transition starts from a complete packet");
        authenticatedSession.MarkAuthenticated();
        Check.True(
            authenticatedTransport.IsAuthenticated,
            "secure authentication transition activates transport heartbeat ownership");

        using var cancellation = new CancellationTokenSource();
        var heartbeatOwnedRead = authenticatedSession.ReadPacketAsync(
            cancellation.Token);
        await authenticatedTransport.WaitForReadCallsAsync(4);
        authenticatedTime.Advance(options.IdleTimeout);
        await Task.Yield();
        Check.True(
            !heartbeatOwnedRead.IsCompleted,
            "authenticated secure transport owns liveness instead of the legacy idle timer");
        cancellation.Cancel();
        await ExpectExceptionAsync<OperationCanceledException>(
            heartbeatOwnedRead,
            "test cancellation releases authenticated secure read");
    }
}
