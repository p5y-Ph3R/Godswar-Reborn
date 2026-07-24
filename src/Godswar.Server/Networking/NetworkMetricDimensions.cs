namespace Godswar.Server.Networking;

internal enum NetworkTrafficDirection : byte
{
    Inbound = 1,
    Outbound = 2
}

internal enum NetworkTimeoutStage : byte
{
    QueueAdmission = 1,
    FirstPacket = 2,
    PacketHeader = 3,
    PacketBody = 4,
    ReliableWrite = 5,
    Idle = 6,
    GracefulDrain = 7
}

internal enum NetworkDrainOutcome : byte
{
    Completed = 1,
    DeadlineExceeded = 2,
    Cancelled = 3
}

internal enum NetworkDisconnectReason : byte
{
    RemoteClosed = 1,
    ServerShutdown = 2,
    AdmissionRejected = 3,
    Timeout = 4,
    ReliableQueueOverflow = 5,
    ProtocolViolation = 6,
    TransportError = 7,
    HandlerCompleted = 8,
    HandlerError = 9,
    ApplicationDisconnect = 10
}

internal static class NetworkMetricDimensionExtensions
{
    public static string ToMetricTag(this NetworkTrafficDirection direction) =>
        direction switch
        {
            NetworkTrafficDirection.Inbound => "inbound",
            NetworkTrafficDirection.Outbound => "outbound",
            _ => throw new ArgumentOutOfRangeException(
                nameof(direction),
                direction,
                "Unknown network traffic direction.")
        };

    public static string ToMetricTag(this NetworkTimeoutStage stage) =>
        stage switch
        {
            NetworkTimeoutStage.QueueAdmission => "queue_admission",
            NetworkTimeoutStage.FirstPacket => "first_packet",
            NetworkTimeoutStage.PacketHeader => "packet_header",
            NetworkTimeoutStage.PacketBody => "packet_body",
            NetworkTimeoutStage.ReliableWrite => "reliable_write",
            NetworkTimeoutStage.Idle => "idle",
            NetworkTimeoutStage.GracefulDrain => "graceful_drain",
            _ => throw new ArgumentOutOfRangeException(
                nameof(stage),
                stage,
                "Unknown network timeout stage.")
        };

    public static string ToMetricTag(this NetworkDrainOutcome outcome) =>
        outcome switch
        {
            NetworkDrainOutcome.Completed => "completed",
            NetworkDrainOutcome.DeadlineExceeded => "deadline_exceeded",
            NetworkDrainOutcome.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(
                nameof(outcome),
                outcome,
                "Unknown network drain outcome.")
        };

    public static string ToMetricTag(this NetworkDisconnectReason reason) =>
        reason switch
        {
            NetworkDisconnectReason.RemoteClosed => "remote_closed",
            NetworkDisconnectReason.ServerShutdown => "server_shutdown",
            NetworkDisconnectReason.AdmissionRejected =>
                "admission_rejected",
            NetworkDisconnectReason.Timeout => "timeout",
            NetworkDisconnectReason.ReliableQueueOverflow =>
                "reliable_queue_overflow",
            NetworkDisconnectReason.ProtocolViolation =>
                "protocol_violation",
            NetworkDisconnectReason.TransportError => "transport_error",
            NetworkDisconnectReason.HandlerCompleted =>
                "handler_completed",
            NetworkDisconnectReason.HandlerError => "handler_error",
            NetworkDisconnectReason.ApplicationDisconnect =>
                "application_disconnect",
            _ => throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "Unknown network disconnect reason.")
        };
}
