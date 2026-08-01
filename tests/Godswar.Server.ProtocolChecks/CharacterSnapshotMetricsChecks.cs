using System.Diagnostics.Metrics;
using Godswar.Server.Application.Characters;

namespace Godswar.Server.ProtocolChecks;

internal static class CharacterSnapshotMetricsChecks
{
    public static async Task RunAsync()
    {
        var counters = new List<Measurement>();
        var durations = new List<Measurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name ==
                CharacterSnapshotMetrics.MeterName)
            {
                current.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>(
            (instrument, _, tags, _) =>
            {
                if (instrument.Name ==
                    "godswar_character_snapshot_queries_total")
                {
                    lock (counters)
                    {
                        counters.Add(Capture(tags));
                    }
                }
            });
        listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) =>
            {
                if (instrument.Name ==
                    "godswar_character_snapshot_query_duration_ms")
                {
                    lock (durations)
                    {
                        durations.Add(Capture(tags, value));
                    }
                }
            });
        listener.Start();

        var loaded = CharacterSnapshotContractChecks.CreateValidSnapshot();
        var successful = new MeasuredCharacterSnapshotReader(
            new FixedReader(loaded));
        _ = await successful.ReadAsync(loaded.AccountId);

        var failing = new MeasuredCharacterSnapshotReader(
            new FailingReader());
        try
        {
            _ = await failing.ReadAsync(loaded.AccountId);
            throw new InvalidOperationException(
                "Expected the measured reader to preserve its failure.");
        }
        catch (CharacterSnapshotUnavailableException ex)
        {
            Check.Equal(
                (int)CharacterSnapshotFailureReason.ProviderUnavailable,
                (int)ex.Reason,
                "measured reader preserves the typed failure");
        }

        Check.True(
            counters.Any(measurement =>
                measurement.Provider == "postgresql" &&
                measurement.Outcome == "loaded"),
            "snapshot metrics expose a bounded PostgreSQL loaded outcome");
        Check.True(
            counters.Any(measurement =>
                measurement.Provider == "postgresql" &&
                measurement.Outcome == "provider_unavailable"),
            "snapshot metrics expose a bounded PostgreSQL failure outcome");
        Check.True(
            durations.Count >= 2 &&
            durations.All(static measurement =>
                measurement.Value >= 0),
            "snapshot metrics expose non-negative query duration");
        Check.True(
            counters.All(static measurement =>
                measurement.Tags.Count == 2),
            "snapshot metrics contain no player or session labels");
    }

    private static Measurement Capture(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        double value = 0)
    {
        var captured = tags.ToArray();
        return new Measurement(
            captured.Single(tag => tag.Key == "provider")
                .Value?.ToString() ?? string.Empty,
            captured.Single(tag => tag.Key == "outcome")
                .Value?.ToString() ?? string.Empty,
            value,
            captured);
    }

    private sealed record Measurement(
        string Provider,
        string Outcome,
        double Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags);

    private sealed class FixedReader(
        CharacterAccountSnapshot snapshot) : ICharacterSnapshotReader
    {
        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }

    private sealed class FailingReader : ICharacterSnapshotReader
    {
        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default) =>
            Task.FromException<CharacterAccountSnapshot>(
                new CharacterSnapshotUnavailableException(
                    CharacterSnapshotFailureReason.ProviderUnavailable,
                    "Synthetic provider failure."));
    }
}
