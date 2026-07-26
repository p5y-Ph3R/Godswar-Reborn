using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecurePhase4AcceptanceFaultChecks
{
    private static void CheckLowCardinalityEvidence()
    {
        var measurements =
            new ConcurrentQueue<EvidenceMeasurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished =
            (instrument, meterListener) =>
            {
                if (instrument.Meter.Name ==
                        SecureNetworkMetrics.MeterName &&
                    instrument.Name ==
                        "godswar.server.network.secure.acceptance.phase4")
                {
                    meterListener.EnableMeasurementEvents(
                        instrument);
                }
            };
        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, _) =>
            {
                string? tagName = null;
                string? outcome = null;
                var tagCount = 0;
                foreach (var tag in tags)
                {
                    tagCount++;
                    tagName = tag.Key;
                    outcome = tag.Value as string;
                }
                measurements.Enqueue(
                    new EvidenceMeasurement(
                        instrument.Name,
                        measurement,
                        tagCount,
                        tagName,
                        outcome));
            });
        listener.Start();

        var originalOutput = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try
        {
            EmitEveryEvidenceOutcome();
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        var allowedOutcomes = new HashSet<string>(
            StringComparer.Ordinal)
        {
            "enabled",
            "campaign_started",
            "snapshot_ack_dropped",
            "snapshot_drop_window_completed",
            "tls_fallback_observed",
            "correction_forced",
            "tls_no_switchback_observed",
            "campaign_expired"
        };
        var observedOutcomes = new HashSet<string>(
            StringComparer.Ordinal);
        foreach (var measurement in measurements)
        {
            Check.True(
                measurement.InstrumentName ==
                    "godswar.server.network.secure.acceptance.phase4" &&
                measurement.Value == 1 &&
                measurement.TagCount == 1 &&
                measurement.TagName ==
                    "network.secure.acceptance.phase4.outcome" &&
                measurement.Outcome is not null &&
                allowedOutcomes.Contains(measurement.Outcome),
                "acceptance evidence uses one fixed low-cardinality outcome tag");
            observedOutcomes.Add(measurement.Outcome!);
        }
        Check.True(
            allowedOutcomes.SetEquals(observedOutcomes),
            "metrics expose every fixed acceptance outcome");
        Check.Equal(
            34,
            measurements.Count(static measurement =>
                measurement.Outcome ==
                    "snapshot_ack_dropped"),
            "per-drop metrics saturate at 32 for high-rate traffic");

        var allowedLogLines = new HashSet<string>(
            StringComparer.Ordinal)
        {
            "[secure-acceptance] phase4 fault campaign enabled",
            "[secure-acceptance] snapshot ACK drop started window_ms=1500 max_recorded_drops=32",
            "[secure-acceptance] snapshot ACK drop window completed",
            "[secure-acceptance] one-way TLS fallback observed",
            "[secure-acceptance] authoritative correction forced reason=not_ready",
            "[secure-acceptance] post-fallback TLS movement observed no_switchback=true",
            "[secure-acceptance] phase4 fault campaign expired"
        };
        var logLines = output.ToString().Split(
            Environment.NewLine,
            StringSplitOptions.RemoveEmptyEntries);
        Check.True(
            logLines.All(allowedLogLines.Contains),
            "acceptance logs contain only fixed evidence lines");
        Check.True(
            allowedLogLines.SetEquals(logLines),
            "logs expose every fixed acceptance outcome without identifiers");
    }

    private static void EmitEveryEvidenceOutcome()
    {
        var boundedTime = new ManualTimeProvider();
        var bounded = new SecurePhase4AcceptanceFaults(
            boundedTime);
        var selected = new SecureUdpConnectionKey(
            0x1111_2222_3333_4444,
            0x5555_6666_7777_8888);
        var dispatch = Dispatch(selected, CreateSnapshot());
        for (var index = 0; index < 128; index++)
        {
            bounded.ShouldDropSnapshot(dispatch);
        }
        boundedTime.Advance(
            SecurePhase4AcceptanceFaults.SnapshotDropWindow);
        bounded.ShouldDropSnapshot(dispatch);

        var completion = new SecurePhase4AcceptanceFaults(
            new ManualTimeProvider());
        completion.ShouldDropSnapshot(dispatch);
        completion.ShouldForceCorrection(
            selected,
            CreateIngress(
                SecureRealtimeTransportSource.Tls,
                SecureRealtimeMovementIngressKind.Input,
                epoch: 2,
                inputId: 2));
        completion.ShouldForceCorrection(
            selected,
            CreateIngress(
                SecureRealtimeTransportSource.Tls,
                SecureRealtimeMovementIngressKind.Input,
                epoch: 2,
                inputId: 3));

        var expiryTime = new ManualTimeProvider();
        var expiry = new SecurePhase4AcceptanceFaults(
            expiryTime);
        expiry.ShouldDropSnapshot(dispatch);
        expiryTime.Advance(
            SecurePhase4AcceptanceFaults.CampaignLifetime);
        expiry.GetSnapshot();
    }

    private readonly record struct EvidenceMeasurement(
        string InstrumentName,
        long Value,
        int TagCount,
        string? TagName,
        string? Outcome);
}
