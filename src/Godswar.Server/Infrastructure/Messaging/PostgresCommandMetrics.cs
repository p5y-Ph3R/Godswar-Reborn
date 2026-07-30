using System.Diagnostics;
using System.Diagnostics.Metrics;
using Godswar.Server.Operations.Observability;

namespace Godswar.Server.Infrastructure.Messaging;

internal static class PostgresCommandMetrics
{
    public const string MeterName =
        "Godswar.Server.Infrastructure.PostgresCommands";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> InboxTransactions =
        Meter.CreateCounter<long>(
            "godswar_command_inbox_transactions_total",
            description:
            "Durable command transaction outcomes by bounded family and outcome.");
    private static readonly Histogram<double> InboxDuration =
        Meter.CreateHistogram<double>(
            "godswar_command_inbox_transaction_duration_ms",
            unit: "ms",
            description:
            "Duration of durable command inbox transactions.");
    private static readonly Counter<long> OutboxEvents =
        Meter.CreateCounter<long>(
            "godswar_outbox_events_total",
            description:
            "Outbox processing outcomes by bounded consumer and outcome.");
    private static readonly Counter<long> OutboxRetries =
        Meter.CreateCounter<long>(
            "godswar_outbox_retries_total",
            description:
            "Outbox retries by bounded consumer and reason.");
    private static readonly Counter<long> OutboxPoison =
        Meter.CreateCounter<long>(
            "godswar_outbox_poison_total",
            description:
            "Outbox events quarantined after bounded delivery attempts.");
    private static readonly Counter<long> OutboxGaps =
        Meter.CreateCounter<long>(
            "godswar_outbox_sequence_gaps_total",
            description:
            "Strict-order outbox sequence gaps by bounded consumer.");
    private static readonly Histogram<double> DispatchDuration =
        Meter.CreateHistogram<double>(
            "godswar_outbox_dispatch_duration_ms",
            unit: "ms",
            description:
            "Duration of one bounded outbox dispatch pass.");

    private static long _backlogCount;
    private static long _oldestAgeMilliseconds;
    private static long _outboxHeartbeatTimestamp;
    private static int _outboxState;

    static PostgresCommandMetrics()
    {
        Meter.CreateObservableGauge(
            "godswar_outbox_backlog",
            () => Interlocked.Read(ref _backlogCount),
            unit: "{event}",
            description: "Pending, non-poisoned outbox events.");
        Meter.CreateObservableGauge(
            "godswar_outbox_oldest_age_seconds",
            () => Interlocked.Read(ref _oldestAgeMilliseconds) / 1000d,
            unit: "s",
            description: "Age of the oldest pending outbox event.");
        Meter.CreateObservableGauge(
            "godswar_outbox_dispatcher_state",
            () => Volatile.Read(ref _outboxState),
            description:
            "Dispatcher state: 0 not-started, 1 running, 2 stopped, 3 faulted.");
        Meter.CreateObservableGauge(
            "godswar_outbox_heartbeat_age_seconds",
            () => GetSnapshot().HeartbeatAge.TotalSeconds,
            unit: "s",
            description: "Age of the latest outbox dispatcher pass.");
    }

    public static void RecordInbox(
        string family,
        string outcome,
        TimeSpan duration)
    {
        var tags = new TagList
        {
            { "family", family },
            { "outcome", outcome }
        };
        InboxTransactions.Add(1, tags);
        InboxDuration.Record(duration.TotalMilliseconds, tags);
        if (SafeTelemetryCode.IsSafe(family))
        {
            ServerActivity.RecordCompleted(
                ServerTraceOperation.PostgresTransaction,
                duration,
                TraceOutcome(outcome),
                ActivityKind.Client,
                ServerTraceAttribute.FromCode(
                    ServerTraceTag.CommandFamily,
                    family),
                ServerTraceAttribute.FromCode(
                    ServerTraceTag.Stage,
                    "inbox"));
        }
    }

    public static void RecordOutbox(
        string consumer,
        string outcome)
    {
        var tags = new TagList
        {
            { "consumer", consumer },
            { "outcome", outcome }
        };
        OutboxEvents.Add(1, tags);
    }

    public static void RecordRetry(
        string consumer,
        string reason)
    {
        var tags = new TagList
        {
            { "consumer", consumer },
            { "reason", reason }
        };
        OutboxRetries.Add(1, tags);
    }

    public static void RecordPoison(
        string consumer,
        string reason)
    {
        var tags = new TagList
        {
            { "consumer", consumer },
            { "reason", reason }
        };
        OutboxPoison.Add(1, tags);
    }

    public static void RecordGap(string consumer)
    {
        OutboxGaps.Add(
            1,
            new TagList { { "consumer", consumer } });
    }

    public static void RecordDispatchDuration(TimeSpan duration)
    {
        DispatchDuration.Record(duration.TotalMilliseconds);
    }

    public static void UpdateBacklog(
        long count,
        TimeSpan oldestAge)
    {
        Interlocked.Exchange(ref _backlogCount, Math.Max(0, count));
        Interlocked.Exchange(
            ref _oldestAgeMilliseconds,
            Math.Max(0, (long)oldestAge.TotalMilliseconds));
    }

    public static void MarkOutboxStarted()
    {
        Volatile.Write(ref _outboxState, (int)OutboxDispatcherState.Running);
        TouchOutboxHeartbeat();
    }

    public static void MarkOutboxPassCompleted()
    {
        TouchOutboxHeartbeat();
    }

    public static void MarkOutboxStopped()
    {
        Volatile.Write(ref _outboxState, (int)OutboxDispatcherState.Stopped);
        TouchOutboxHeartbeat();
    }

    public static void MarkOutboxFaulted()
    {
        Volatile.Write(ref _outboxState, (int)OutboxDispatcherState.Faulted);
    }

    public static PostgresOutboxRuntimeSnapshot GetSnapshot()
    {
        var heartbeat = Volatile.Read(ref _outboxHeartbeatTimestamp);
        var age = heartbeat <= 0
            ? TimeSpan.MaxValue
            : Stopwatch.GetElapsedTime(heartbeat);
        return new PostgresOutboxRuntimeSnapshot(
            (OutboxDispatcherState)Volatile.Read(ref _outboxState),
            Interlocked.Read(ref _backlogCount),
            TimeSpan.FromMilliseconds(
                Interlocked.Read(ref _oldestAgeMilliseconds)),
            age);
    }

    private static void TouchOutboxHeartbeat()
    {
        Volatile.Write(
            ref _outboxHeartbeatTimestamp,
            Stopwatch.GetTimestamp());
    }

    private static ServerTraceOutcome TraceOutcome(string outcome) =>
        outcome switch
        {
            "committed" or "accepted" or "success" =>
                ServerTraceOutcome.Accepted,
            "duplicate" or "replayed" =>
                ServerTraceOutcome.Duplicate,
            "cancelled" => ServerTraceOutcome.Cancelled,
            "provider_unavailable" =>
                ServerTraceOutcome.Faulted,
            _ => ServerTraceOutcome.Rejected
        };
}

internal enum OutboxDispatcherState : byte
{
    NotStarted = 0,
    Running = 1,
    Stopped = 2,
    Faulted = 3,
}

internal readonly record struct PostgresOutboxRuntimeSnapshot(
    OutboxDispatcherState State,
    long BacklogCount,
    TimeSpan OldestAge,
    TimeSpan HeartbeatAge);
