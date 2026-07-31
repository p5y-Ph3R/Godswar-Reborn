using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;

namespace Godswar.Server.Networking.RelayGateway;

internal sealed class RelayGatewayTrackedConnection : IDisposable
{
    private readonly object _sync = new();
    private TcpClient? _worker;
    private int _disposed;

    public RelayGatewayTrackedConnection(TcpClient client)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public TcpClient Client { get; }

    public Task? Task { get; set; }

    public bool TrySetWorker(TcpClient worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return false;
            }

            _worker = worker;
            return true;
        }
    }

    public void Disconnect()
    {
        lock (_sync)
        {
            DisposeClient(Client);
            DisposeClient(_worker);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            DisposeClient(Client);
            DisposeClient(_worker);
            _worker = null;
        }
    }

    private static void DisposeClient(TcpClient? client)
    {
        try
        {
            client?.Dispose();
        }
        catch
        {
            // Socket teardown is best effort and must not block shutdown.
        }
    }
}

internal static class RelayGatewayConnection
{
    public static async Task<RelayGatewayConnectionOutcome> RunAsync(
        RelayGatewayTrackedConnection connection,
        RelayGatewayEndpointConfiguration endpoint,
        RelayGatewayRuntimeLimits limits,
        RelayGatewayMetrics metrics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(metrics);

        using var worker = new TcpClient(
            endpoint.Upstream.AddressFamily);
        ConfigureSocket(connection.Client, limits.BufferSizeBytes);
        ConfigureSocket(worker, limits.BufferSizeBytes);
        if (!connection.TrySetWorker(worker))
        {
            return RelayGatewayConnectionOutcome.ServerShutdown;
        }

        var connectOutcome = await ConnectWorkerAsync(
            worker,
            endpoint,
            limits.ConnectTimeout,
            metrics,
            cancellationToken);
        if (connectOutcome is { } failed)
        {
            return failed;
        }

        metrics.RecordWorkerAvailable(endpoint.Role);
        using var relayStop =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        var activity = new RelayConnectionActivity();
        using var clientStream = connection.Client.GetStream();
        using var workerStream = worker.GetStream();
        var clientToWorker = PumpAsync(
            clientStream,
            workerStream,
            worker.Client,
            endpoint.Role,
            clientToWorker: true,
            limits,
            activity,
            metrics,
            relayStop.Token);
        var workerToClient = PumpAsync(
            workerStream,
            clientStream,
            connection.Client.Client,
            endpoint.Role,
            clientToWorker: false,
            limits,
            activity,
            metrics,
            relayStop.Token);
        var idle = WaitForIdleAsync(
            activity,
            limits.IdleTimeout,
            relayStop.Token);

        return await ObservePumpsAsync(
            connection,
            clientToWorker,
            workerToClient,
            idle,
            relayStop,
            cancellationToken);
    }

