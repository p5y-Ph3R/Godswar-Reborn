using System.Diagnostics.Metrics;

namespace Godswar.Server.Networking.Secure;

internal enum SecureHandshakeOutcome : byte
{
    Accepted = 1,
    DeadlineExceeded = 2,
    AuthenticationFailed = 3,
    PolicyRejected = 4,
    Cancelled = 5
}

internal enum SecurePrefaceOutcome : byte
{
    Accepted = 1,
    DeadlineExceeded = 2,
    Malformed = 3,
    UnsupportedVersion = 4,
    WrongEndpoint = 5,
    UnsupportedBuild = 6,
    PolicyRejected = 7
}

internal enum SecureFrameOutcome : byte
{
    Accepted = 1,
    Malformed = 2,
    WrongPhase = 3,
    QueueOverflow = 4,
    DeadlineExceeded = 5,
    Rejected = 6
}

internal static class SecureNetworkMetricTags
{
    public static string ToMetricTag(this SecureHandshakeOutcome outcome) =>
        outcome switch
        {
            SecureHandshakeOutcome.Accepted => "accepted",
            SecureHandshakeOutcome.DeadlineExceeded => "deadline_exceeded",
            SecureHandshakeOutcome.AuthenticationFailed =>
                "authentication_failed",
            SecureHandshakeOutcome.PolicyRejected => "policy_rejected",
            SecureHandshakeOutcome.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

    public static string ToMetricTag(this SecurePrefaceOutcome outcome) =>
        outcome switch
        {
            SecurePrefaceOutcome.Accepted => "accepted",
            SecurePrefaceOutcome.DeadlineExceeded => "deadline_exceeded",
            SecurePrefaceOutcome.Malformed => "malformed",
            SecurePrefaceOutcome.UnsupportedVersion =>
                "unsupported_version",
            SecurePrefaceOutcome.WrongEndpoint => "wrong_endpoint",
            SecurePrefaceOutcome.UnsupportedBuild =>
                "unsupported_build",
            SecurePrefaceOutcome.PolicyRejected => "policy_rejected",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

    public static string ToMetricTag(this SecureFrameOutcome outcome) =>
        outcome switch
        {
            SecureFrameOutcome.Accepted => "accepted",
            SecureFrameOutcome.Malformed => "malformed",
            SecureFrameOutcome.WrongPhase => "wrong_phase",
            SecureFrameOutcome.QueueOverflow => "queue_overflow",
            SecureFrameOutcome.DeadlineExceeded => "deadline_exceeded",
            SecureFrameOutcome.Rejected => "rejected",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
}

internal static class SecureNetworkMetrics
{
    public const string MeterName = "Godswar.Server.Networking.Secure";

    private const string EndpointTagName = "network.endpoint.role";
    private const string OutcomeTagName = "network.secure.outcome";

    private static readonly Meter Meter = new(MeterName);

    private static readonly UpDownCounter<long> ActiveHandshakes =
        Meter.CreateUpDownCounter<long>(
            "godswar.server.network.tls.handshakes.active",
            "{handshake}");

    private static readonly Counter<long> HandshakeOutcomes =
        Meter.CreateCounter<long>(
            "godswar.server.network.tls.handshakes",
            "{handshake}");

    private static readonly Histogram<double> HandshakeDuration =
        Meter.CreateHistogram<double>(
            "godswar.server.network.tls.handshake.duration",
            "ms");

    private static readonly Counter<long> PrefaceOutcomes =
        Meter.CreateCounter<long>(
            "godswar.server.network.secure.prefaces",
            "{preface}");

    private static readonly Counter<long> FrameOutcomes =
        Meter.CreateCounter<long>(
            "godswar.server.network.secure.frames",
            "{frame}");

    private static readonly UpDownCounter<long> IngressQueueItems =
        Meter.CreateUpDownCounter<long>(
            "godswar.server.network.secure.ingress.items",
            "{item}");

    private static readonly UpDownCounter<long> IngressQueueBytes =
        Meter.CreateUpDownCounter<long>(
            "godswar.server.network.secure.ingress.bytes",
            "By");

    private static readonly UpDownCounter<long> ControlQueueItems =
        Meter.CreateUpDownCounter<long>(
            "godswar.server.network.secure.control.items",
            "{item}");

    private static readonly UpDownCounter<long> ControlQueueBytes =
        Meter.CreateUpDownCounter<long>(
            "godswar.server.network.secure.control.bytes",
            "By");

    public static void HandshakeStarted(NetworkEndpointRole endpoint)
    {
        ActiveHandshakes.Add(1, Endpoint(endpoint));
    }

    public static void HandshakeCompleted(
        NetworkEndpointRole endpoint,
        SecureHandshakeOutcome outcome,
        TimeSpan duration)
    {
        var endpointTag = Endpoint(endpoint);
        ActiveHandshakes.Add(-1, endpointTag);
        HandshakeOutcomes.Add(1, endpointTag, Outcome(outcome.ToMetricTag()));
        HandshakeDuration.Record(
            Math.Max(0, duration.TotalMilliseconds),
            endpointTag,
            Outcome(outcome.ToMetricTag()));
    }

    public static void HandshakeRejectedBeforeAdmission(
        NetworkEndpointRole endpoint,
        SecureHandshakeOutcome outcome,
        TimeSpan duration)
    {
        HandshakeOutcomes.Add(
            1,
            Endpoint(endpoint),
            Outcome(outcome.ToMetricTag()));
        HandshakeDuration.Record(
            Math.Max(0, duration.TotalMilliseconds),
            Endpoint(endpoint),
            Outcome(outcome.ToMetricTag()));
    }

    public static void PrefaceCompleted(
        NetworkEndpointRole endpoint,
        SecurePrefaceOutcome outcome)
    {
        PrefaceOutcomes.Add(
            1,
            Endpoint(endpoint),
            Outcome(outcome.ToMetricTag()));
    }

    public static void FrameCompleted(
        NetworkEndpointRole endpoint,
        SecureFrameOutcome outcome)
    {
        FrameOutcomes.Add(
            1,
            Endpoint(endpoint),
            Outcome(outcome.ToMetricTag()));
    }

    public static void IngressEnqueued(
        NetworkEndpointRole endpoint,
        int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        var endpointTag = Endpoint(endpoint);
        IngressQueueItems.Add(1, endpointTag);
        IngressQueueBytes.Add(byteCount, endpointTag);
    }

    public static void IngressRemoved(
        NetworkEndpointRole endpoint,
        int itemCount,
        long byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        var endpointTag = Endpoint(endpoint);
        IngressQueueItems.Add(-itemCount, endpointTag);
        IngressQueueBytes.Add(-byteCount, endpointTag);
    }

    public static void ControlEnqueued(
        NetworkEndpointRole endpoint,
        int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        var endpointTag = Endpoint(endpoint);
        ControlQueueItems.Add(1, endpointTag);
        ControlQueueBytes.Add(byteCount, endpointTag);
    }

    public static void ControlRemoved(
        NetworkEndpointRole endpoint,
        int itemCount,
        long byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        var endpointTag = Endpoint(endpoint);
        ControlQueueItems.Add(-itemCount, endpointTag);
        ControlQueueBytes.Add(-byteCount, endpointTag);
    }

    private static KeyValuePair<string, object?> Endpoint(
        NetworkEndpointRole endpoint) =>
        new(EndpointTagName, endpoint.ToMetricTag());

    private static KeyValuePair<string, object?> Outcome(string outcome) =>
        new(OutcomeTagName, outcome);
}
