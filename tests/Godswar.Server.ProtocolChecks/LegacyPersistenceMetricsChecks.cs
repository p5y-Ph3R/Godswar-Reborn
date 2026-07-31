using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Godswar.Server.Operations.Observability;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class LegacyPersistenceMetricsChecks
{
    public const string CheckName =
        "B20 legacy persistence metric cardinality";

    public static Task RunAsync()
    {
        CheckFiniteOperationDimensionAndReadySignal();
        CheckBoundedPrometheusExport();
        CheckUnknownOperationFailsClosed();
        return Task.CompletedTask;
    }

    private static void CheckFiniteOperationDimensionAndReadySignal()
    {
        // Publish before listener startup to prove that an operational scraper
        // can discover an eagerly initialized observer with zero legacy use.
        LegacyPersistenceMetrics.EnsureInitialized();

        var invocations =
            new ConcurrentQueue<CapturedMeasurement>();
        var ready = new ConcurrentQueue<int>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name ==
                LegacyPersistenceMetrics.MeterName)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, measurement, tags, _) =>
            {
                if (instrument.Name ==
                    LegacyPersistenceMetrics.InvocationInstrumentName)
                {
                    invocations.Enqueue(
                        new CapturedMeasurement(
                            measurement,
                            tags.ToArray()));
                }
            });
        listener.SetMeasurementEventCallback<int>(
            (instrument, measurement, _, _) =>
            {
                if (instrument.Name ==
                    LegacyPersistenceMetrics.ObserverReadyInstrumentName)
                {
                    ready.Enqueue(measurement);
                }
            });
        listener.Start();

        var operations = Enum.GetValues<LegacyPersistenceOperation>();
        foreach (var operation in operations)
        {
            LegacyPersistenceMetrics.Record(operation);
        }
        listener.RecordObservableInstruments();

        Check.Equal(
            operations.Length,
            invocations.Count,
            "one invocation measurement per finite legacy operation");
        Check.True(
            ready.Count == 1 && ready.Single() == 1,
            "observer-ready gauge is present without a legacy call");

        var expectedCodes = operations
            .Select(LegacyPersistenceMetrics.ToMetricTag)
            .ToHashSet(StringComparer.Ordinal);
        var actualCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var measurement in invocations)
        {
            Check.Equal(
                1L,
                measurement.Value,
                "legacy invocation counter increment");
            Check.True(
                measurement.Tags.Length == 1 &&
                measurement.Tags[0].Key ==
                    LegacyPersistenceMetrics.OperationTagName,
                "legacy metric has only its finite operation label");
            var code = measurement.Tags[0].Value?.ToString() ??
                string.Empty;
            Check.True(
                SafeTelemetryCode.IsSafe(code) &&
                code.Length <= SafeTelemetryCode.MaximumLength,
                "legacy operation code is bounded safe telemetry");
            Check.True(
                actualCodes.Add(code),
                "legacy operation codes are unique");
        }

        Check.True(
            actualCodes.SetEquals(expectedCodes),
            "captured labels equal the closed operation catalog");
    }

    private static void CheckBoundedPrometheusExport()
    {
        using var collector = new BoundedPrometheusCollector(
            new PrometheusCollectorOptions
            {
                AllowedMeterPrefixes =
                    [LegacyPersistenceMetrics.MeterName],
                MaximumInstruments = 2,
                MaximumSeries = 64,
                MaximumTagsPerSeries = 1,
                MaximumSnapshotBytes = 65_536
            });

        LegacyPersistenceMetrics.Record(
            LegacyPersistenceOperation.EnsureSeedData);
        var output = collector.CollectSnapshot();

        Check.True(
            output.Contains(
                LegacyPersistenceMetrics.InvocationInstrumentName +
                "{operation=\"ensure_seed_data\"} 1\n",
                StringComparison.Ordinal),
            "bounded exporter accepts the finite invocation series");
        Check.True(
            output.Contains(
                LegacyPersistenceMetrics.ObserverReadyInstrumentName +
                " 1\n",
                StringComparison.Ordinal),
            "bounded exporter exposes zero-use observer readiness");
        var snapshot = collector.GetSnapshot();
        Check.Equal(2, snapshot.Instruments, "legacy metric instruments");
        Check.Equal(2, snapshot.Series, "legacy metric series");
        Check.Equal(0L, snapshot.DroppedTags, "legacy metric dropped tags");
        Check.Equal(
            0L,
            snapshot.DroppedMeasurements,
            "legacy metric dropped measurements");
    }

    private static void CheckUnknownOperationFailsClosed()
    {
        Check.Throws<ArgumentOutOfRangeException>(
            () => LegacyPersistenceMetrics.Record(
                (LegacyPersistenceOperation)byte.MaxValue),
            "unknown legacy operation cannot create a metric series");
    }

    private readonly record struct CapturedMeasurement(
        long Value,
        KeyValuePair<string, object?>[] Tags);
}
