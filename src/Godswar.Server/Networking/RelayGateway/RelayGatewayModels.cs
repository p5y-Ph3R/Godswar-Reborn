using System.Net;

namespace Godswar.Server.Networking.RelayGateway;

internal enum RelayGatewayEndpointRole : byte
{
    Login = 1,
    Game = 2
}

internal enum RelayGatewayRuntimeState : byte
{
    Starting = 1,
    Ready = 2,
    Draining = 3,
    Faulted = 4,
    Stopped = 5
}

internal enum RelayGatewayReadinessReason : byte
{
    None = 0,
    Starting = 1,
    ListenerUnavailable = 2,
    LoginWorkerUnavailable = 3,
    GameWorkerUnavailable = 4,
    Draining = 5,
    Faulted = 6,
    Stopped = 7
}

internal enum RelayGatewayWorkerAvailability : byte
{
    Unknown = 0,
    Available = 1,
    Unavailable = 2
}

internal enum RelayGatewayConnectionOutcome : byte
{
    Completed = 1,
    WorkerUnavailable = 2,
    IdleTimeout = 3,
    WriteTimeout = 4,
    TransportError = 5,
    ServerShutdown = 6
}

internal readonly record struct RelayGatewayEndpointConfiguration(
    RelayGatewayEndpointRole Role,
    IPEndPoint Bind,
    IPEndPoint Upstream);

internal readonly record struct RelayGatewayRuntimeLimits(
    int ListenBacklog,
    int MaximumConnections,
    int BufferSizeBytes,
    TimeSpan ConnectTimeout,
    TimeSpan IdleTimeout,
    TimeSpan WriteTimeout,
    TimeSpan DrainTimeout);

internal sealed record RelayGatewayConfiguration(
    RelayGatewayEndpointConfiguration Login,
    RelayGatewayEndpointConfiguration Game,
    RelayGatewayRuntimeLimits Limits);

internal readonly record struct RelayGatewayStartedEndpoints(
    IPEndPoint Login,
    IPEndPoint Game);

internal readonly record struct RelayGatewaySnapshot(
    RelayGatewayRuntimeState State,
    bool IsLive,
    bool IsReady,
    RelayGatewayReadinessReason ReadinessReason,
    RelayGatewayWorkerAvailability LoginWorker,
    RelayGatewayWorkerAvailability GameWorker,
    int ActiveConnections,
    int ConnectionCapacity,
    long AcceptedConnections,
    long RejectedConnections,
    long WorkerConnectFailures,
    long CompletedConnections,
    long FaultedConnections,
    long TimedOutConnections,
    long ShutdownConnections,
    long BytesClientToWorker,
    long BytesWorkerToClient);

internal static class RelayGatewayProtocolValues
{
    public static string ToProtocolValue(
        this RelayGatewayEndpointRole role) =>
        role switch
        {
            RelayGatewayEndpointRole.Login => "login",
            RelayGatewayEndpointRole.Game => "game",
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };

    public static string ToProtocolValue(
        this RelayGatewayConnectionOutcome outcome) =>
        outcome switch
        {
            RelayGatewayConnectionOutcome.Completed => "completed",
            RelayGatewayConnectionOutcome.WorkerUnavailable =>
                "worker_unavailable",
            RelayGatewayConnectionOutcome.IdleTimeout => "idle_timeout",
            RelayGatewayConnectionOutcome.WriteTimeout => "write_timeout",
            RelayGatewayConnectionOutcome.TransportError =>
                "transport_error",
            RelayGatewayConnectionOutcome.ServerShutdown =>
                "server_shutdown",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
}
