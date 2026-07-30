using System.Diagnostics;
using System.Diagnostics.Metrics;
using Godswar.Server.Operations.Observability;

namespace Godswar.Server.Application.Characters;

internal sealed class CharacterCheckpointMetrics : IDisposable
{
    public const string MeterName =
        "Godswar.Server.Application.CharacterCheckpoints";

    private const string FacetTagName = "checkpoint.facet";
    private const string OutcomeTagName = "checkpoint.outcome";

    private readonly Counter<long> _enqueueOutcomes;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _writeOutcomes;
    private readonly Histogram<double> _writeDuration;
    private readonly Func<CharacterCheckpointRuntimeSnapshot> _snapshot;
    private readonly Meter _meter = new(MeterName);
    private int _disposed;

    public CharacterCheckpointMetrics(
        Func<CharacterCheckpointRuntimeSnapshot> snapshot)
    {
        _snapshot = snapshot ??
            throw new ArgumentNullException(nameof(snapshot));
        _enqueueOutcomes = _meter.CreateCounter<long>(
            "godswar.server.checkpoints.enqueue.total",
            "{checkpoint}",
            "Checkpoint enqueue outcomes by finite facet and outcome.");
        _writeOutcomes = _meter.CreateCounter<long>(
            "godswar.server.checkpoints.write.total",
            "{checkpoint}",
            "Checkpoint persistence outcomes by finite facet and outcome.");
        _retries = _meter.CreateCounter<long>(
            "godswar.server.checkpoints.retry.total",
            "{checkpoint}",
            "Transient checkpoint persistence retries.");
        _writeDuration = _meter.CreateHistogram<double>(
            "godswar.server.checkpoints.write.duration",
            "ms",
            "Elapsed checkpoint store operation time.");

        _meter.CreateObservableGauge(
            "godswar.server.checkpoints.queue.depth",
            () => _snapshot().PendingKeys,
            "{checkpoint}",
            "Distinct pending checkpoint keys.");
        _meter.CreateObservableGauge(
            "godswar.server.checkpoints.writes.active",
            () => _snapshot().ActiveWrites,
            "{checkpoint}",
            "Active bounded checkpoint writes.");
        _meter.CreateObservableGauge(
            "godswar.server.checkpoints.retries.scheduled",
            () => _snapshot().ScheduledRetries,
            "{checkpoint}",
            "Checkpoint keys waiting for a bounded retry.");
        _meter.CreateObservableGauge(
            "godswar.server.checkpoints.ready",
            () => _snapshot().IsReady ? 1 : 0,
            "{state}",
            "Whether the checkpoint coordinator accepts ordinary work.");
        _meter.CreateObservableGauge(
            "godswar.server.checkpoints.dirty.age",
            () => _snapshot().OldestPendingAge.TotalSeconds,
            "s",
            "Age of the oldest pending checkpoint.");
        _meter.CreateObservableGauge(
            "godswar.server.checkpoints.heartbeat.age",
            () => _snapshot().HeartbeatAge.TotalSeconds,
            "s",
            "Age of the process-wide checkpoint worker heartbeat.");
    }

    public void RecordEnqueue(
        CharacterCheckpointFacet facet,
        CharacterCheckpointEnqueueStatus status)
    {
        _enqueueOutcomes.Add(
            1,
            Tags(facet, status.ToMetricTag()));
    }

    public void RecordWrite(
        CharacterCheckpointFacet facet,
        CharacterCheckpointWriteStatus status,
        TimeSpan duration)
    {
        var tags = Tags(facet, status.ToMetricTag());
        _writeOutcomes.Add(1, tags);
        _writeDuration.Record(duration.TotalMilliseconds, tags);
        ServerActivity.RecordCompleted(
            ServerTraceOperation.CheckpointWrite,
            duration,
            TraceOutcome(status),
            ActivityKind.Client,
            ServerTraceAttribute.FromCode(
                ServerTraceTag.Component,
                facet.ToMetricTag()),
            ServerTraceAttribute.FromCode(
                ServerTraceTag.Reason,
                status.ToMetricTag()));
    }

    public void RecordFailure(
        CharacterCheckpointFacet facet,
        string outcome,
        TimeSpan duration)
    {
        var tags = Tags(facet, outcome);
        _writeOutcomes.Add(1, tags);
        _writeDuration.Record(duration.TotalMilliseconds, tags);
        ServerActivity.RecordCompleted(
            ServerTraceOperation.CheckpointWrite,
            duration,
            ServerTraceOutcome.Faulted,
            ActivityKind.Client,
            ServerTraceAttribute.FromCode(
                ServerTraceTag.Component,
                facet.ToMetricTag()),
            ServerTraceAttribute.FromCode(
                ServerTraceTag.Reason,
                "failure"));
    }

    public void RecordRetry(CharacterCheckpointFacet facet)
    {
        _retries.Add(
            1,
            new KeyValuePair<string, object?>(
                FacetTagName,
                facet.ToMetricTag()));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _meter.Dispose();
        }
    }

    private static TagList Tags(
        CharacterCheckpointFacet facet,
        string outcome) =>
        new()
        {
            { FacetTagName, facet.ToMetricTag() },
            { OutcomeTagName, outcome }
        };

    private static ServerTraceOutcome TraceOutcome(
        CharacterCheckpointWriteStatus status) =>
        status switch
        {
            CharacterCheckpointWriteStatus.Applied =>
                ServerTraceOutcome.Accepted,
            CharacterCheckpointWriteStatus.AlreadyApplied or
            CharacterCheckpointWriteStatus.Superseded =>
                ServerTraceOutcome.Duplicate,
            _ => ServerTraceOutcome.Rejected
        };
}

internal static class CharacterCheckpointMetricTags
{
    public static string ToMetricTag(
        this CharacterCheckpointFacet facet) =>
        facet switch
        {
            CharacterCheckpointFacet.Position => "position",
            CharacterCheckpointFacet.Vitals => "vitals",
            _ => throw new ArgumentOutOfRangeException(nameof(facet))
        };

    public static string ToMetricTag(
        this CharacterCheckpointEnqueueStatus status) =>
        status switch
        {
            CharacterCheckpointEnqueueStatus.Accepted => "accepted",
            CharacterCheckpointEnqueueStatus.Coalesced => "coalesced",
            CharacterCheckpointEnqueueStatus.IgnoredStale =>
                "ignored_stale",
            CharacterCheckpointEnqueueStatus.RevisionConflict =>
                "revision_conflict",
            CharacterCheckpointEnqueueStatus.OwnershipLost =>
                "ownership_lost",
            CharacterCheckpointEnqueueStatus.Saturated => "saturated",
            CharacterCheckpointEnqueueStatus.NotReady => "not_ready",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    public static string ToMetricTag(
        this CharacterCheckpointWriteStatus status) =>
        status switch
        {
            CharacterCheckpointWriteStatus.Applied => "applied",
            CharacterCheckpointWriteStatus.AlreadyApplied =>
                "already_applied",
            CharacterCheckpointWriteStatus.Superseded => "superseded",
            CharacterCheckpointWriteStatus.RevisionConflict =>
                "revision_conflict",
            CharacterCheckpointWriteStatus.OwnershipLost =>
                "ownership_lost",
            CharacterCheckpointWriteStatus.CharacterNotFound =>
                "character_not_found",
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
}
