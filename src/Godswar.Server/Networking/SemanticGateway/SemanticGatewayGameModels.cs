using System.Net;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking.Backhaul;

namespace Godswar.Server.Networking.SemanticGateway;

internal sealed record SemanticGatewayWorkerTarget
{
    public SemanticGatewayWorkerTarget(
        ServerNodeId nodeId,
        IPEndPoint endpoint,
        string tlsHost,
        BackhaulCertificatePins certificatePins)
    {
        if (!nodeId.IsValid)
        {
            throw new ArgumentException(
                "A valid worker node ID is required.",
                nameof(nodeId));
        }
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(endpoint));
        }
        if (string.IsNullOrWhiteSpace(tlsHost) ||
            tlsHost.Length > 253)
        {
            throw new ArgumentException(
                "A bounded worker TLS host is required.",
                nameof(tlsHost));
        }

        NodeId = nodeId;
        Endpoint = new IPEndPoint(endpoint.Address, endpoint.Port);
        TlsHost = tlsHost;
        CertificatePins = certificatePins ??
            throw new ArgumentNullException(nameof(certificatePins));
    }

    public ServerNodeId NodeId { get; }

    public IPEndPoint Endpoint { get; }

    public string TlsHost { get; }

    public BackhaulCertificatePins CertificatePins { get; }
}

internal enum SemanticGatewayGameOutcome : byte
{
    Completed = 1,
    LoginNotFound = 2,
    CharacterUnavailable = 3,
    RouteUnavailable = 4,
    AdmissionRejected = 5,
    WorkerUnavailable = 6,
    ProtocolRejected = 7,
    IdleTimeout = 8,
    TransportError = 9,
    ServerShutdown = 10
}

internal readonly record struct SemanticGatewayGameSnapshot(
    int ActiveConnections,
    int ConnectionCapacity,
    long AcceptedConnections,
    long RejectedConnections,
    long CompletedConnections,
    long LoginRejections,
    long RouteRejections,
    long AdmissionRejections,
    long WorkerFailures,
    long ProtocolRejections,
    long TimedOutConnections,
    long TransportFailures,
    long ShutdownConnections,
    long BytesClientToWorker,
    long BytesWorkerToClient);

internal sealed class SemanticGatewayGameMetrics
{
    private readonly int _capacity;
    private long _accepted;
    private int _active;
    private long _admissionRejected;
    private long _bytesClientToWorker;
    private long _bytesWorkerToClient;
    private long _completed;
    private long _loginRejected;
    private long _protocolRejected;
    private long _rejected;
    private long _routeRejected;
    private long _shutdown;
    private long _timedOut;
    private long _transportFailed;
    private long _workerFailed;

    public SemanticGatewayGameMetrics(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
    }

    public void RecordAccepted()
    {
        Interlocked.Increment(ref _accepted);
        Interlocked.Increment(ref _active);
    }

    public void RecordRejected() =>
        Interlocked.Increment(ref _rejected);

    public void RecordBytes(bool clientToWorker, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (clientToWorker)
        {
            Interlocked.Add(ref _bytesClientToWorker, count);
        }
        else
        {
            Interlocked.Add(ref _bytesWorkerToClient, count);
        }
    }

    public void RecordCompleted(SemanticGatewayGameOutcome outcome)
    {
        Interlocked.Decrement(ref _active);
        switch (outcome)
        {
            case SemanticGatewayGameOutcome.Completed:
                Interlocked.Increment(ref _completed);
                break;
            case SemanticGatewayGameOutcome.LoginNotFound:
                Interlocked.Increment(ref _loginRejected);
                break;
            case SemanticGatewayGameOutcome.CharacterUnavailable:
            case SemanticGatewayGameOutcome.RouteUnavailable:
                Interlocked.Increment(ref _routeRejected);
                break;
            case SemanticGatewayGameOutcome.AdmissionRejected:
                Interlocked.Increment(ref _admissionRejected);
                break;
            case SemanticGatewayGameOutcome.WorkerUnavailable:
                Interlocked.Increment(ref _workerFailed);
                break;
            case SemanticGatewayGameOutcome.ProtocolRejected:
                Interlocked.Increment(ref _protocolRejected);
                break;
            case SemanticGatewayGameOutcome.IdleTimeout:
                Interlocked.Increment(ref _timedOut);
                break;
            case SemanticGatewayGameOutcome.TransportError:
                Interlocked.Increment(ref _transportFailed);
                break;
            case SemanticGatewayGameOutcome.ServerShutdown:
                Interlocked.Increment(ref _shutdown);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome));
        }
    }

    public SemanticGatewayGameSnapshot GetSnapshot() =>
        new(
            Volatile.Read(ref _active),
            _capacity,
            Interlocked.Read(ref _accepted),
            Interlocked.Read(ref _rejected),
            Interlocked.Read(ref _completed),
            Interlocked.Read(ref _loginRejected),
            Interlocked.Read(ref _routeRejected),
            Interlocked.Read(ref _admissionRejected),
            Interlocked.Read(ref _workerFailed),
            Interlocked.Read(ref _protocolRejected),
            Interlocked.Read(ref _timedOut),
            Interlocked.Read(ref _transportFailed),
            Interlocked.Read(ref _shutdown),
            Interlocked.Read(ref _bytesClientToWorker),
            Interlocked.Read(ref _bytesWorkerToClient));
}
