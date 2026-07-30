using System.Diagnostics.Metrics;
using Godswar.Server.Operations.Observability;

namespace Godswar.Server.ProtocolChecks;

internal static class B13PrometheusCollectorChecks
{
    public static async Task RunAsync()
    {
        CheckAggregationAndSensitiveTagRejection();
        CheckObservableCountersAreSnapshots();
        CheckObservableSeriesExpire();
        CheckSnapshotBound();
        await CheckRenderingDoesNotBlockProducersAsync();
    }

    private static void CheckAggregationAndSensitiveTagRejection()
    {
        const string meterName =
            "Godswar.Server.ProtocolChecks.B13.Primary";
        using var collector = new BoundedPrometheusCollector(
            new PrometheusCollectorOptions
            {
                AllowedMeterPrefixes = [meterName],
                MaximumSeries = 3,
                MaximumSnapshotBytes = 65_536
            });
        using var meter = new Meter(meterName);
        var requests = meter.CreateCounter<long>(
            "b13_requests_total");
        var duration = meter.CreateHistogram<double>(
            "b13_request_duration_ms",
            unit: "ms");

        requests.Add(
            1,
            new KeyValuePair<string, object?>(
                "outcome",
                "accepted"));
        requests.Add(
            2,
            new KeyValuePair<string, object?>(
                "outcome",
                "accepted"));
        requests.Add(
            1,
            new KeyValuePair<string, object?>(
                "outcome",
                "rejected"));
        duration.Record(
            0.75d,
            new KeyValuePair<string, object?>(
                "outcome",
                "accepted"));
        duration.Record(
            7d,
            new KeyValuePair<string, object?>(
                "outcome",
                "accepted"));
        duration.Record(
            80d,
            new KeyValuePair<string, object?>(
                "outcome",
                "accepted"));

        // The three accepted label series consume the configured bound.
        requests.Add(
            1,
            new KeyValuePair<string, object?>(
                "outcome",
                "busy"));
        requests.Add(
            1,
            new KeyValuePair<string, object?>(
                "account_id",
                "alice"));

        var output = collector.CollectSnapshot();
        Check.True(
            output.Contains(
                "b13_requests_total{outcome=\"accepted\"} 3\n",
                StringComparison.Ordinal),
            "counter values are aggregated by finite labels");
        Check.True(
            output.Contains(
                "# TYPE b13_request_duration_ms histogram\n",
                StringComparison.Ordinal),
            "duration metric is exported as a histogram");
        Check.True(
            output.Contains(
                "b13_request_duration_ms_bucket" +
                "{outcome=\"accepted\",le=\"1\"} 1\n",
                StringComparison.Ordinal),
            "millisecond histogram has a cumulative one-ms bucket");
        Check.True(
            output.Contains(
                "b13_request_duration_ms_bucket" +
                "{outcome=\"accepted\",le=\"10\"} 2\n",
                StringComparison.Ordinal),
            "millisecond histogram has a cumulative ten-ms bucket");
        Check.True(
            output.Contains(
                "b13_request_duration_ms_bucket" +
                "{outcome=\"accepted\",le=\"+Inf\"} 3\n",
                StringComparison.Ordinal),
            "millisecond histogram has a cumulative infinity bucket");
        Check.True(
            !output.Contains("account_id", StringComparison.Ordinal) &&
            !output.Contains("alice", StringComparison.Ordinal),
            "identity labels and values are absent from metrics");

        var state = collector.GetSnapshot();
        Check.Equal(3, state.Series, "metric series bound");
        Check.Equal(
            1L,
            state.DroppedSeries,
            "series over the configured bound are counted");
        Check.Equal(
            1L,
            state.DroppedTags,
            "sensitive tag names are rejected and counted");
    }

    private static void CheckSnapshotBound()
    {
        const string meterName =
            "Godswar.Server.ProtocolChecks.B13.Truncated";
        using var collector = new BoundedPrometheusCollector(
            new PrometheusCollectorOptions
            {
                AllowedMeterPrefixes = [meterName],
                MaximumSeries = 16,
                MaximumSnapshotBytes = 1_024
            });
        using var meter = new Meter(meterName);
        var duration = meter.CreateHistogram<double>(
            "b13_bounded_duration_ms",
            unit: "ms");
        duration.Record(
            5d,
            new KeyValuePair<string, object?>(
                "outcome",
                "accepted"));
        duration.Record(
            500d,
            new KeyValuePair<string, object?>(
                "outcome",
                "rejected"));

        var output = collector.CollectSnapshot();
        Check.True(
            output.Length <= 1_024,
            "Prometheus snapshot respects its byte bound");
        Check.True(
            collector.GetSnapshot().TruncatedSnapshots > 0,
            "bounded snapshot truncation is observable");
        Check.True(
            output.StartsWith(
                "godswar_server_metrics_collector",
                StringComparison.Ordinal) &&
            output.Contains(
                "state=\"truncated_snapshots\"",
                StringComparison.Ordinal),
            "collector pressure remains present in truncated snapshots");
    }

