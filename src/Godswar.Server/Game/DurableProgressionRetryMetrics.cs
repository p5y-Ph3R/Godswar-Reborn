using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Godswar.Server.Game;

internal sealed class DurableProgressionRetryMetrics : IDisposable
{
    public const string MeterName =
        "Godswar.Server.ProgressionRetry";

    private readonly Meter _meter = new(MeterName);
    private readonly object _snapshotGate = new();
    private readonly Func<DurableProgressionRetryRuntimeSnapshot>
        _snapshot;
    private DurableProgressionRetryRuntimeSnapshot _cachedSnapshot;
    private long _cachedAt;

    public DurableProgressionRetryMetrics(
        Func<DurableProgressionRetryRuntimeSnapshot> snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(
            nameof(snapshot));
        _meter.CreateObservableGauge(
            "godswar_progression_retry_queue_depth",
            () => ReadSnapshot().QueueDepth,
            unit: "{interval}",
            description:
            "Process-local progression intervals awaiting durable settlement.");
        _meter.CreateObservableGauge(
            "godswar_progression_retry_oldest_age_seconds",
            () => ReadSnapshot().OldestAge.TotalSeconds,
            unit: "s",
            description:
            "Age of the oldest deferred progression interval.");
        _meter.CreateObservableGauge(
            "godswar_progression_retry_heartbeat_age_seconds",
            ObserveHeartbeatAge,
            unit: "s",
            description:
            "Age of the progression retry worker heartbeat.");
        _meter.CreateObservableGauge(
            "godswar_progression_retry_worker_state",
            () => (int)ReadSnapshot().State,
            description:
            "Retry worker state: 0 not-started, 1 running, 2 stopped, 3 faulted.");
    }

    public void Dispose() => _meter.Dispose();

    private double ObserveHeartbeatAge()
    {
        var snapshot = ReadSnapshot();
        return snapshot.HeartbeatAge == TimeSpan.MaxValue
            ? -1d
            : snapshot.HeartbeatAge.TotalSeconds;
    }

    private DurableProgressionRetryRuntimeSnapshot ReadSnapshot()
    {
        var now = Stopwatch.GetTimestamp();
        lock (_snapshotGate)
        {
            if (_cachedAt > 0 &&
                Stopwatch.GetElapsedTime(_cachedAt, now) <
                    TimeSpan.FromMilliseconds(100))
            {
                return _cachedSnapshot;
            }

            _cachedSnapshot = _snapshot();
            _cachedAt = now;
            return _cachedSnapshot;
        }
    }
}
