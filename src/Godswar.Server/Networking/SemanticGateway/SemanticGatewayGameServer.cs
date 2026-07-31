using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Godswar.Server.Networking.SemanticGateway;

/// <summary>
/// Bounded local legacy game listener. It admits a single-use semantic
/// generation, opens one authenticated worker backhaul, and then relays the
/// original encrypted byte stream without owning gameplay state.
/// </summary>
internal sealed class SemanticGatewayGameServer : IAsyncDisposable
{
    private readonly IConnectionAdmission _admission;
    private readonly IPEndPoint _bind;
    private readonly ConcurrentDictionary<long, TrackedConnection>
        _connections = [];
    private readonly SemanticGatewayGameConnectionDependencies _dependencies;
    private readonly CancellationTokenSource _disposeStop = new();
    private readonly SemanticGatewayGameMetrics _metrics;
    private readonly NetworkRuntimeOptions _network;
    private readonly TaskCompletionSource<IPEndPoint> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _nextConnectionId;
    private int _runStarted;

    public SemanticGatewayGameServer(
        IPEndPoint bind,
        NetworkRuntimeOptions network,
        IConnectionAdmission admission,
        SemanticGatewayGameConnectionDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(bind);
        if (bind.Port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(bind));
        }
        _bind = new IPEndPoint(bind.Address, bind.Port);
        _network = network ??
            throw new ArgumentNullException(nameof(network));
        _network.Validate();
        _admission = admission ??
            throw new ArgumentNullException(nameof(admission));
        _dependencies = dependencies ??
            throw new ArgumentNullException(nameof(dependencies));
        _metrics = new SemanticGatewayGameMetrics(
            network.MaxActiveConnections);
    }

    public SemanticGatewayGameSnapshot GetSnapshot() =>
        _metrics.GetSnapshot();

    public Task<IPEndPoint> WaitUntilStartedAsync(
        CancellationToken cancellationToken = default) =>
        _started.Task.WaitAsync(cancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _runStarted, 1) != 0)
        {
            throw new InvalidOperationException(
                "A semantic gateway game server can run only once.");
        }

        using var lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeStop.Token);
        var listener = new TcpListener(_bind);
        try
        {
            listener.Start(_network.ListenBacklog);
            _started.TrySetResult(
                (IPEndPoint)listener.LocalEndpoint);
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(
                    lifetime.Token);
                Accept(client, lifetime.Token);
            }
        }
        catch (OperationCanceledException)
            when (lifetime.IsCancellationRequested)
        {
            _started.TrySetCanceled(lifetime.Token);
        }
        catch (Exception ex)
        {
            _started.TrySetException(ex);
            throw;
        }
        finally
        {
            listener.Stop();
            DisconnectAll();
            await DrainAsync();
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
                    _network.GracefulDrainTimeout +
                    _dependencies.BackhaulLimits.ConnectTimeout +
                    _dependencies.BackhaulLimits.TlsHandshakeTimeout);
            }
            catch (TimeoutException)
            {
                DisconnectAll();
            }
        }
        else
        {
            _started.TrySetException(
                new ObjectDisposedException(
                    nameof(SemanticGatewayGameServer)));
            _stopped.TrySetResult();
        }

        _disposeStop.Dispose();
    }

    private void Accept(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        var address =
            (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        if (!_admission.TryAcquire(
                NetworkEndpointRole.Game,
                address,
                out var lease,
                out _))
        {
            _metrics.RecordRejected();
            client.Dispose();
            return;
        }

        try
        {
            client.NoDelay = true;
            client.ReceiveBufferSize = _dependencies.BufferSizeBytes;
            client.SendBufferSize = _dependencies.BufferSizeBytes;
            var id = Interlocked.Increment(ref _nextConnectionId);
            var tracked = new TrackedConnection(client, lease);
            if (!_connections.TryAdd(id, tracked))
            {
                throw new InvalidOperationException(
                    "A gateway connection tracking ID was reused.");
            }

            _metrics.RecordAccepted();
            tracked.Task = RunTrackedAsync(
                id,
                tracked,
                cancellationToken);
        }
        catch
        {
            lease.Dispose();
            client.Dispose();
            throw;
        }
    }

    private async Task RunTrackedAsync(
        long id,
        TrackedConnection tracked,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        var outcome = SemanticGatewayGameOutcome.TransportError;
        try
        {
            outcome = await SemanticGatewayGameConnection.RunAsync(
                tracked.Client,
                tracked.AdmissionLease,
                _dependencies,
                _metrics,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            outcome = SemanticGatewayGameOutcome.ServerShutdown;
        }
        catch
        {
            outcome = SemanticGatewayGameOutcome.TransportError;
        }
        finally
        {
            _connections.TryRemove(id, out _);
            tracked.Dispose();
            _metrics.RecordCompleted(outcome);
        }
    }

    private async Task DrainAsync()
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

        try
        {
            await Task.WhenAll(tasks).WaitAsync(
                _network.GracefulDrainTimeout);
        }
        catch
        {
            DisconnectAll();
        }
    }

    private void DisconnectAll()
    {
        foreach (var connection in _connections.Values)
        {
            connection.Disconnect();
        }
    }

    private sealed class TrackedConnection(
        TcpClient client,
        ConnectionAdmissionLease admissionLease) :
        IDisposable
    {
        public TcpClient Client { get; } = client;

        public ConnectionAdmissionLease AdmissionLease { get; } =
            admissionLease;

        public Task? Task { get; set; }

        public void Disconnect()
        {
            try
            {
                Client.Dispose();
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            Disconnect();
            AdmissionLease.Dispose();
        }
    }
}
