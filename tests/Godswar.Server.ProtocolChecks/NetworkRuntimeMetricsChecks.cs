using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Godswar.Server.Networking;

namespace Godswar.Server.ProtocolChecks;

internal static class NetworkRuntimeMetricsChecks
{
    private static readonly HashSet<string> AllowedTagNames =
    [
        "network.endpoint.role",
        "network.io.direction",
        "network.connection.rejection_reason",
        "network.disconnect.reason",
        "network.timeout.stage",
        "network.drain.outcome"
    ];

    public static Task RunAsync()
    {
        CheckFiniteDimensionMappings();
        CheckMeasurementsUseOnlyBoundedDimensions();
        return Task.CompletedTask;
    }

    private static void CheckFiniteDimensionMappings()
    {
        Check.Equal(
            "outbound",
            NetworkTrafficDirection.Outbound.ToMetricTag(),
            "outbound traffic has a finite metric value");
        Check.Equal(
            "packet_body",
            NetworkTimeoutStage.PacketBody.ToMetricTag(),
            "packet body deadline has a finite metric value");
        Check.Equal(
            "deadline_exceeded",
            NetworkDrainOutcome.DeadlineExceeded.ToMetricTag(),
            "drain deadline has a finite metric value");
        Check.Equal(
            "reliable_queue_overflow",
            NetworkDisconnectReason.ReliableQueueOverflow.ToMetricTag(),
            "queue overflow disconnect has a finite metric value");

        Check.Throws<ArgumentOutOfRangeException>(
            () => ((NetworkTrafficDirection)byte.MaxValue).ToMetricTag(),
            "unknown traffic direction cannot become a metric value");
        Check.Throws<ArgumentOutOfRangeException>(
            () => ((NetworkTimeoutStage)byte.MaxValue).ToMetricTag(),
            "unknown timeout stage cannot become a metric value");
        Check.Throws<ArgumentOutOfRangeException>(
            () => ((NetworkDrainOutcome)byte.MaxValue).ToMetricTag(),
            "unknown drain outcome cannot become a metric value");
        Check.Throws<ArgumentOutOfRangeException>(
            () => ((NetworkDisconnectReason)byte.MaxValue).ToMetricTag(),
            "unknown disconnect reason cannot become a metric value");
    }

    private static void CheckMeasurementsUseOnlyBoundedDimensions()
    {
        var measurements = new ConcurrentQueue<CapturedMeasurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, candidate) =>
        {
            if (instrument.Meter.Name == NetworkRuntimeMetrics.MeterName)
            {
                candidate.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, _) =>
            {
                measurements.Enqueue(
                    new CapturedMeasurement(
                        instrument.Name,
                        measurement,
                        tags.ToArray()));
            });
        listener.Start();

        NetworkRuntimeMetrics.RecordConnectionAccepted(NetworkEndpointRole.Login);
        NetworkRuntimeMetrics.RecordConnectionRejected(
            NetworkEndpointRole.Game,
            ConnectionAdmissionRejection.PerIpLimit);
        NetworkRuntimeMetrics.RecordTrackedTaskStarted(NetworkEndpointRole.Game);
        NetworkRuntimeMetrics.RecordTimeout(
            NetworkEndpointRole.Game,
            NetworkTimeoutStage.PacketHeader);
        NetworkRuntimeMetrics.RecordReliableQueueEnqueued(
            NetworkEndpointRole.Game,
            NetworkTrafficDirection.Outbound,
            17);
        NetworkRuntimeMetrics.RecordReliableQueueRemoved(
            NetworkEndpointRole.Game,
            NetworkTrafficDirection.Outbound,
            1,
            17);
        NetworkRuntimeMetrics.RecordReliableQueueOverflow(
            NetworkEndpointRole.Game,
            NetworkTrafficDirection.Outbound);
        NetworkRuntimeMetrics.RecordTransportBytes(
            NetworkEndpointRole.Game,
            NetworkTrafficDirection.Inbound,
            23);
        NetworkRuntimeMetrics.RecordDrainOutcome(
            NetworkEndpointRole.Game,
            NetworkDrainOutcome.Completed);
        NetworkRuntimeMetrics.RecordTrackedTaskCompleted(NetworkEndpointRole.Game);
        NetworkRuntimeMetrics.RecordConnectionClosed(
            NetworkEndpointRole.Login,
            NetworkDisconnectReason.ServerShutdown);

        var captured = measurements.ToArray();
        Check.True(captured.Length >= 14, "runtime metrics emit every lifecycle family");
        Check.True(
            captured.Any(static value =>
                value.InstrumentName == "godswar.server.network.connections.rejected"
                && HasTag(
                    value,
                    "network.connection.rejection_reason",
                    "per_ip_limit")),
            "connection rejection exposes a finite reason");
        Check.True(
            captured.Any(static value =>
                value.InstrumentName == "godswar.server.network.timeouts"
                && HasTag(value, "network.timeout.stage", "packet_header")),
            "timeout metric exposes a finite stage");
        Check.True(
            captured.Any(static value =>
                value.InstrumentName == "godswar.server.network.transport.bytes"
                && value.Value == 23
                && HasTag(value, "network.io.direction", "inbound")),
            "transport bytes expose only a finite direction");

        foreach (var measurement in captured)
        {
            Check.True(
                measurement.Tags.All(static tag =>
                    AllowedTagNames.Contains(tag.Key)),
                $"{measurement.InstrumentName} uses only approved low-cardinality tag names");
            Check.True(
                measurement.Tags.All(static tag =>
                    tag.Value is string value && IsAllowedTagValue(tag.Key, value)),
                $"{measurement.InstrumentName} uses only finite tag values");
        }

        Check.Throws<ArgumentException>(
            () => NetworkRuntimeMetrics.RecordConnectionRejected(
                NetworkEndpointRole.Game,
                ConnectionAdmissionRejection.None),
            "a rejection metric requires a finite non-success reason");
        Check.Throws<ArgumentOutOfRangeException>(
            () => NetworkRuntimeMetrics.RecordTransportBytes(
                NetworkEndpointRole.Game,
                NetworkTrafficDirection.Inbound,
                -1),
            "negative transport bytes cannot corrupt metric accounting");
    }

    private static bool HasTag(
        CapturedMeasurement measurement,
        string name,
        string value)
    {
        return measurement.Tags.Any(
            tag => tag.Key == name && Equals(tag.Value, value));
    }

    private static bool IsAllowedTagValue(string name, string value)
    {
        return name switch
        {
            "network.endpoint.role" => value is "login" or "game",
            "network.io.direction" => value is "inbound" or "outbound",
            "network.connection.rejection_reason" => value is
                "active_limit"
                or "unauthenticated_limit"
                or "per_ip_limit"
                or "prefix_limit"
                or "invalid_remote_address"
                or "invalid_endpoint_role",
            "network.disconnect.reason" => value is
                "remote_closed"
                or "server_shutdown"
                or "admission_rejected"
                or "timeout"
                or "reliable_queue_overflow"
                or "protocol_violation"
                or "transport_error"
                or "handler_completed"
                or "handler_error"
                or "application_disconnect",
            "network.timeout.stage" => value is
                "queue_admission"
                or "first_packet"
                or "packet_header"
                or "packet_body"
                or "reliable_write"
                or "idle"
                or "graceful_drain",
            "network.drain.outcome" => value is
                "completed"
                or "deadline_exceeded"
                or "cancelled",
            _ => false
        };
    }

    private readonly record struct CapturedMeasurement(
        string InstrumentName,
        long Value,
        KeyValuePair<string, object?>[] Tags);
}
