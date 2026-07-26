using System.Diagnostics.Metrics;

namespace Godswar.Server.Networking.Secure.Udp;

internal enum SecureUdpDatagramOutcome : byte
{
    ChallengeSent = 1,
    Bound = 2,
    Idempotent = 3,
    Malformed = 4,
    RateLimited = 5,
    UnknownSession = 6,
    Expired = 7,
    InvalidSessionProof = 8,
    EndpointConflict = 9,
    TransportError = 10,
    Rebound = 11,
    ReplayRejected = 12,
    RebindRateLimited = 13,
    ProtectedPongSent = 14,
    ProtectedRejected = 15,
    EndpointMismatch = 16,
    RealtimeMovementAccepted = 17,
    RealtimeMovementDeduplicated = 18,
    RealtimeMovementRejected = 19,
    RealtimeSnapshotSent = 20
}

internal enum SecureUdpRuntimeOutcome : byte
{
    Started = 1,
    Stopped = 2,
    Faulted = 3,
    SessionExpired = 4,
    KeyRotated = 5,
    KeyEpochExhausted = 6
}

internal static class SecureUdpMetricTags
{
    public static string ToMetricTag(
        this SecureUdpDatagramOutcome outcome) =>
        outcome switch
        {
            SecureUdpDatagramOutcome.ChallengeSent =>
                "challenge_sent",
            SecureUdpDatagramOutcome.Bound => "bound",
            SecureUdpDatagramOutcome.Idempotent => "idempotent",
            SecureUdpDatagramOutcome.Malformed => "malformed",
            SecureUdpDatagramOutcome.RateLimited => "rate_limited",
            SecureUdpDatagramOutcome.UnknownSession =>
                "unknown_session",
            SecureUdpDatagramOutcome.Expired => "expired",
            SecureUdpDatagramOutcome.InvalidSessionProof =>
                "invalid_session_proof",
            SecureUdpDatagramOutcome.EndpointConflict =>
                "endpoint_conflict",
            SecureUdpDatagramOutcome.TransportError =>
                "transport_error",
            SecureUdpDatagramOutcome.Rebound => "rebound",
            SecureUdpDatagramOutcome.ReplayRejected =>
                "replay_rejected",
            SecureUdpDatagramOutcome.RebindRateLimited =>
                "rebind_rate_limited",
            SecureUdpDatagramOutcome.ProtectedPongSent =>
                "protected_pong_sent",
            SecureUdpDatagramOutcome.ProtectedRejected =>
                "protected_rejected",
            SecureUdpDatagramOutcome.EndpointMismatch =>
                "endpoint_mismatch",
            SecureUdpDatagramOutcome.RealtimeMovementAccepted =>
                "realtime_movement_accepted",
            SecureUdpDatagramOutcome
                    .RealtimeMovementDeduplicated =>
                "realtime_movement_deduplicated",
            SecureUdpDatagramOutcome.RealtimeMovementRejected =>
                "realtime_movement_rejected",
            SecureUdpDatagramOutcome.RealtimeSnapshotSent =>
                "realtime_snapshot_sent",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

    public static string ToMetricTag(
        this SecureUdpRuntimeOutcome outcome) =>
        outcome switch
        {
            SecureUdpRuntimeOutcome.Started => "started",
            SecureUdpRuntimeOutcome.Stopped => "stopped",
            SecureUdpRuntimeOutcome.Faulted => "faulted",
            SecureUdpRuntimeOutcome.SessionExpired =>
                "session_expired",
            SecureUdpRuntimeOutcome.KeyRotated => "key_rotated",
            SecureUdpRuntimeOutcome.KeyEpochExhausted =>
                "key_epoch_exhausted",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
}

internal static class SecureUdpMetrics
{
    private const string OutcomeTagName = "network.secure.udp.outcome";
    private const string RuntimeOutcomeTagName =
        "network.secure.udp.runtime_outcome";

    private static readonly Meter Meter =
        new(SecureNetworkMetrics.MeterName);

    private static readonly Counter<long> ReceivedPackets =
        Meter.CreateCounter<long>(
            "godswar.server.network.secure.udp.received.packets",
            "{packet}");

    private static readonly Counter<long> ReceivedBytes =
        Meter.CreateCounter<long>(
            "godswar.server.network.secure.udp.received.bytes",
            "By");

    private static readonly Counter<long> SentPackets =
        Meter.CreateCounter<long>(
            "godswar.server.network.secure.udp.sent.packets",
            "{packet}");

    private static readonly Counter<long> SentBytes =
        Meter.CreateCounter<long>(
            "godswar.server.network.secure.udp.sent.bytes",
            "By");

    private static readonly Counter<long> Outcomes =
        Meter.CreateCounter<long>(
            "godswar.server.network.secure.udp.datagrams",
            "{datagram}");

    private static readonly Counter<long> RuntimeOutcomes =
        Meter.CreateCounter<long>(
            "godswar.server.network.secure.udp.runtime",
            "{event}");

    public static void DatagramReceived(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        ReceivedPackets.Add(1);
        ReceivedBytes.Add(byteCount);
    }

    public static void DatagramSent(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        SentPackets.Add(1);
        SentBytes.Add(byteCount);
    }

    public static void RecordOutcome(SecureUdpDatagramOutcome outcome)
    {
        Outcomes.Add(
            1,
            new KeyValuePair<string, object?>(
                OutcomeTagName,
                outcome.ToMetricTag()));
    }

    public static void RecordRuntimeOutcome(
        SecureUdpRuntimeOutcome outcome,
        long count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        RuntimeOutcomes.Add(
            count,
            new KeyValuePair<string, object?>(
                RuntimeOutcomeTagName,
                outcome.ToMetricTag()));
    }
}
