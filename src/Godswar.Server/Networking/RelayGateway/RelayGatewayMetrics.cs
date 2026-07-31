using System.Diagnostics.Metrics;

namespace Godswar.Server.Networking.RelayGateway;

/// <summary>
/// Finite, low-cardinality relay accounting. The snapshot is the management
/// integration boundary. Meter instruments are available to a future relay
/// host collector; B18C1 does not expose a management endpoint.
/// </summary>
internal sealed class RelayGatewayMetrics : IDisposable
{
    public const string MeterName = "Godswar.Server.RelayGateway";

    private readonly int _capacity;
    private readonly Counter<long> _acceptedInstrument;
    private readonly Counter<long> _bytesInstrument;
    private readonly Counter<long> _outcomeInstrument;
    private readonly Counter<long> _rejectedInstrument;
    private readonly Meter _meter = new(MeterName);
    private long _accepted;
    private int _active;
    private long _bytesClientToWorker;
    private long _bytesWorkerToClient;
    private long _completed;
    private long _faulted;
    private int _gameListenerReady;
    private int _gameWorker;
    private int _loginListenerReady;
    private int _loginWorker;
    private long _rejected;
    private long _shutdown;
    private int _state = (int)RelayGatewayRuntimeState.Starting;
    private long _timedOut;
    private long _workerConnectFailures;

    public RelayGatewayMetrics(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _acceptedInstrument = _meter.CreateCounter<long>(
            "godswar.server.relay.connections.accepted",
            "{connection}",
            "Accepted relay connections by finite endpoint role.");
        _rejectedInstrument = _meter.CreateCounter<long>(
            "godswar.server.relay.connections.rejected",
            "{connection}",
            "Relay connections rejected by global admission.");
        _outcomeInstrument = _meter.CreateCounter<long>(
            "godswar.server.relay.connections.completed",
            "{connection}",
            "Completed relay connections by finite role and outcome.");
        _bytesInstrument = _meter.CreateCounter<long>(
            "godswar.server.relay.bytes",
            "By",
            "Opaque bytes relayed by finite direction.");
        _meter.CreateObservableGauge(
            "godswar.server.relay.connections.active",
            () => Volatile.Read(ref _active),
            "{connection}",
            "Currently tracked relay connections.");
        _meter.CreateObservableGauge(
            "godswar.server.relay.readiness",
            () => GetSnapshot().IsReady ? 1 : 0,
            description: "Whether both relay listeners can admit traffic.");
    }

    public RelayGatewaySnapshot GetSnapshot()
    {
        var state = (RelayGatewayRuntimeState)Volatile.Read(ref _state);
        var loginListener = Volatile.Read(ref _loginListenerReady) != 0;
        var gameListener = Volatile.Read(ref _gameListenerReady) != 0;
        var loginWorker = (RelayGatewayWorkerAvailability)
            Volatile.Read(ref _loginWorker);
        var gameWorker = (RelayGatewayWorkerAvailability)
            Volatile.Read(ref _gameWorker);
        var reason = ReadinessReason(
            state,
            loginListener,
            gameListener,
            loginWorker,
            gameWorker);

        return new RelayGatewaySnapshot(
            state,
            state is RelayGatewayRuntimeState.Starting or
                RelayGatewayRuntimeState.Ready or
                RelayGatewayRuntimeState.Draining,
            reason == RelayGatewayReadinessReason.None,
            reason,
            loginWorker,
            gameWorker,
            Volatile.Read(ref _active),
            _capacity,
            Interlocked.Read(ref _accepted),
            Interlocked.Read(ref _rejected),
            Interlocked.Read(ref _workerConnectFailures),
            Interlocked.Read(ref _completed),
            Interlocked.Read(ref _faulted),
            Interlocked.Read(ref _timedOut),
            Interlocked.Read(ref _shutdown),
            Interlocked.Read(ref _bytesClientToWorker),
            Interlocked.Read(ref _bytesWorkerToClient));
    }

    public void MarkListenerReady(RelayGatewayEndpointRole role)
    {
        ref var target = ref ListenerReadyField(role);
        Volatile.Write(ref target, 1);
    }

    public void MarkReady() =>
        Volatile.Write(
            ref _state,
            (int)RelayGatewayRuntimeState.Ready);

    public void BeginDrain() =>
        Volatile.Write(
            ref _state,
            (int)RelayGatewayRuntimeState.Draining);

    public void MarkFaulted() =>
        Volatile.Write(
            ref _state,
            (int)RelayGatewayRuntimeState.Faulted);

    public void MarkStopped()
    {
        Volatile.Write(ref _loginListenerReady, 0);
        Volatile.Write(ref _gameListenerReady, 0);
        Volatile.Write(
            ref _state,
            (int)RelayGatewayRuntimeState.Stopped);
    }

