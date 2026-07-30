using System.Diagnostics;
using Godswar.Server.Operations.Observability;

namespace Godswar.Server.ProtocolChecks;

internal static class B13ServerActivityChecks
{
    public static Task RunAsync()
    {
        CheckRetrospectiveDurationAndSafeTags();
        CheckAcceptedPacketSamplingAndFaultCapture();
        CheckCircularCapacity();
        CheckInvalidInputs();
        return Task.CompletedTask;
    }

    private static void CheckRetrospectiveDurationAndSafeTags()
    {
        using var buffer = new BoundedTraceBuffer(
            new BoundedTraceBufferOptions { Capacity = 8 });
        Check.True(
            ServerActivity.RecordCompleted(
                ServerTraceOperation.PostgresTransaction,
                TimeSpan.FromMilliseconds(125),
                ServerTraceOutcome.Accepted,
                ActivityKind.Internal,
                ServerTraceAttribute.FromCode(
                    ServerTraceTag.Component,
                    "command_inbox"),
                ServerTraceAttribute.FromCode(
                    ServerTraceTag.Stage,
                    "commit")),
            "retrospective database activity is captured");

        var span = buffer.Snapshot().Single();
        Check.Equal(
            "postgres.transaction",
            span.Operation,
            "retrospective trace operation");
        Check.True(
            Math.Abs(
                span.Duration.TotalMilliseconds - 125d) < 1d,
            "retrospective activity preserves real elapsed time");
        Check.Equal(32, span.TraceId.Length, "opaque trace ID length");
        Check.Equal(16, span.SpanId.Length, "opaque span ID length");
        Check.True(
            span.Tags.Any(tag =>
                tag.Name == "outcome" &&
                tag.Value == "accepted"),
            "finite outcome is captured");
        Check.True(
            span.Tags.All(tag =>
                !tag.Name.Contains(
                    "account",
                    StringComparison.Ordinal) &&
                !tag.Name.Contains(
                    "player",
                    StringComparison.Ordinal)),
            "captured trace tags cannot contain player identity");

        var startedAt = TimeProvider.System.GetUtcNow() -
            TimeSpan.FromMilliseconds(50);
        using (var activity = ServerActivity.StartAt(
                   ServerTraceOperation.OutboxDispatch,
                   startedAt,
                   ActivityKind.Internal,
                   ServerTraceAttribute.FromCode(
                       ServerTraceTag.Component,
                       "outbox")))
        {
            Check.True(
                activity is not null,
                "bounded past start timestamp is accepted");
            ServerActivity.Complete(
                activity,
                ServerTraceOutcome.Accepted);
        }
        Check.Equal(
            2,
            buffer.Snapshot().Length,
            "StartAt activity reaches the bounded buffer");
    }

