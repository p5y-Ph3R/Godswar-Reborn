using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Godswar.Server.Networking.RelayGateway;

namespace Godswar.Server.ProtocolChecks;

internal static partial class RelayGatewayChecks
{
    private static async Task CheckOpaqueRoundTripsAsync()
    {
        using var loginWorker = StartListener();
        using var gameWorker = StartListener();
        var loginRequest = Enumerable
            .Range(0, 8_197)
            .Select(static value => (byte)(value * 37))
            .ToArray();
        var loginResponse = Enumerable
            .Range(0, 4_101)
            .Select(static value => (byte)(255 - value * 19))
            .ToArray();
        var gameRequest = Enumerable
            .Range(0, 3_333)
            .Select(static value => (byte)(value * 11))
            .ToArray();
        var gameResponse = Enumerable
            .Range(0, 7_777)
            .Select(static value => (byte)(value * 23))
            .ToArray();

        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        var loginWorkerTask = ServeAfterHalfCloseAsync(
            loginWorker,
            loginRequest,
            loginResponse,
            timeout.Token);
        var gameWorkerTask = ServeAfterHalfCloseAsync(
            gameWorker,
            gameRequest,
            gameResponse,
            timeout.Token);
        await using var gateway = new RelayGatewayServer(
            CreateConfiguration(
                ListenerEndpoint(loginWorker),
                ListenerEndpoint(gameWorker)));
        using var gatewayStop = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(gatewayStop.Token);
        var endpoints = await gateway.WaitUntilStartedAsync(timeout.Token);

        var loginRoundTrip = RoundTripAsync(
            endpoints.Login,
            loginRequest,
            loginResponse,
            [1, 17, 257, 3, 1_024],
            timeout.Token);
        var gameRoundTrip = RoundTripAsync(
            endpoints.Game,
            gameRequest,
            gameResponse,
            [gameRequest.Length],
            timeout.Token);
        await Task.WhenAll(
            loginRoundTrip,
            gameRoundTrip,
            loginWorkerTask,
            gameWorkerTask);
        await WaitUntilAsync(
            () => gateway.GetSnapshot().ActiveConnections == 0,
            timeout.Token,
            "opaque relay connections to leave tracking");

        var snapshot = gateway.GetSnapshot();
        Check.Equal(
            (long)loginRequest.Length + gameRequest.Length,
            snapshot.BytesClientToWorker,
            "all opaque client bytes are counted");
        Check.Equal(
            (long)loginResponse.Length + gameResponse.Length,
            snapshot.BytesWorkerToClient,
            "all opaque worker bytes are counted");
        Check.Equal(
            2L,
            snapshot.CompletedConnections,
            "both half-closed streams complete");

        gatewayStop.Cancel();
        await gatewayTask.WaitAsync(timeout.Token);
        Check.True(
            gateway.GetSnapshot().State ==
                RelayGatewayRuntimeState.Stopped,
            "relay stops after a bounded drain");
    }

    private static async Task CheckWorkerRecoveryAsync()
    {
        var loginPort = ReservePort();
        var gamePort = ReservePort();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        await using var gateway = new RelayGatewayServer(
            CreateConfiguration(
                new IPEndPoint(IPAddress.Loopback, loginPort),
                new IPEndPoint(IPAddress.Loopback, gamePort)));
        using var gatewayStop = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(gatewayStop.Token);
        var endpoints = await gateway.WaitUntilStartedAsync(timeout.Token);

        using (var unavailable = new TcpClient())
        {
            await unavailable.ConnectAsync(
                endpoints.Login.Address,
                endpoints.Login.Port,
                timeout.Token);
            Check.True(
                await WaitForPeerCloseAsync(unavailable, timeout.Token),
                "unavailable worker closes the public connection");
        }
        await WaitUntilAsync(
            () => gateway.GetSnapshot().WorkerConnectFailures >= 1,
            timeout.Token,
            "worker failure observation");
        Check.True(
            gateway.GetSnapshot().ReadinessReason ==
                RelayGatewayReadinessReason.LoginWorkerUnavailable,
            "passive readiness reports the unavailable login worker");
        Check.True(
            !gatewayTask.IsCompleted,
            "worker failure does not stop the relay process");

        using var recoveredWorker = new TcpListener(
            IPAddress.Loopback,
            loginPort);
        recoveredWorker.Start(4);
        var request = new byte[] { 0, 255, 1, 254, 2, 253 };
        var response = new byte[] { 9, 0, 8, 0, 7, 0 };
        var workerTask = ServeAfterHalfCloseAsync(
            recoveredWorker,
            request,
            response,
            timeout.Token);
        await RoundTripAsync(
            endpoints.Login,
            request,
            response,
            [2, 1, 3],
            timeout.Token);
        await workerTask;

        Check.True(
            gateway.GetSnapshot().LoginWorker ==
                RelayGatewayWorkerAvailability.Available,
            "a later connection observes worker recovery");
        Check.True(
            !gatewayTask.IsCompleted,
            "worker recovery does not require a relay restart");

        gatewayStop.Cancel();
        await gatewayTask.WaitAsync(timeout.Token);
    }

    private static async Task CheckAdmissionAndDrainAsync()
    {
        using var loginWorker = StartListener();
        var gamePort = ReservePort();
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(10));
        await using var gateway = new RelayGatewayServer(
            CreateConfiguration(
                ListenerEndpoint(loginWorker),
                new IPEndPoint(IPAddress.Loopback, gamePort),
                maximumConnections: 1,
                drainMilliseconds: 100));
        using var gatewayStop = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(gatewayStop.Token);
        var endpoints = await gateway.WaitUntilStartedAsync(timeout.Token);

