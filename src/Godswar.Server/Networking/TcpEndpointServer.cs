using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.Networking;

internal sealed class TcpEndpointServer
{
    private readonly ConcurrentDictionary<long, ActiveConnection> _connections = [];
    private readonly NetworkEndpointRole _endpointRole;
    private readonly Func<ClientSession, IClientHandler> _handlerFactory;
    private readonly string _host;
    private readonly IConnectionAdmission _admission;
    private readonly NetworkRuntimeOptions _runtimeOptions;
    private readonly ILegacyByteTransportFactory _transportFactory;
    private readonly TaskCompletionSource<IPEndPoint> _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider _timeProvider;
    private long _nextConnectionId;

    public TcpEndpointServer(
        NetworkEndpointRole endpointRole,
        string host,
        int port,
        NetworkRuntimeOptions runtimeOptions,
        IConnectionAdmission admission,
        Func<ClientSession, IClientHandler> handlerFactory,
        TimeProvider? timeProvider = null,
        ILegacyByteTransportFactory? transportFactory = null)
    {
        _endpointRole = endpointRole;
        _host = host;
        Port = port;
        _runtimeOptions = runtimeOptions
            ?? throw new ArgumentNullException(nameof(runtimeOptions));
        _admission = admission
            ?? throw new ArgumentNullException(nameof(admission));
        _handlerFactory = handlerFactory
            ?? throw new ArgumentNullException(nameof(handlerFactory));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _transportFactory =
            transportFactory ?? RawTcpLegacyTransportFactory.Instance;
        _runtimeOptions.Validate();
    }

    public int Port { get; }

    internal int ActiveConnectionCount => _connections.Count;

