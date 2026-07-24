using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Godswar.Server.Networking;

namespace Godswar.Server.ProtocolChecks;

internal static class TcpEndpointServerLifecycleChecks
{
    public static async Task RunAsync()
    {
        await CheckAdmissionAndBoundedShutdownAsync();
    }

    private static async Task CheckAdmissionAndBoundedShutdownAsync()
    {
        var options = CreateSingleConnectionOptions();
        var admission = new ConnectionAdmission(
            new ConnectionAdmissionOptions(
                options.MaxActiveConnections,
                options.MaxUnauthenticatedConnections,
                options.MaxUnauthenticatedConnectionsPerIp,
                options.MaxUnauthenticatedConnectionsPerPrefix));
        var handler = new ReleaseControlledHandler();
        var server = new TcpEndpointServer(
            NetworkEndpointRole.Game,
            IPAddress.Loopback.ToString(),
            0,
            options,
            admission,
            _ => handler);
        using var serverCancellation = new CancellationTokenSource();
        var serverTask = server.RunAsync(serverCancellation.Token);
        var endpoint = await server.WaitUntilStartedAsync()
            .WaitAsync(TimeSpan.FromSeconds(5));

        using var acceptedClient = new TcpClient();
        await acceptedClient.ConnectAsync(
            endpoint.Address,
            endpoint.Port,
            CancellationToken.None);
        await handler.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        Check.Equal(
            1,
            server.ActiveConnectionCount,
            "accepted connection is represented by one tracked task");

        using var rejectedClient = new TcpClient();
        await rejectedClient.ConnectAsync(
            endpoint.Address,
            endpoint.Port,
            CancellationToken.None);
        Check.True(
            await WaitForPeerCloseAsync(rejectedClient),
            "connection above the admission limit is closed before a handler starts");
        Check.Equal(
            1,
            handler.EntryCount,
            "rejected connection never creates a session handler");
        Check.Equal(
            1,
            admission.GetSnapshot().ActiveConnections,
            "rejected connection does not consume admission capacity");

        var stopwatch = Stopwatch.StartNew();
        serverCancellation.Cancel();
        await serverTask.WaitAsync(TimeSpan.FromSeconds(5));
        stopwatch.Stop();

        Check.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            "endpoint shutdown is bounded when a handler ignores cancellation");
        Check.Equal(
            1,
            server.ActiveConnectionCount,
            "timed-out drain keeps the unfinished task tracked");

        handler.Release();
        await WaitUntilAsync(
            () => server.ActiveConnectionCount == 0,
            TimeSpan.FromSeconds(5),
            "released connection task leaves the registry");
        Check.Equal(
            0,
            admission.GetSnapshot().ActiveConnections,
            "eventual handler completion releases connection admission");
    }

    private static NetworkRuntimeOptions CreateSingleConnectionOptions()
    {
        return new NetworkRuntimeOptions
        {
            MaxActiveConnections = 1,
            MaxConcurrentTlsHandshakes = 1,
            MaxUnauthenticatedConnections = 1,
            MaxUnauthenticatedConnectionsPerIp = 1,
            MaxUnauthenticatedConnectionsPerPrefix = 1,
            GracefulDrainTimeoutMilliseconds = 75
        };
    }

    private static async Task<bool> WaitForPeerCloseAsync(TcpClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            return await client.GetStream().ReadAsync(
                new byte[1],
                timeout.Token) == 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string description)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed >= timeout)
            {
                throw new InvalidOperationException(
                    $"Assertion failed: timed out waiting for {description}.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class ReleaseControlledHandler : IClientHandler
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entryCount;

        public Task Entered => _entered.Task;

        public int EntryCount => Volatile.Read(ref _entryCount);

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _entryCount);
            _entered.TrySetResult();
            await _release.Task;
        }

        public void Release()
        {
            _release.TrySetResult();
        }
    }
}