    private static void CheckObservableCountersAreSnapshots()
    {
        const string meterName =
            "Godswar.Server.ProtocolChecks.B13.Observable";
        using var collector = new BoundedPrometheusCollector(
            new PrometheusCollectorOptions
            {
                AllowedMeterPrefixes = [meterName],
                MaximumSnapshotBytes = 16_384
            });
        using var meter = new Meter(meterName);
        long current = 5;
        meter.CreateObservableCounter(
            "b13_observable_total",
            () => current);

        var first = collector.CollectSnapshot();
        var second = collector.CollectSnapshot();
        Check.True(
            first.Contains(
                "b13_observable_total 5\n",
                StringComparison.Ordinal) &&
            second.Contains(
                "b13_observable_total 5\n",
                StringComparison.Ordinal),
            "observable counters replace rather than re-add snapshots");

        current = 8;
        var third = collector.CollectSnapshot();
        Check.True(
            third.Contains(
                "b13_observable_total 8\n",
                StringComparison.Ordinal),
            "observable counter follows its latest callback value");
    }

    private static void CheckObservableSeriesExpire()
    {
        const string meterName =
            "Godswar.Server.ProtocolChecks.B13.ObservableState";
        using var collector = new BoundedPrometheusCollector(
            new PrometheusCollectorOptions
            {
                AllowedMeterPrefixes = [meterName],
                MaximumSnapshotBytes = 16_384
            });
        using var meter = new Meter(meterName);
        var state = "starting";
        meter.CreateObservableGauge(
            "b13_runtime_state",
            () => new Measurement<int>(
                1,
                new KeyValuePair<string, object?>(
                    "state",
                    state)));

        var first = collector.CollectSnapshot();
        state = "running";
        var second = collector.CollectSnapshot();
        Check.True(
            first.Contains(
                "b13_runtime_state{state=\"starting\"} 1\n",
                StringComparison.Ordinal) &&
            second.Contains(
                "b13_runtime_state{state=\"running\"} 1\n",
                StringComparison.Ordinal) &&
            !second.Contains(
                "state=\"starting\"",
                StringComparison.Ordinal),
            "observable label sets expire when a callback stops reporting them");
        Check.Equal(
            1,
            collector.GetSnapshot().Series,
            "stale observable series no longer consume the series bound");
    }

    private static async Task CheckRenderingDoesNotBlockProducersAsync()
    {
        const string meterName =
            "Godswar.Server.ProtocolChecks.B13.Concurrent";
        using var captured = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var collector = new BoundedPrometheusCollector(
            new PrometheusCollectorOptions
            {
                AllowedMeterPrefixes = [meterName],
                MaximumSnapshotBytes = 16_384,
                SnapshotCaptured = () =>
                {
                    captured.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException(
                            "Synthetic renderer was not released.");
                    }
                }
            });
        using var meter = new Meter(meterName);
        var requests = meter.CreateCounter<long>(
            "b13_concurrent_requests_total");
        requests.Add(1);

        var scrape = Task.Run(collector.CollectSnapshot);
        Check.True(
            captured.Wait(TimeSpan.FromSeconds(2)),
            "scrape pauses after capturing its immutable snapshot");

        var producer = Task.Run(() => requests.Add(1));
        try
        {
            Check.True(
                await CompletesWithinAsync(
                    producer,
                    TimeSpan.FromSeconds(1)),
                "metric producer never waits behind snapshot rendering");
        }
        finally
        {
            release.Set();
            await scrape;
            await producer;
        }

        Check.Equal(
            2L,
            collector.GetSnapshot().Measurements,
            "producer measurement is retained while rendering is stalled");
    }

    private static async Task<bool> CompletesWithinAsync(
        Task task,
        TimeSpan timeout)
    {
        var completed = await Task.WhenAny(
            task,
            Task.Delay(timeout));
        return ReferenceEquals(completed, task);
    }
}