        using var firstClient = new TcpClient();
        await firstClient.ConnectAsync(
            endpoints.Login.Address,
            endpoints.Login.Port,
            timeout.Token);
        using var workerConnection =
            await loginWorker.AcceptTcpClientAsync(timeout.Token);
        await WaitUntilAsync(
            () => gateway.GetSnapshot().ActiveConnections == 1,
            timeout.Token,
            "first relay connection to consume capacity");

        using var rejectedClient = new TcpClient();
        await rejectedClient.ConnectAsync(
            endpoints.Game.Address,
            endpoints.Game.Port,
            timeout.Token);
        Check.True(
            await WaitForPeerCloseAsync(rejectedClient, timeout.Token),
            "connection above the shared cap is closed");
        await WaitUntilAsync(
            () => gateway.GetSnapshot().RejectedConnections == 1,
            timeout.Token,
            "shared admission rejection metric");

        var stopwatch = Stopwatch.StartNew();
        gatewayStop.Cancel();
        await gatewayTask.WaitAsync(timeout.Token);
        stopwatch.Stop();
        Check.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            "active relay connection is force-closed after drain deadline");
        Check.True(
            await WaitForPeerCloseAsync(firstClient, timeout.Token),
            "drain closes the public side of an active connection");
        Check.True(
            gateway.GetSnapshot().State ==
                RelayGatewayRuntimeState.Stopped,
            "drained relay reaches stopped state");
    }

    private static async Task CheckIdleDeadlineAsync()
    {
        using var loginWorker = StartListener();
        var gamePort = ReservePort();
        var configuration = CreateConfiguration(
            ListenerEndpoint(loginWorker),
            new IPEndPoint(IPAddress.Loopback, gamePort));
        configuration = configuration with
        {
            Limits = configuration.Limits with
            {
                IdleTimeout = TimeSpan.FromMilliseconds(100)
            }
        };
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        await using var gateway = new RelayGatewayServer(configuration);
        using var gatewayStop = new CancellationTokenSource();
        var gatewayTask = gateway.RunAsync(gatewayStop.Token);
        var endpoints = await gateway.WaitUntilStartedAsync(timeout.Token);

        using var client = new TcpClient();
        await client.ConnectAsync(
            endpoints.Login.Address,
            endpoints.Login.Port,
            timeout.Token);
        using var workerConnection =
            await loginWorker.AcceptTcpClientAsync(timeout.Token);
        Check.True(
            await WaitForPeerCloseAsync(client, timeout.Token),
            "idle relay connection closes at its deadline");
        await WaitUntilAsync(
            () => gateway.GetSnapshot().TimedOutConnections == 1,
            timeout.Token,
            "idle timeout metric");
        Check.True(
            !gatewayTask.IsCompleted,
            "one idle timeout does not stop the relay");

        gatewayStop.Cancel();
        await gatewayTask.WaitAsync(timeout.Token);
    }

    private static TcpListener StartListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(8);
        return listener;
    }

    private static IPEndPoint ListenerEndpoint(TcpListener listener) =>
        (IPEndPoint)listener.LocalEndpoint;

    private static int ReservePort()
    {
        using var listener = StartListener();
        return ListenerEndpoint(listener).Port;
    }

    private static async Task ServeAfterHalfCloseAsync(
        TcpListener listener,
        byte[] expectedRequest,
        byte[] response,
        CancellationToken cancellationToken)
    {
        using var client =
            await listener.AcceptTcpClientAsync(cancellationToken);
        var stream = client.GetStream();
        var request = await ReadExactlyAsync(
            stream,
            expectedRequest.Length,
            cancellationToken);
        Check.True(
            request.SequenceEqual(expectedRequest),
            "worker receives exact opaque bytes");
        var end = new byte[1];
        Check.Equal(
            0,
            await stream.ReadAsync(end, cancellationToken),
            "client half-close reaches worker");
        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        client.Client.Shutdown(SocketShutdown.Send);
    }

    private static async Task RoundTripAsync(
        IPEndPoint endpoint,
        byte[] request,
        byte[] expectedResponse,
        int[] chunks,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient(endpoint.AddressFamily);
        await client.ConnectAsync(
            endpoint.Address,
            endpoint.Port,
            cancellationToken);
        var stream = client.GetStream();
        var offset = 0;
        var chunkIndex = 0;
        while (offset < request.Length)
        {
            var count = Math.Min(
                chunks[chunkIndex++ % chunks.Length],
                request.Length - offset);
            await stream.WriteAsync(
                request.AsMemory(offset, count),
                cancellationToken);
            offset += count;
        }
        await stream.FlushAsync(cancellationToken);
        client.Client.Shutdown(SocketShutdown.Send);

        var response = await ReadExactlyAsync(
            stream,
            expectedResponse.Length,
            cancellationToken);
        Check.True(
            response.SequenceEqual(expectedResponse),
            "client receives exact opaque response bytes");
        Check.Equal(
            0,
            await stream.ReadAsync(new byte[1], cancellationToken),
            "worker half-close reaches client");
    }

    private static async Task<byte[]> ReadExactlyAsync(
        NetworkStream stream,
        int length,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var count = await stream.ReadAsync(
                bytes.AsMemory(offset),
                cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException(
                    "Relay peer closed before the expected bytes arrived.");
            }
            offset += count;
        }
        return bytes;
    }

    private static async Task<bool> WaitForPeerCloseAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetStream().ReadAsync(
                new byte[1],
                cancellationToken) == 0;
        }
        catch (Exception ex)
            when (ex is IOException or SocketException)
        {
            return true;
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken,
        string description)
    {
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(10, cancellationToken);
        }
    }
}
