using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Godswar.Server.Operations.Observability;

namespace Godswar.Server.ProtocolChecks;

internal static class B13StructuredLoggingChecks
{
    public static Task RunAsync()
    {
        CheckStructuredJson();
        CheckLegacyRedactionAndBounds();
        CheckConsoleBoundaryOwnership();
        CheckRateAndSizeLimits();
        CheckSinkFailureIsolation();
        CheckSlowSinkDoesNotBlockProducers();
        CheckShutdownIsBoundedAndAccounted();
        return Task.CompletedTask;
    }

    private static void CheckStructuredJson()
    {
        using var sink = new StringWriter();
        using var logger = new BoundedStructuredLogger(sink);
        Check.True(
            logger.TryWrite(
                OperationalLogEvent.ServerLifecycle,
                OperationalLogLevel.Information,
                OperationalLogValue.FromCode(
                    OperationalLogField.Component,
                    "game_server"),
                OperationalLogValue.FromCode(
                    OperationalLogField.State,
                    "started")),
            "a valid structured event is accepted");

        using var document = JsonDocument.Parse(
            OnlyLine(logger, sink));
        var root = document.RootElement;
        Check.Equal(
            "server_lifecycle",
            root.GetProperty("event").GetString()!,
            "structured event code");
        Check.Equal(
            "game_server",
            root.GetProperty("component").GetString()!,
            "structured component code");
        Check.True(
            root.TryGetProperty("timestamp_utc", out _),
            "structured event has a timestamp");
        Check.True(
            !root.TryGetProperty("message", out _),
            "structured event has no arbitrary message field");

        Check.Throws<ArgumentException>(
            () => OperationalLogValue.FromCode(
                OperationalLogField.Component,
                "Player Alice"),
            "unbounded telemetry text is rejected");
        Check.Throws<ArgumentException>(
            () => logger.TryWrite(
                OperationalLogEvent.ServerLifecycle,
                OperationalLogLevel.Information,
                OperationalLogValue.FromCode(
                    OperationalLogField.State,
                    "started"),
                OperationalLogValue.FromCode(
                    OperationalLogField.State,
                    "ready")),
            "duplicate structured fields are rejected");
    }

    private static void CheckLegacyRedactionAndBounds()
    {
        const string sensitive =
            "[db] username=Alice password=hunter2 " +
            "endpoint=198.51.100.7 token=DEADBEEF";
        using var sink = new StringWriter();
        using var logger = new BoundedStructuredLogger(sink);
        using (var writer = new BoundedLegacyConsoleWriter(
                   logger,
                   LegacyConsoleSource.Stderr,
                   64))
        {
            writer.WriteLine(sensitive);
        }

        Check.True(
            logger.WaitUntilIdle(TimeSpan.FromSeconds(2)),
            "legacy diagnostic reaches the writer");
        var output = sink.ToString();
        Check.True(
            !output.Contains("Alice", StringComparison.OrdinalIgnoreCase) &&
            !output.Contains("hunter2", StringComparison.Ordinal) &&
            !output.Contains("198.51.100.7", StringComparison.Ordinal) &&
            !output.Contains("DEADBEEF", StringComparison.Ordinal),
            "legacy diagnostics never expose raw content");
        using (var document = JsonDocument.Parse(
                   OnlyLine(logger, sink)))
        {
            var root = document.RootElement;
            Check.Equal(
                "legacy_diagnostic_suppressed",
                root.GetProperty("event").GetString()!,
                "legacy line is represented by a safe event");
            Check.Equal(
                "database",
                root.GetProperty("category").GetString()!,
                "legacy diagnostic finite classification");
            Check.Equal(
                (long)sensitive.Length,
                root.GetProperty("count").GetInt64(),
                "legacy diagnostic character count");
        }

        sink.GetStringBuilder().Clear();
        using (var writer = new BoundedLegacyConsoleWriter(
                   logger,
                   LegacyConsoleSource.Stdout,
                   64))
        {
            writer.Write(new string('x', 4_096));
        }
        using var oversized = JsonDocument.Parse(
            OnlyLine(logger, sink));
        Check.Equal(
            4_096L,
            oversized.RootElement.GetProperty("count").GetInt64(),
            "oversized legacy write reports a bounded count");
        Check.True(
            oversized.RootElement.GetProperty("truncated").GetBoolean(),
            "oversized legacy write is marked truncated");
        Check.True(
            sink.ToString().Length < 512,
            "oversized legacy content produces bounded output");
    }