    private static async Task<RelayGatewayConnectionOutcome?>
        ConnectWorkerAsync(
            TcpClient worker,
            RelayGatewayEndpointConfiguration endpoint,
            TimeSpan timeout,
            RelayGatewayMetrics metrics,
            CancellationToken cancellationToken)
    {
        using var deadline =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            await worker.ConnectAsync(
                endpoint.Upstream.Address,
                endpoint.Upstream.Port,
                deadline.Token);
            return null;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            return RelayGatewayConnectionOutcome.ServerShutdown;
        }
        catch (Exception ex)
            when (ex is OperationCanceledException or
                SocketException or
                IOException)
        {
            metrics.RecordWorkerUnavailable(endpoint.Role);
            return RelayGatewayConnectionOutcome.WorkerUnavailable;
        }
    }

    private static async Task PumpAsync(
        NetworkStream source,
        NetworkStream destination,
        Socket destinationSocket,
        RelayGatewayEndpointRole role,
        bool clientToWorker,
        RelayGatewayRuntimeLimits limits,
        RelayConnectionActivity activity,
        RelayGatewayMetrics metrics,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(
            limits.BufferSizeBytes);
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(
                    buffer.AsMemory(0, limits.BufferSizeBytes),
                    cancellationToken);
                if (count == 0)
                {
                    TryHalfClose(destinationSocket);
                    return;
                }

                activity.Touch();
                using var writeDeadline =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                writeDeadline.CancelAfter(limits.WriteTimeout);
                try
                {
                    await destination.WriteAsync(
                        buffer.AsMemory(0, count),
                        writeDeadline.Token);
                }
                catch (OperationCanceledException)
                    when (!cancellationToken.IsCancellationRequested)
                {
                    throw new RelayGatewayWriteTimeoutException();
                }

                activity.Touch();
                metrics.RecordBytes(role, clientToWorker, count);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async Task WaitForIdleAsync(
        RelayConnectionActivity activity,
        TimeSpan idleTimeout,
        CancellationToken cancellationToken)
    {
        var maximumPoll = TimeSpan.FromSeconds(1);
        while (true)
        {
            var elapsed = activity.GetElapsed();
            var remaining = idleTimeout - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(
                remaining < maximumPoll ? remaining : maximumPoll,
                cancellationToken);
        }
    }

    private static async Task<RelayGatewayConnectionOutcome>
        ObservePumpsAsync(
            RelayGatewayTrackedConnection connection,
            Task clientToWorker,
            Task workerToClient,
            Task idle,
            CancellationTokenSource relayStop,
            CancellationToken serverCancellation)
    {
        var first = await Task.WhenAny(
            clientToWorker,
            workerToClient,
            idle);
        if (ReferenceEquals(first, idle))
        {
            await StopAndSettleAsync(
                connection,
                relayStop,
                clientToWorker,
                workerToClient,
                idle);
            return serverCancellation.IsCancellationRequested
                ? RelayGatewayConnectionOutcome.ServerShutdown
                : RelayGatewayConnectionOutcome.IdleTimeout;
        }
        if (first.IsFaulted || first.IsCanceled)
        {
            var outcome = OutcomeOf(first, serverCancellation);
            await StopAndSettleAsync(
                connection,
                relayStop,
                clientToWorker,
                workerToClient,
                idle);
            return outcome;
        }

        var secondPump = ReferenceEquals(first, clientToWorker)
            ? workerToClient
            : clientToWorker;
        var second = await Task.WhenAny(secondPump, idle);
        if (ReferenceEquals(second, idle))
        {
            await StopAndSettleAsync(
                connection,
                relayStop,
                clientToWorker,
                workerToClient,
                idle);
            return serverCancellation.IsCancellationRequested
                ? RelayGatewayConnectionOutcome.ServerShutdown
                : RelayGatewayConnectionOutcome.IdleTimeout;
        }

        var finalOutcome = second.IsCompletedSuccessfully
            ? RelayGatewayConnectionOutcome.Completed
            : OutcomeOf(second, serverCancellation);
        relayStop.Cancel();
        await IgnoreCompletionAsync(idle);
        return finalOutcome;
    }

    private static async Task StopAndSettleAsync(
        RelayGatewayTrackedConnection connection,
        CancellationTokenSource relayStop,
        params Task[] tasks)
    {
        relayStop.Cancel();
        connection.Disconnect();
        foreach (var task in tasks)
        {
            await IgnoreCompletionAsync(task);
        }
    }

    private static async Task IgnoreCompletionAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // The selected finite outcome is returned to the owner.
        }
    }

    private static RelayGatewayConnectionOutcome OutcomeOf(
        Task task,
        CancellationToken serverCancellation)
    {
        if (serverCancellation.IsCancellationRequested ||
            task.IsCanceled)
        {
            return RelayGatewayConnectionOutcome.ServerShutdown;
        }

        var exception = task.Exception?.Flatten().InnerExceptions
            .FirstOrDefault();
        return exception is RelayGatewayWriteTimeoutException
            ? RelayGatewayConnectionOutcome.WriteTimeout
            : RelayGatewayConnectionOutcome.TransportError;
    }

    private static void ConfigureSocket(
        TcpClient client,
        int bufferSize)
    {
        client.NoDelay = true;
        client.ReceiveBufferSize = bufferSize;
        client.SendBufferSize = bufferSize;
    }

    private static void TryHalfClose(Socket destination)
    {
        try
        {
            destination.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex)
            when (ex is SocketException or ObjectDisposedException)
        {
        }
    }

    private sealed class RelayConnectionActivity
    {
        private long _lastTimestamp = Stopwatch.GetTimestamp();

        public void Touch() =>
            Interlocked.Exchange(
                ref _lastTimestamp,
                Stopwatch.GetTimestamp());

        public TimeSpan GetElapsed() =>
            Stopwatch.GetElapsedTime(
                Interlocked.Read(ref _lastTimestamp));
    }

    private sealed class RelayGatewayWriteTimeoutException : IOException
    {
    }
}
