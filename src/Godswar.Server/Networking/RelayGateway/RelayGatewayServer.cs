using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Godswar.Server.Networking.RelayGateway;

/// <summary>
/// Two-listener, protocol-opaque TCP relay. Admission is shared globally by
/// login and game traffic; each connection owns exactly two fixed-buffer
/// pumps and no application queue.
/// </summary>
internal sealed class RelayGatewayServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<
        long,
        RelayGatewayTrackedConnection> _connections = [];
    private readonly RelayGatewayConfiguration _configuration;
    private readonly CancellationTokenSource _connectionStop = new();
    private readonly CancellationTokenSource _disposeStop = new();
    private readonly RelayGatewayMetrics _metrics;
    private readonly SemaphoreSlim _slots;
    private readonly TaskCompletionSource<RelayGatewayStartedEndpoints>
        _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TcpListener? _gameListener;
    private TcpListener? _loginListener;
    private long _nextConnectionId;
    private int _runStarted;

    public RelayGatewayServer(RelayGatewayConfiguration configuration)
    {
        _configuration = configuration ??
            throw new ArgumentNullException(nameof(configuration));
        _slots = new SemaphoreSlim(
            configuration.Limits.MaximumConnections,
            configuration.Limits.MaximumConnections);
        _metrics = new RelayGatewayMetrics(
            configuration.Limits.MaximumConnections);
    }

    public RelayGatewaySnapshot GetSnapshot() => _metrics.GetSnapshot();

    public Task<RelayGatewayStartedEndpoints> WaitUntilStartedAsync(
        CancellationToken cancellationToken = default) =>
        _started.Task.WaitAsync(cancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException(
                "A relay gateway can run only once.");
        }

        using var acceptStop =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeStop.Token);
        var faulted = false;
        try
        {
            _loginListener = StartListener(
                _configuration.Login,
                _configuration.Limits.ListenBacklog);
            _metrics.MarkListenerReady(RelayGatewayEndpointRole.Login);
            _gameListener = StartListener(
                _configuration.Game,
                _configuration.Limits.ListenBacklog);
            _metrics.MarkListenerReady(RelayGatewayEndpointRole.Game);
            _metrics.MarkReady();
            _started.TrySetResult(new RelayGatewayStartedEndpoints(
                (IPEndPoint)_loginListener.LocalEndpoint,
                (IPEndPoint)_gameListener.LocalEndpoint));

            var loginLoop = AcceptLoopAsync(
                _loginListener,
                _configuration.Login,
                acceptStop.Token);
            var gameLoop = AcceptLoopAsync(
                _gameListener,
                _configuration.Game,
                acceptStop.Token);
            await RunAcceptLoopsAsync(
                loginLoop,
                gameLoop,
                acceptStop);
        }
        catch (OperationCanceledException)
            when (acceptStop.IsCancellationRequested)
        {
            _started.TrySetCanceled(acceptStop.Token);
        }
        catch (Exception ex)
        {
            faulted = true;
            _metrics.MarkFaulted();
            _started.TrySetException(ex);
            throw;
        }
        finally
        {
            if (!faulted)
            {
                _metrics.BeginDrain();
            }

            acceptStop.Cancel();
            StopListener(_loginListener);
            StopListener(_gameListener);
            await DrainConnectionsAsync();
            if (!faulted)
            {
                _metrics.MarkStopped();
            }
            _stopped.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _disposeStop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        if (Volatile.Read(ref _runStarted) != 0)
        {
            try
            {
                await _stopped.Task.WaitAsync(
                    _configuration.Limits.DrainTimeout +
                    _configuration.Limits.ConnectTimeout +
                    _configuration.Limits.WriteTimeout);
            }
            catch (TimeoutException)
            {
                _connectionStop.Cancel();
                DisconnectAll();
            }
        }
        else
        {
            _metrics.MarkStopped();
            _started.TrySetException(
                new ObjectDisposedException(nameof(RelayGatewayServer)));
            _stopped.TrySetResult();
        }

        if (_connections.IsEmpty)
        {
            _connectionStop.Dispose();
            _disposeStop.Dispose();
            _slots.Dispose();
            _metrics.Dispose();
        }
    }

    private static TcpListener StartListener(
        RelayGatewayEndpointConfiguration endpoint,
        int backlog)
    {
        var listener = new TcpListener(endpoint.Bind);
        listener.Start(backlog);
        return listener;
    }

    private async Task AcceptLoopAsync(
        TcpListener listener,
        RelayGatewayEndpointConfiguration endpoint,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(
                cancellationToken);
            if (!_slots.Wait(0))
            {
                _metrics.RecordRejected(endpoint.Role);
                client.Dispose();
                continue;
            }

            TrackConnection(client, endpoint);
        }
    }

    private async Task RunAcceptLoopsAsync(
        Task loginLoop,
        Task gameLoop,
        CancellationTokenSource acceptStop)
    {
        var first = await Task.WhenAny(loginLoop, gameLoop);
        var shutdownWasRequested = acceptStop.IsCancellationRequested;
        if (!shutdownWasRequested)
        {
            acceptStop.Cancel();
            StopListener(_loginListener);
            StopListener(_gameListener);
        }

        try
        {
            await Task.WhenAll(loginLoop, gameLoop);
        }
        catch (OperationCanceledException)
            when (!shutdownWasRequested &&
                first.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException(
                "A relay listener stopped before gateway shutdown.");
        }

        if (!shutdownWasRequested)
        {
            throw new InvalidOperationException(
                "A relay listener stopped before gateway shutdown.");
        }
    }

    private void TrackConnection(
        TcpClient client,
        RelayGatewayEndpointConfiguration endpoint)
    {
        var connectionId = Interlocked.Increment(
            ref _nextConnectionId);
        var connection = new RelayGatewayTrackedConnection(client);
        if (!_connections.TryAdd(connectionId, connection))
        {
            connection.Dispose();
            _slots.Release();
            throw new InvalidOperationException(
                "A unique relay connection ID could not be registered.");
        }

        _metrics.RecordAccepted(endpoint.Role);
        connection.Task = RunTrackedConnectionAsync(
            connectionId,
            connection,
            endpoint);
    }

    private async Task RunTrackedConnectionAsync(
        long connectionId,
        RelayGatewayTrackedConnection connection,
        RelayGatewayEndpointConfiguration endpoint)
    {
        await Task.Yield();
        var outcome = RelayGatewayConnectionOutcome.TransportError;
        try
        {
            outcome = await RelayGatewayConnection.RunAsync(
                connection,
                endpoint,
                _configuration.Limits,
                _metrics,
                _connectionStop.Token);
        }
        catch (OperationCanceledException)
            when (_connectionStop.IsCancellationRequested)
        {
            outcome = RelayGatewayConnectionOutcome.ServerShutdown;
        }
        catch (Exception)
        {
            outcome = RelayGatewayConnectionOutcome.TransportError;
        }
        finally
        {
            _connections.TryRemove(connectionId, out _);
            connection.Dispose();
            _metrics.RecordCompleted(endpoint.Role, outcome);
            _slots.Release();
        }
    }

    private async Task DrainConnectionsAsync()
    {
        var tasks = _connections.Values
            .Select(static connection => connection.Task)
            .Where(static task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (tasks.Length == 0)
        {
            return;
        }

        var all = Task.WhenAll(tasks);
        try
        {
            await all.WaitAsync(
                _configuration.Limits.DrainTimeout);
            return;
        }
        catch (TimeoutException)
        {
            _connectionStop.Cancel();
            DisconnectAll();
        }
        catch
        {
            _connectionStop.Cancel();
            DisconnectAll();
        }

        try
        {
            await all.WaitAsync(
                _configuration.Limits.ConnectTimeout +
                _configuration.Limits.WriteTimeout);
        }
        catch
        {
            // Every connection remains tracked. Socket disposal and the
            // connection cancellation token prevent additional network I/O.
        }
    }

    private void DisconnectAll()
    {
        foreach (var connection in _connections.Values)
        {
            connection.Disconnect();
        }
    }

    private static void StopListener(TcpListener? listener)
    {
        try
        {
            listener?.Stop();
        }
        catch
        {
            // A listener may already be stopped by a startup failure.
        }
    }
}