    private static void CheckRateAndSizeLimits()
    {
        using var sink = new StringWriter();
        using var logger = new BoundedStructuredLogger(
            sink,
            new StructuredLogOptions
            {
                GlobalEventsPerWindow = 2,
                EventsPerKindPerWindow = 2
            });
        Check.True(
            logger.TryWrite(
                OperationalLogEvent.ReadinessChanged,
                OperationalLogLevel.Information),
            "first event enters the fixed rate window");
        Check.True(
            logger.TryWrite(
                OperationalLogEvent.ReadinessChanged,
                OperationalLogLevel.Information),
            "second event enters the fixed rate window");
        Check.True(
            !logger.TryWrite(
                OperationalLogEvent.ReadinessChanged,
                OperationalLogLevel.Information),
            "event over the fixed rate bound is rejected");
        Check.Equal(
            1L,
            logger.GetSnapshot().RateLimited,
            "rate-limit rejection is counted");

        using var smallSink = new StringWriter();
        using var smallLogger = new BoundedStructuredLogger(
            smallSink,
            new StructuredLogOptions { MaximumLineBytes = 256 });
        var code = new string('a', SafeTelemetryCode.MaximumLength);
        Check.True(
            !smallLogger.TryWrite(
                OperationalLogEvent.TelemetryExporter,
                OperationalLogLevel.Warning,
                OperationalLogValue.FromCode(
                    OperationalLogField.Component,
                    code),
                OperationalLogValue.FromCode(
                    OperationalLogField.Outcome,
                    code),
                OperationalLogValue.FromCode(
                    OperationalLogField.Reason,
                    code),
                OperationalLogValue.FromCode(
                    OperationalLogField.State,
                    code)),
            "oversized structured event is rejected");
        Check.Equal(
            1L,
            smallLogger.GetSnapshot().Oversized,
            "oversized structured event is counted");
    }