    private static void CheckAcceptedPacketSamplingAndFaultCapture()
    {
        using var buffer = new BoundedTraceBuffer(
            new BoundedTraceBufferOptions { Capacity = 128 });

        // The packet sequence is process-global and intentionally opaque.
        // Every consecutive window of 64 sequence values contains exactly
        // one value selected by the deterministic 1/64 mask, regardless of
        // where an earlier check left the sequence.
        for (var index = 0; index < 64; index++)
        {
            using var packet = ServerActivity.StartPacket(
                ServerTraceOperation.LoginPacket,
                "login",
                "tls");
            packet.Complete(ServerTraceOutcome.Accepted);
        }

        var accepted = buffer.Snapshot();
        Check.Equal(
            1,
            accepted.Length,
            "exactly one accepted packet is retained per 64 packets");
        Check.Equal(
            "login.packet",
            accepted[0].Operation,
            "accepted packet sampling retains the packet operation");
        Check.True(
            accepted[0].Tags.Any(tag =>
                tag.Name == "outcome" &&
                tag.Value == "accepted"),
            "sampled accepted packet retains its finite outcome");

        // A second complete sequence window contains 63 packets that were
        // not selected for normal sampling. Every fault must nevertheless be
        // materialized by ServerPacketTraceScope.Complete.
        for (var index = 0; index < 64; index++)
        {
            using var packet = ServerActivity.StartPacket(
                ServerTraceOperation.GamePacket,
                "game",
                "raw_tcp");
            packet.Complete(ServerTraceOutcome.Faulted);
        }

        var all = buffer.Snapshot();
        var faults = all
            .Where(span => span.Operation == "game.packet")
            .ToArray();
        Check.Equal(
            64,
            faults.Length,
            "sampled and unsampled packet faults are all retained");
        Check.True(
            faults.All(span =>
                span.Status == "error" &&
                span.Tags.Any(tag =>
                    tag.Name == "outcome" &&
                    tag.Value == "faulted") &&
                span.Tags.Any(tag =>
                    tag.Name == "endpoint" &&
                    tag.Value == "game") &&
                span.Tags.Any(tag =>
                    tag.Name == "transport" &&
                    tag.Value == "raw_tcp")),
            "unsampled packet fault capture preserves finite diagnostics");

        var runtime = buffer.GetRuntimeSnapshot();
        Check.Equal(
            65L,
            runtime.Captured,
            "packet trace sampling and fault capture counts");
        Check.Equal(
            0L,
            runtime.Overwritten,
            "packet trace check remains within its fixed ring capacity");
    }

    private static void CheckCircularCapacity()
    {
        using var buffer = new BoundedTraceBuffer(
            new BoundedTraceBufferOptions { Capacity = 2 });
        RecordStage("first");
        RecordStage("second");
        RecordStage("third");

        var spans = buffer.Snapshot();
        var runtime = buffer.GetRuntimeSnapshot();
        Check.Equal(2, spans.Length, "trace buffer live capacity");
        Check.Equal(3L, runtime.Captured, "all spans are counted");
        Check.Equal(1L, runtime.Overwritten, "oldest span overwrite count");
        Check.True(
            spans[0].Tags.Any(tag => tag.Value == "second") &&
            spans[1].Tags.Any(tag => tag.Value == "third"),
            "circular buffer retains the newest spans");
    }

    private static void CheckInvalidInputs()
    {
        using var buffer = new BoundedTraceBuffer();
        Check.True(
            !ServerActivity.RecordCompleted(
                ServerTraceOperation.PostgresTransaction,
                TimeSpan.FromMilliseconds(-1),
                ServerTraceOutcome.Faulted),
            "negative retrospective duration is safely ignored");
        Check.True(
            !ServerActivity.RecordCompleted(
                ServerTraceOperation.PostgresTransaction,
                ServerActivity.MaximumRetrospectiveDuration +
                TimeSpan.FromMilliseconds(1),
                ServerTraceOutcome.Faulted),
            "unbounded retrospective duration is safely ignored");
        Check.Equal(
            0,
            buffer.Snapshot().Length,
            "invalid retrospective durations create no spans");

        Check.Throws<ArgumentException>(
            () => ServerTraceAttribute.FromCode(
                ServerTraceTag.Component,
                "Player Alice"),
            "trace code rejects arbitrary identity text");
        Check.Throws<ArgumentException>(
            () =>
            {
                using var activity = ServerActivity.Start(
                    ServerTraceOperation.ApplicationCommand,
                    ActivityKind.Internal,
                    ServerTraceAttribute.FromCode(
                        ServerTraceTag.Stage,
                        "one"),
                    ServerTraceAttribute.FromCode(
                        ServerTraceTag.Stage,
                        "two"));
            },
            "duplicate trace tags are rejected");
    }

    private static void RecordStage(string stage)
    {
        using var activity = ServerActivity.Start(
            ServerTraceOperation.ApplicationCommand,
            ActivityKind.Internal,
            ServerTraceAttribute.FromCode(
                ServerTraceTag.Stage,
                stage));
        ServerActivity.Complete(
            activity,
            ServerTraceOutcome.Accepted);
    }
}
