using System.Diagnostics.Metrics;
using Godswar.Server.Operations;
using Godswar.Server.Operations.Observability;

namespace Godswar.Server.ProtocolChecks;

internal static class ServerOperationsMetricsChecks
{
    private const long ExpectedProcessStartUnixMilliseconds =
        1_774_000_123_456L;
    private const double ExpectedProcessStartUnixSeconds =
        1_774_000_123.456d;
    private const string ExpectedExportedProcessStartUnixSeconds =
        "1774000123.456";
    private const string ExportedProcessStartMetricName =
        "godswar_server_operations_process_start_time_seconds";

    public static Task RunAsync()
    {
        CheckStableProcessStartMetric();
        return Task.CompletedTask;
    }

    private static void CheckStableProcessStartMetric()
    {
        Instrument? publishedInstrument = null;
        var measurements = new List<double>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, candidate) =>
            {
                if (instrument.Meter.Name ==
                        ServerOperationsMetrics.MeterName &&
                    instrument.Name ==
                        ServerOperationsMetrics
                            .ProcessStartTimeInstrumentName)
                {
                    publishedInstrument = instrument;
                    candidate.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>(
            (instrument, measurement, _, _) =>
            {
                if (instrument.Name ==
                    ServerOperationsMetrics.ProcessStartTimeInstrumentName)
                {
                    measurements.Add(measurement);
                }
            });
        listener.Start();

        using var collector = new BoundedPrometheusCollector(
            new PrometheusCollectorOptions
            {
                AllowedMeterPrefixes =
                [
                    ServerOperationsMetrics.MeterName
                ],
                MaximumSnapshotBytes = 16_384
            });
        var state = new ServerOperationalState(
            ServerReadinessDependency.None);
        var tasks = new CriticalTaskSupervisor(state, static () => { });
        var providerCalls = 0;
        using var metrics = new ServerOperationsMetrics(
            state,
            tasks,
            managementObserver: null,
            () =>
            {
                providerCalls++;
                return DateTimeOffset.FromUnixTimeMilliseconds(
                    ExpectedProcessStartUnixMilliseconds);
            });

        listener.RecordObservableInstruments();
        var first = collector.CollectSnapshot();
        listener.RecordObservableInstruments();
        var second = collector.CollectSnapshot();

        Check.True(
            publishedInstrument is ObservableGauge<double>,
            "process start is an observable double gauge");
        Check.True(
            publishedInstrument!.Unit == "s",
            "process start uses the seconds unit");
        Check.Equal(
            1,
            providerCalls,
            "process start is captured once per metrics instance");
        Check.True(
            measurements.SequenceEqual(
            [
                ExpectedProcessStartUnixSeconds,
                ExpectedProcessStartUnixSeconds
            ]),
            "process start remains stable across observations");
        Check.True(
            measurements.All(static measurement =>
                measurement != Math.Truncate(measurement)),
            "process start retains its fractional milliseconds");
        Check.True(
            ContainsExport(first) && ContainsExport(second),
            "Prometheus snapshots expose the stable Unix-seconds value");
        Check.True(
            first.Contains(
                $"# TYPE {ExportedProcessStartMetricName} gauge\n",
                StringComparison.Ordinal),
            "Prometheus identifies process start as a gauge");
    }

    private static bool ContainsExport(string snapshot) =>
        snapshot.Contains(
            $"{ExportedProcessStartMetricName} " +
            $"{ExpectedExportedProcessStartUnixSeconds}\n",
            StringComparison.Ordinal);
}