    internal Task<IPEndPoint> WaitUntilStartedAsync(
        CancellationToken cancellationToken = default)
    {
        return _started.Task.WaitAsync(cancellationToken);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var address = ResolveAddress(_host);
        var listener = new TcpListener(address, Port);
        using var endpointLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            listener.Start(_runtimeOptions.ListenBacklog);
            var localEndPoint = (IPEndPoint)listener.LocalEndpoint;
            _started.TrySetResult(localEndPoint);
            Console.WriteLine(
                $"[{_endpointRole.ToMetricTag()}] listening on {localEndPoint}");

            while (!endpointLifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(
                    endpointLifetime.Token);
                AcceptConnection(client, endpointLifetime.Token);
            }
        }
        catch (OperationCanceledException)
            when (endpointLifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _started.TrySetException(ex);
            throw;
        }
        finally
        {
            listener.Stop();
            endpointLifetime.Cancel();
            DisconnectAll();
            await DrainConnectionsAsync();
        }
    }

    private void AcceptConnection(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        var remoteAddress =
            (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
        if (!_admission.TryAcquire(
                _endpointRole,
                remoteAddress,
                out var lease,
                out var rejection))
        {
            NetworkRuntimeMetrics.RecordConnectionRejected(
                _endpointRole,
                rejection);
            client.Dispose();
            return;
        }

        var connectionId = Interlocked.Increment(ref _nextConnectionId);
        var connection = new ActiveConnection(
            client,
            lease,
            _timeProvider.GetTimestamp());
        if (!_connections.TryAdd(connectionId, connection))
        {
            lease.Dispose();
            client.Dispose();
            throw new InvalidOperationException(
                "A unique connection tracking ID could not be registered.");
        }

        NetworkRuntimeMetrics.RecordConnectionAccepted(_endpointRole);
        NetworkRuntimeMetrics.RecordTrackedTaskStarted(_endpointRole);
        connection.Task = RunTrackedConnectionAsync(
            connectionId,
            connection,
            cancellationToken);
    }

    private async Task RunTrackedConnectionAsync(
        long connectionId,
        ActiveConnection connection,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        var disconnectReason = NetworkDisconnectReason.HandlerCompleted;

        try
        {
            var transport = await _transportFactory.CreateAsync(
                connection.Client,
                _endpointRole,
                connection.AcceptedTimestamp,
                cancellationToken);
            await using var session = new ClientSession(
                transport,
                _runtimeOptions,
                _endpointRole,
                _timeProvider,
                connection.Lease.MarkAuthenticated);
            connection.Session = session;
            if (session.BoundGamePrincipal is not null)
            {
                // Ticket consumption is the authentication boundary for the
                // secure game endpoint. Release unauthenticated admission and
                // start the secure heartbeat before legacy compatibility data
                // reaches the game handler.
                session.MarkAuthenticated();
            }
            await _handlerFactory(session).RunAsync(cancellationToken);
            disconnectReason = cancellationToken.IsCancellationRequested
                ? NetworkDisconnectReason.ServerShutdown
                : NetworkDisconnectReason.HandlerCompleted;
        }
        catch (NetworkDeadlineException)
        {
            disconnectReason = NetworkDisconnectReason.Timeout;
        }
        catch (ReliableQueueOverflowException)
        {
            disconnectReason = NetworkDisconnectReason.ReliableQueueOverflow;
        }
        catch (InvalidDataException)
        {
            disconnectReason = NetworkDisconnectReason.ProtocolViolation;
        }
        catch (SecureTransportException)
        {
            disconnectReason = NetworkDisconnectReason.ProtocolViolation;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            disconnectReason = NetworkDisconnectReason.ServerShutdown;
        }
        catch (OperationCanceledException)
        {
            disconnectReason = NetworkDisconnectReason.ApplicationDisconnect;
        }
        catch (IOException)
        {
            disconnectReason = NetworkDisconnectReason.RemoteClosed;
        }
        catch (SocketException)
        {
            disconnectReason = NetworkDisconnectReason.TransportError;
        }
        catch (Exception)
        {
            disconnectReason = NetworkDisconnectReason.HandlerError;
        }
        finally
        {
            _connections.TryRemove(connectionId, out _);
            connection.Lease.Dispose();
            NetworkRuntimeMetrics.RecordTrackedTaskCompleted(_endpointRole);
            NetworkRuntimeMetrics.RecordConnectionClosed(
                _endpointRole,
                disconnectReason);
        }
    }

    private void DisconnectAll()
    {
        foreach (var connection in _connections.Values)
        {
            connection.Disconnect();
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
            NetworkRuntimeMetrics.RecordDrainOutcome(
                _endpointRole,
                NetworkDrainOutcome.Completed);
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(
                _runtimeOptions.GracefulDrainTimeout,
                _timeProvider,
                CancellationToken.None);
            NetworkRuntimeMetrics.RecordDrainOutcome(
                _endpointRole,
                NetworkDrainOutcome.Completed);
        }
        catch (TimeoutException)
        {
            NetworkRuntimeMetrics.RecordTimeout(
                _endpointRole,
                NetworkTimeoutStage.GracefulDrain);
            NetworkRuntimeMetrics.RecordDrainOutcome(
                _endpointRole,
                NetworkDrainOutcome.DeadlineExceeded);
        }
    }

    private static IPAddress ResolveAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host is "*" or "0.0.0.0")
        {
            return IPAddress.Any;
        }

        return IPAddress.TryParse(host, out var address)
            ? address
            : Dns.GetHostAddresses(host).First();
    }

    private sealed class ActiveConnection(
        TcpClient client,
        ConnectionAdmissionLease lease,
        long acceptedTimestamp)
    {
        public TcpClient Client { get; } = client;

        public ConnectionAdmissionLease Lease { get; } = lease;

        public long AcceptedTimestamp { get; } = acceptedTimestamp;

        public ClientSession? Session { get; set; }

        public Task? Task { get; set; }

        public void Disconnect()
        {
            if (Session is { } session)
            {
                session.Disconnect();
                return;
            }

            Client.Dispose();
        }
    }
}