    public void RecordAccepted(RelayGatewayEndpointRole role)
    {
        Interlocked.Increment(ref _accepted);
        Interlocked.Increment(ref _active);
        _acceptedInstrument.Add(1, RoleTag(role));
    }

    public void RecordRejected(RelayGatewayEndpointRole role)
    {
        Interlocked.Increment(ref _rejected);
        _rejectedInstrument.Add(1, RoleTag(role));
    }

    public void RecordWorkerAvailable(RelayGatewayEndpointRole role)
    {
        ref var target = ref WorkerField(role);
        Volatile.Write(
            ref target,
            (int)RelayGatewayWorkerAvailability.Available);
    }

    public void RecordWorkerUnavailable(RelayGatewayEndpointRole role)
    {
        Interlocked.Increment(ref _workerConnectFailures);
        ref var target = ref WorkerField(role);
        Volatile.Write(
            ref target,
            (int)RelayGatewayWorkerAvailability.Unavailable);
    }

    public void RecordBytes(
        RelayGatewayEndpointRole role,
        bool clientToWorker,
        int count)
    {
        if (count <= 0)
        {
            return;
        }

        if (clientToWorker)
        {
            Interlocked.Add(ref _bytesClientToWorker, count);
        }
        else
        {
            Interlocked.Add(ref _bytesWorkerToClient, count);
        }

        _bytesInstrument.Add(
            count,
            RoleTag(role),
            new(
                "direction",
                clientToWorker
                    ? "client_to_worker"
                    : "worker_to_client"));
    }

    public void RecordCompleted(
        RelayGatewayEndpointRole role,
        RelayGatewayConnectionOutcome outcome)
    {
        Interlocked.Decrement(ref _active);
        switch (outcome)
        {
            case RelayGatewayConnectionOutcome.Completed:
                Interlocked.Increment(ref _completed);
                break;
            case RelayGatewayConnectionOutcome.IdleTimeout:
            case RelayGatewayConnectionOutcome.WriteTimeout:
                Interlocked.Increment(ref _timedOut);
                break;
            case RelayGatewayConnectionOutcome.WorkerUnavailable:
            case RelayGatewayConnectionOutcome.TransportError:
                Interlocked.Increment(ref _faulted);
                break;
            case RelayGatewayConnectionOutcome.ServerShutdown:
                Interlocked.Increment(ref _shutdown);
                break;
        }

        _outcomeInstrument.Add(
            1,
            RoleTag(role),
            new("outcome", outcome.ToProtocolValue()));
    }

    public void Dispose() => _meter.Dispose();

    private ref int ListenerReadyField(RelayGatewayEndpointRole role)
    {
        if (role == RelayGatewayEndpointRole.Login)
        {
            return ref _loginListenerReady;
        }
        if (role == RelayGatewayEndpointRole.Game)
        {
            return ref _gameListenerReady;
        }

        throw new ArgumentOutOfRangeException(nameof(role));
    }

    private ref int WorkerField(RelayGatewayEndpointRole role)
    {
        if (role == RelayGatewayEndpointRole.Login)
        {
            return ref _loginWorker;
        }
        if (role == RelayGatewayEndpointRole.Game)
        {
            return ref _gameWorker;
        }

        throw new ArgumentOutOfRangeException(nameof(role));
    }

    private static KeyValuePair<string, object?> RoleTag(
        RelayGatewayEndpointRole role) =>
        new("endpoint", role.ToProtocolValue());

    private static RelayGatewayReadinessReason ReadinessReason(
        RelayGatewayRuntimeState state,
        bool loginListener,
        bool gameListener,
        RelayGatewayWorkerAvailability loginWorker,
        RelayGatewayWorkerAvailability gameWorker)
    {
        switch (state)
        {
            case RelayGatewayRuntimeState.Starting:
                return RelayGatewayReadinessReason.Starting;
            case RelayGatewayRuntimeState.Draining:
                return RelayGatewayReadinessReason.Draining;
            case RelayGatewayRuntimeState.Faulted:
                return RelayGatewayReadinessReason.Faulted;
            case RelayGatewayRuntimeState.Stopped:
                return RelayGatewayReadinessReason.Stopped;
        }

        if (!loginListener || !gameListener)
        {
            return RelayGatewayReadinessReason.ListenerUnavailable;
        }
        if (loginWorker == RelayGatewayWorkerAvailability.Unavailable)
        {
            return RelayGatewayReadinessReason.LoginWorkerUnavailable;
        }
        if (gameWorker == RelayGatewayWorkerAvailability.Unavailable)
        {
            return RelayGatewayReadinessReason.GameWorkerUnavailable;
        }

        return RelayGatewayReadinessReason.None;
    }
}
