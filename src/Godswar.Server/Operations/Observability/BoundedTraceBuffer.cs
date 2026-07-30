using System.Diagnostics;
using System.Globalization;

namespace Godswar.Server.Operations.Observability;

internal sealed class BoundedTraceBuffer : IDisposable
{
    private static readonly HashSet<string> AllowedTags =
        Enum.GetValues<ServerTraceTag>()
            .Select(ServerTraceCodes.Tag)
            .ToHashSet(StringComparer.Ordinal);

    private readonly CapturedTraceSpan?[] _buffer;
    private readonly object _gate = new();
    private readonly ActivityListener _listener;
    private readonly BoundedTraceBufferOptions _options;

    private long _captured;
    private int _count;
    private int _disposed;
    private int _next;
    private long _overwritten;
    private long _rejectedTags;

    public BoundedTraceBuffer(
        BoundedTraceBufferOptions? options = null)
    {
        _options = options ?? new BoundedTraceBufferOptions();
        _options.Validate();
        _buffer = new CapturedTraceSpan[_options.Capacity];
        _listener = new ActivityListener
        {
            ShouldListenTo = static source =>
                source.Name == ServerActivity.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext>
                options) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (
                ref ActivityCreationOptions<string> options) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = Capture
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public BoundedTraceBufferRuntimeSnapshot GetRuntimeSnapshot()
    {
        lock (_gate)
        {
            return new BoundedTraceBufferRuntimeSnapshot(
                _buffer.Length,
                _count,
                Interlocked.Read(ref _captured),
                Interlocked.Read(ref _overwritten),
                Interlocked.Read(ref _rejectedTags));
        }
    }

    public CapturedTraceSpan[] Snapshot()
    {
        lock (_gate)
        {
            var result = new CapturedTraceSpan[_count];
            var start = _count == _buffer.Length ? _next : 0;
            for (var index = 0; index < _count; index++)
            {
                result[index] =
                    _buffer[(start + index) % _buffer.Length] ??
                    throw new InvalidOperationException(
                        "The bounded trace buffer has an empty live slot.");
            }
            return result;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _listener.Dispose();
        lock (_gate)
        {
            Array.Clear(_buffer);
            _count = 0;
            _next = 0;
        }
    }

    private void Capture(Activity activity)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            !ServerTraceCodesIsKnownOperation(activity.OperationName))
        {
            return;
        }

        var tags = CaptureTags(activity);
        var span = new CapturedTraceSpan(
            activity.OperationName,
            activity.TraceId.ToHexString(),
            activity.SpanId.ToHexString(),
            activity.ParentSpanId == default
                ? null
                : activity.ParentSpanId.ToHexString(),
            new DateTimeOffset(activity.StartTimeUtc, TimeSpan.Zero),
            activity.Duration < TimeSpan.Zero
                ? TimeSpan.Zero
                : activity.Duration,
            activity.Status switch
            {
                ActivityStatusCode.Ok => "ok",
                ActivityStatusCode.Error => "error",
                _ => "unset"
            },
            tags);

        lock (_gate)
        {
            if (_disposed != 0)
            {
                return;
            }
            if (_count == _buffer.Length)
            {
                Interlocked.Increment(ref _overwritten);
                ObservabilityCoreMetrics.RecordTrace("overwritten");
            }
            else
            {
                _count++;
            }

            _buffer[_next] = span;
            _next = (_next + 1) % _buffer.Length;
            Interlocked.Increment(ref _captured);
            ObservabilityCoreMetrics.RecordTrace("captured");
        }
    }

    private CapturedTraceTag[] CaptureTags(Activity activity)
    {
        var tags = new List<CapturedTraceTag>(
            _options.MaximumTagsPerSpan);
        var outcomeName =
            ServerTraceCodes.Tag(ServerTraceTag.Outcome);
        if (_options.MaximumTagsPerSpan > 0)
        {
            var outcome = activity.GetTagItem(outcomeName);
            if (TryFormatValue(outcome, out var outcomeValue))
            {
                tags.Add(new CapturedTraceTag(
                    outcomeName,
                    outcomeValue));
            }
        }
        foreach (var tag in activity.TagObjects)
        {
            if (tag.Key == outcomeName)
            {
                continue;
            }
            if (tags.Count >= _options.MaximumTagsPerSpan ||
                !AllowedTags.Contains(tag.Key) ||
                !TryFormatValue(tag.Value, out var value))
            {
                Interlocked.Increment(ref _rejectedTags);
                ObservabilityCoreMetrics.RecordTrace("tag_rejected");
                continue;
            }
            tags.Add(new CapturedTraceTag(tag.Key, value));
        }

        return tags
            .OrderBy(static value => value.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool TryFormatValue(
        object? value,
        out string formatted)
    {
        switch (value)
        {
            case string code when SafeTelemetryCode.IsSafe(code):
                formatted = code;
                return true;
            case bool boolean:
                formatted = boolean ? "true" : "false";
                return true;
            case byte or sbyte or short or ushort or int or uint or
                long or ulong:
                formatted = Convert.ToString(
                    value,
                    CultureInfo.InvariantCulture) ?? string.Empty;
                return formatted.Length <= SafeTelemetryCode.MaximumLength;
            default:
                formatted = string.Empty;
                return false;
        }
    }

    private static bool ServerTraceCodesIsKnownOperation(string value)
    {
        foreach (var operation in Enum.GetValues<ServerTraceOperation>())
        {
            if (ServerTraceCodes.Operation(operation) == value)
            {
                return true;
            }
        }
        return false;
    }
}
