using System.Diagnostics.Metrics;

namespace Godswar.Server.Networking;

internal static class NetworkRuntimeMetrics
{
    public const string MeterName = "Godswar.Server.Networking";

    private const string EndpointTagName = "network.endpoint.role";
    private const string DirectionTagName = "network.io.direction";
    private const string RejectionReasonTagName =
        "network.connection.rejection_reason";
    private const string DisconnectReasonTagName =
        "network.disconnect.reason";
    private const string StageTagName = "network.timeout.stage";
    private const string OutcomeTagName = "network.drain.outcome";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> AcceptedConnections =
        Meter.CreateCounter<long>(
            "godswar.server.network.connections.accepted",
            "{connection}",
            "Accepted TCP connections.");

    private static readonly Counter<long> RejectedConnections =
        Meter.CreateCounter<long>(
            "godswar.server.network.connections.rejected",
            "{connection}",
            "Connections rejected before a session starts.");

    private static readonly UpDownCounter<long> ActiveConnections =
        Meter.CreateUpDownCounter<long>(
            "godswar.server.network.connections.active",
            "{connection}",
            "Currently active accepted connections.");

    private static readonly UpDownCounter<long> TrackedConnectionTasks =
        Meter.CreateUpDownCounter<long>(
            "godswar.server.network.tasks.tracked",
            "{task}",
            "Currently tracked connection tasks.");

    private static readonly Counter<long> Timeouts =
        Meter.CreateCounter<long>(
            "godswar.server.network.timeouts",
            "{timeout}",
            "Network lifecycle timeouts by finite stage.");

    private static readonly UpDownCounter<long> ReliableQueueItems =
        Meter.CreateUpDownCounter<long>(
            "godswar.server.network.reliable_queue.items",
            "{item}",
            "Items currently held by reliable network queues.");

    private static readonly UpDownCounter<long> ReliableQueueBytes =
        Meter.CreateUpDownCounter<long>(
            "godswar.server.network.reliable_queue.bytes",
            "By",
            "Bytes currently held by reliable network queues.");

    private static readonly Counter<long> ReliableQueueOverflows =
        Meter.CreateCounter<long>(
            "godswar.server.network.reliable_queue.overflows",
            "{overflow}",
            "Reliable queue admission failures.");

    private static readonly Counter<long> TransportBytes =
        Meter.CreateCounter<long>(
            "godswar.server.network.transport.bytes",
            "By",
            "Bytes read from or written to network transports.");

    private static readonly Counter<long> DrainOutcomes =
        Meter.CreateCounter<long>(
            "godswar.server.network.drains",
            "{drain}",
            "Graceful connection-task drain outcomes.");

    private static readonly Counter<long> Disconnects =
        Meter.CreateCounter<long>(
            "godswar.server.network.disconnects",
            "{disconnect}",
            "Closed accepted connections by finite reason.");

    public static void RecordConnectionAccepted(NetworkEndpointRole endpoint)
    {
        var endpointTag = EndpointTag(endpoint);
        AcceptedConnections.Add(1, endpointTag);
        ActiveConnections.Add(1, endpointTag);
    }

    public static void RecordConnectionRejected(
        NetworkEndpointRole endpoint,
        ConnectionAdmissionRejection reason)
    {
        if (reason == ConnectionAdmissionRejection.None)
        {
            throw new ArgumentException(
                "A rejected connection requires a rejection reason.",
                nameof(reason));
        }

        RejectedConnections.Add(
            1,
            EndpointTag(endpoint),
            RejectionReasonTag(reason));
    }

    public static void RecordConnectionClosed(
        NetworkEndpointRole endpoint,
        NetworkDisconnectReason reason)
    {
        var endpointTag = EndpointTag(endpoint);
        var reasonTag = DisconnectReasonTag(reason);
        ActiveConnections.Add(-1, endpointTag);
        Disconnects.Add(1, endpointTag, reasonTag);
    }

    public static void RecordTrackedTaskStarted(NetworkEndpointRole endpoint)
    {
        TrackedConnectionTasks.Add(1, EndpointTag(endpoint));
    }

    public static void RecordTrackedTaskCompleted(NetworkEndpointRole endpoint)
    {
        TrackedConnectionTasks.Add(-1, EndpointTag(endpoint));
    }

    public static void RecordTimeout(
        NetworkEndpointRole endpoint,
        NetworkTimeoutStage stage)
    {
        Timeouts.Add(
            1,
            EndpointTag(endpoint),
            new KeyValuePair<string, object?>(
                StageTagName,
                stage.ToMetricTag()));
    }

    public static void RecordReliableQueueEnqueued(
        NetworkEndpointRole endpoint,
        NetworkTrafficDirection direction,
        int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);

        var endpointTag = EndpointTag(endpoint);
        var directionTag = DirectionTag(direction);
        ReliableQueueItems.Add(1, endpointTag, directionTag);
        ReliableQueueBytes.Add(byteCount, endpointTag, directionTag);
    }

    public static void RecordReliableQueueRemoved(
        NetworkEndpointRole endpoint,
        NetworkTrafficDirection direction,
        int itemCount,
        long byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);

        var endpointTag = EndpointTag(endpoint);
        var directionTag = DirectionTag(direction);
        ReliableQueueItems.Add(-itemCount, endpointTag, directionTag);
        ReliableQueueBytes.Add(-byteCount, endpointTag, directionTag);
    }

    public static void RecordReliableQueueOverflow(
        NetworkEndpointRole endpoint,
        NetworkTrafficDirection direction)
    {
        ReliableQueueOverflows.Add(
            1,
            EndpointTag(endpoint),
            DirectionTag(direction));
    }

    public static void RecordTransportBytes(
        NetworkEndpointRole endpoint,
        NetworkTrafficDirection direction,
        int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        if (byteCount == 0)
        {
            return;
        }

        TransportBytes.Add(
            byteCount,
            EndpointTag(endpoint),
            DirectionTag(direction));
    }

    public static void RecordDrainOutcome(
        NetworkEndpointRole endpoint,
        NetworkDrainOutcome outcome)
    {
        DrainOutcomes.Add(
            1,
            EndpointTag(endpoint),
            new KeyValuePair<string, object?>(
                OutcomeTagName,
                outcome.ToMetricTag()));
    }

    private static KeyValuePair<string, object?> EndpointTag(
        NetworkEndpointRole endpoint) =>
        new(EndpointTagName, endpoint.ToMetricTag());

    private static KeyValuePair<string, object?> DirectionTag(
        NetworkTrafficDirection direction) =>
        new(DirectionTagName, direction.ToMetricTag());

    private static KeyValuePair<string, object?> RejectionReasonTag(
        ConnectionAdmissionRejection reason) =>
        new(RejectionReasonTagName, reason.ToMetricTag());

    private static KeyValuePair<string, object?> DisconnectReasonTag(
        NetworkDisconnectReason reason) =>
        new(DisconnectReasonTagName, reason.ToMetricTag());
}
