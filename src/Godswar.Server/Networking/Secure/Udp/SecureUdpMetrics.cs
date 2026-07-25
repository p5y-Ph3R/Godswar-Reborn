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
    TransportError = 10
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
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
}

internal static class SecureUdpMetrics
{
    private const string OutcomeTagName = "network.secure.udp.outcome";

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
}
