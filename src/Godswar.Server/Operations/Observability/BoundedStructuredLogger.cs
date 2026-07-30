using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Godswar.Server.Operations.Observability;

internal sealed class BoundedStructuredLogger : IDisposable
{
    private const int EventKindCount = 6;

    private readonly int[] _eventCounts = new int[EventKindCount];
    private readonly object _gate = new();
    private readonly StructuredLogOptions _options;
    private readonly Queue<StructuredLogEnvelope> _queue;
    private readonly TextWriter _sink;
    private readonly TimeProvider _timeProvider;
    private readonly Thread _writerThread;

    private long _accepted;
    private int _disposed;
    private int _globalCount;
    private long _legacyLinesSuppressed;
    private long _oversized;
    private long _queueFull;
    private long _rateLimited;
    private long _shutdownDropped;
    private long _sinkFailures;
    private long _windowTimestamp;
    private long _written;
    private bool _stopRequested;
    private bool _writerActive;

    public BoundedStructuredLogger(
        TextWriter sink,
        StructuredLogOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = options ?? new StructuredLogOptions();
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _windowTimestamp = _timeProvider.GetTimestamp();
        _queue = new Queue<StructuredLogEnvelope>(
            _options.QueueCapacity);
        _writerThread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "godswar-structured-log-writer"
        };
        _writerThread.Start();
    }

    public StructuredLogRuntimeSnapshot GetSnapshot() =>
        new(
            Interlocked.Read(ref _accepted),
            Interlocked.Read(ref _written),
            Interlocked.Read(ref _queueFull),
            Interlocked.Read(ref _rateLimited),
            Interlocked.Read(ref _oversized),
            Interlocked.Read(ref _sinkFailures),
            Interlocked.Read(ref _legacyLinesSuppressed),
            Interlocked.Read(ref _shutdownDropped));

    public bool TryWrite(
        OperationalLogEvent eventId,
        OperationalLogLevel level,
        params ReadOnlySpan<OperationalLogValue> fields)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }
        if (!Enum.IsDefined(eventId))
        {
            throw new ArgumentOutOfRangeException(nameof(eventId));
        }
        if (!Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }
        if (fields.Length > StructuredLogOptions.MaximumFields)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fields),
                "A structured log event has a fixed field bound.");
        }

        ValidateUniqueFields(fields);
        lock (_gate)
        {
            if (_disposed != 0)
            {
                return false;
            }
            if (!AdmitLocked(eventId))
            {
                Interlocked.Increment(ref _rateLimited);
                ObservabilityCoreMetrics.RecordLog(
                    eventId,
                    "rate_limited");
                return false;
            }

            if (_queue.Count >= _options.QueueCapacity)
            {
                Interlocked.Increment(ref _queueFull);
                ObservabilityCoreMetrics.RecordLog(
                    eventId,
                    "queue_full");
                return false;
            }

            var bytes = Encode(eventId, level, fields);
            if (bytes.Length > _options.MaximumLineBytes)
            {
                Interlocked.Increment(ref _oversized);
                ObservabilityCoreMetrics.RecordLog(
                    eventId,
                    "oversized");
                return false;
            }

            _queue.Enqueue(new StructuredLogEnvelope(eventId, bytes));
            Interlocked.Increment(ref _accepted);
            ObservabilityCoreMetrics.RecordLog(
                eventId,
                "enqueued");
            Monitor.Pulse(_gate);
            return true;
        }
    }

    internal void SuppressLegacyLine(
        LegacyConsoleSource source,
        ReadOnlySpan<char> prefix,
        long characterCount,
        bool truncated)
    {
        Interlocked.Increment(ref _legacyLinesSuppressed);
        TryWrite(
            OperationalLogEvent.LegacyDiagnosticSuppressed,
            source == LegacyConsoleSource.Stderr
                ? OperationalLogLevel.Warning
                : OperationalLogLevel.Debug,
            OperationalLogValue.FromCode(
                OperationalLogField.Source,
                source == LegacyConsoleSource.Stderr
                    ? "stderr"
                    : "stdout"),
            OperationalLogValue.FromCode(
                OperationalLogField.Category,
                LegacyDiagnosticClassifier.Classify(prefix)),
            OperationalLogValue.FromNumber(
                OperationalLogField.Count,
                Math.Max(0, characterCount)),
            OperationalLogValue.FromBoolean(
                OperationalLogField.Truncated,
                truncated));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _stopRequested = true;
            Monitor.PulseAll(_gate);
        }

        if (ReferenceEquals(Thread.CurrentThread, _writerThread) ||
            _writerThread.Join(_options.ShutdownTimeout))
        {
            return;
        }

        lock (_gate)
        {
            var dropped = _queue.Count;
            _queue.Clear();
            Interlocked.Add(ref _shutdownDropped, dropped);
            Monitor.PulseAll(_gate);
        }
    }

    internal bool WaitUntilIdle(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var started = Environment.TickCount64;
        lock (_gate)
        {
            while (_queue.Count > 0 || _writerActive)
            {
                var elapsed = TimeSpan.FromMilliseconds(
                    Environment.TickCount64 - started);
                var remaining = timeout - elapsed;
                if (remaining <= TimeSpan.Zero ||
                    !Monitor.Wait(_gate, remaining))
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal bool WaitUntilStopped(TimeSpan timeout) =>
        _writerThread.Join(timeout);

    private void WriteLoop()
    {
        while (true)
        {
            StructuredLogEnvelope envelope;
            lock (_gate)
            {
                while (_queue.Count == 0 && !_stopRequested)
                {
                    Monitor.Wait(_gate);
                }
                if (_queue.Count == 0 && _stopRequested)
                {
                    Monitor.PulseAll(_gate);
                    return;
                }

                envelope = _queue.Dequeue();
                _writerActive = true;
            }

            try
            {
                _sink.WriteLine(
                    Encoding.UTF8.GetString(envelope.Bytes));
                _sink.Flush();
                Interlocked.Increment(ref _written);
                ObservabilityCoreMetrics.RecordLog(
                    envelope.EventId,
                    "written");
            }
            catch
            {
                Interlocked.Increment(ref _sinkFailures);
                ObservabilityCoreMetrics.RecordLog(
                    envelope.EventId,
                    "sink_failure");
            }
            finally
            {
                lock (_gate)
                {
                    _writerActive = false;
                    if (_queue.Count == 0)
                    {
                        Monitor.PulseAll(_gate);
                    }
                }
            }
        }
    }

    private bool AdmitLocked(OperationalLogEvent eventId)
    {
        var now = _timeProvider.GetTimestamp();
        if (_timeProvider.GetElapsedTime(_windowTimestamp, now) >=
            _options.RateWindow)
        {
            _windowTimestamp = now;
            _globalCount = 0;
            Array.Clear(_eventCounts);
        }

        var index = checked((int)eventId - 1);
        if (_globalCount >= _options.GlobalEventsPerWindow ||
            _eventCounts[index] >= _options.EventsPerKindPerWindow)
        {
            return false;
        }

        _globalCount++;
        _eventCounts[index]++;
        return true;
    }

    private byte[] Encode(
        OperationalLogEvent eventId,
        OperationalLogLevel level,
        ReadOnlySpan<OperationalLogValue> fields)
    {
        var output = new ArrayBufferWriter<byte>(
            Math.Min(_options.MaximumLineBytes, 1_024));
        using var writer = new Utf8JsonWriter(
            output,
            new JsonWriterOptions
            {
                Indented = false,
                SkipValidation = false
            });
        writer.WriteStartObject();
        writer.WriteString(
            "timestamp_utc",
            _timeProvider.GetUtcNow());
        writer.WriteNumber("event_id", (int)eventId);
        writer.WriteString(
            "event",
            StructuredLogCodes.Event(eventId));
        writer.WriteString(
            "level",
            StructuredLogCodes.Level(level));
        foreach (var field in fields)
        {
            var name = StructuredLogCodes.Field(field.Field);
            switch (field.Kind)
            {
                case OperationalLogValueKind.Code:
                    writer.WriteString(name, field.Code);
                    break;
                case OperationalLogValueKind.Number:
                    writer.WriteNumber(name, field.Number);
                    break;
                case OperationalLogValueKind.Boolean:
                    writer.WriteBoolean(name, field.Boolean);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(fields),
                        "Unknown structured-log value kind.");
            }
        }
        writer.WriteEndObject();
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    private static void ValidateUniqueFields(
        ReadOnlySpan<OperationalLogValue> fields)
    {
        Span<bool> seen = stackalloc bool[10];
        foreach (var field in fields)
        {
            var index = (int)field.Field;
            if (index <= 0 ||
                index >= seen.Length ||
                seen[index])
            {
                throw new ArgumentException(
                    "Structured log fields must be finite and unique.",
                    nameof(fields));
            }
            seen[index] = true;
        }
    }

    private readonly record struct StructuredLogEnvelope(
        OperationalLogEvent EventId,
        byte[] Bytes);
}