    private static void CheckConsoleBoundaryOwnership()
    {
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        try
        {
            using var sink = new StringWriter();
            using var logger = new BoundedStructuredLogger(sink);
            using (StructuredConsoleBoundary.Install(logger))
            {
                Console.WriteLine(
                    "[net] username=alice endpoint=198.51.100.7");
                Console.Error.WriteLine(
                    "[security] password=hunter2 token=DEADBEEF");
            }
            Check.True(
                logger.WaitUntilIdle(TimeSpan.FromSeconds(2)),
                "console diagnostics reach the writer");

            Check.True(
                ReferenceEquals(Console.Out, originalOutput) &&
                ReferenceEquals(Console.Error, originalError),
                "console boundary restores streams it still owns");
            var output = sink.ToString();
            Check.True(
                !output.Contains("alice", StringComparison.Ordinal) &&
                !output.Contains("198.51.100.7", StringComparison.Ordinal) &&
                !output.Contains("hunter2", StringComparison.Ordinal) &&
                !output.Contains("DEADBEEF", StringComparison.Ordinal),
                "installed console boundary suppresses sensitive content");

            using var replacement = new StringWriter();
            using (var boundary =
                   StructuredConsoleBoundary.Install(logger))
            {
                Console.SetOut(replacement);
                var installedReplacement = Console.Out;
                boundary.Dispose();
                Check.True(
                    ReferenceEquals(
                        Console.Out,
                        installedReplacement),
                    "boundary does not overwrite a newer console owner");
            }
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }

    private static void CheckSinkFailureIsolation()
    {
        using var logger = new BoundedStructuredLogger(
            new ThrowingTextWriter());
        Check.True(
            logger.TryWrite(
                OperationalLogEvent.TelemetryExporter,
                OperationalLogLevel.Error),
            "producer enqueues without waiting for the failing sink");
        Check.True(
            logger.WaitUntilIdle(TimeSpan.FromSeconds(2)),
            "failing sink is consumed without leaking its exception");
        Check.Equal(
            1L,
            logger.GetSnapshot().SinkFailures,
            "sink failure is counted");
    }

    private static void CheckSlowSinkDoesNotBlockProducers()
    {
        using var sink = new BlockingTextWriter();
        var timeProvider = new CountingTimeProvider();
        using var logger = new BoundedStructuredLogger(
            sink,
            new StructuredLogOptions
            {
                QueueCapacity = 2,
                GlobalEventsPerWindow = 32,
                EventsPerKindPerWindow = 32,
                ShutdownTimeout = TimeSpan.FromSeconds(2)
            },
            timeProvider);
        Check.True(
            logger.TryWrite(
                OperationalLogEvent.TelemetryExporter,
                OperationalLogLevel.Information),
            "first event enters the slow sink");
        Check.True(
            sink.WaitUntilBlocked(TimeSpan.FromSeconds(2)),
            "adversarial sink stalls the dedicated writer");

        var timer = Stopwatch.StartNew();
        for (var index = 0; index < 16; index++)
        {
            logger.TryWrite(
                OperationalLogEvent.TelemetryExporter,
                OperationalLogLevel.Information);
        }
        timer.Stop();
        Check.True(
            timer.Elapsed < TimeSpan.FromSeconds(1),
            "slow sink never blocks structured-log producers");
        Check.True(
            logger.GetSnapshot().QueueFull > 0,
            "full structured-log queue is explicitly counted");
        Check.Equal(
            logger.GetSnapshot().Accepted,
            timeProvider.UtcReads,
            "queue-full events are rejected before JSON encoding");

        sink.Release();
        Check.True(
            logger.WaitUntilIdle(TimeSpan.FromSeconds(2)),
            "slow sink worker drains and terminates deterministically");
        var snapshot = logger.GetSnapshot();
        Check.Equal(
            snapshot.Accepted,
            snapshot.Written,
            "every accepted event is written after the sink recovers");
    }

    private static void CheckShutdownIsBoundedAndAccounted()
    {
        using var sink = new BlockingTextWriter();
        using var logger = new BoundedStructuredLogger(
            sink,
            new StructuredLogOptions
            {
                QueueCapacity = 2,
                GlobalEventsPerWindow = 8,
                EventsPerKindPerWindow = 8,
                ShutdownTimeout = TimeSpan.FromMilliseconds(50)
            });
        Check.True(
            logger.TryWrite(
                OperationalLogEvent.TelemetryExporter,
                OperationalLogLevel.Warning),
            "shutdown fixture enters the slow sink");
        Check.True(
            sink.WaitUntilBlocked(TimeSpan.FromSeconds(2)),
            "shutdown fixture stalls the dedicated writer");
        Check.True(
            logger.TryWrite(
                OperationalLogEvent.TelemetryExporter,
                OperationalLogLevel.Warning) &&
            logger.TryWrite(
                OperationalLogEvent.TelemetryExporter,
                OperationalLogLevel.Warning),
            "shutdown fixture fills the pending queue");

        var timer = Stopwatch.StartNew();
        logger.Dispose();
        timer.Stop();
        Check.True(
            timer.Elapsed < TimeSpan.FromSeconds(1),
            "blocked log sink cannot make shutdown unbounded");
        Check.Equal(
            2L,
            logger.GetSnapshot().ShutdownDropped,
            "pending records discarded at shutdown are counted");

        sink.Release();
        Check.True(
            logger.WaitUntilStopped(TimeSpan.FromSeconds(2)),
            "released shutdown writer leaves no background thread");
    }

    private static string OnlyLine(
        BoundedStructuredLogger logger,
        StringWriter sink)
    {
        Check.True(
            logger.WaitUntilIdle(TimeSpan.FromSeconds(2)),
            "structured event reaches the writer");
        var lines = sink.ToString().Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries);
        Check.Equal(1, lines.Length, "one JSON line emitted");
        return lines[0];
    }

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) =>
            throw new IOException("Synthetic sink failure.");
    }

    private sealed class BlockingTextWriter : TextWriter
    {
        private readonly ManualResetEventSlim _blocked = new(false);
        private readonly ManualResetEventSlim _release = new(false);

        public override Encoding Encoding => Encoding.UTF8;

        public bool WaitUntilBlocked(TimeSpan timeout) =>
            _blocked.Wait(timeout);

        public void Release() => _release.Set();

        public override void Write(char value)
        {
            _blocked.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new IOException(
                    "Synthetic slow sink was not released.");
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _release.Set();
                _blocked.Dispose();
                _release.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class CountingTimeProvider : TimeProvider
    {
        private long _utcReads;

        public long UtcReads => Interlocked.Read(ref _utcReads);

        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Increment(ref _utcReads);
            return DateTimeOffset.UnixEpoch;
        }

        public override long GetTimestamp() =>
            TimeProvider.System.GetTimestamp();

        public override long TimestampFrequency =>
            TimeProvider.System.TimestampFrequency;
    }
}
