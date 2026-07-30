using System.Collections.Immutable;
using System.Globalization;

namespace Godswar.Server.Operations.Observability;

internal sealed class PrometheusCollectorOptions
{
    public string[] AllowedMeterPrefixes { get; init; } =
        ["Godswar.Server."];

    public int MaximumInstruments { get; init; } = 256;

    public int MaximumSeries { get; init; } = 2_048;

    public int MaximumTagsPerSeries { get; init; } = 8;

    public int MaximumSnapshotBytes { get; init; } = 1_048_576;

    internal Action? SnapshotCaptured { get; init; }

    public void Validate()
    {
        if (AllowedMeterPrefixes is null ||
            AllowedMeterPrefixes.Length is < 1 or > 8 ||
            AllowedMeterPrefixes.Any(static value =>
                string.IsNullOrWhiteSpace(value) ||
                value.Length > 128))
        {
            throw new InvalidDataException(
                "Prometheus meter prefixes must have a finite configured set.");
        }
        if (MaximumInstruments is < 1 or > 4_096)
        {
            throw new InvalidDataException(
                "The Prometheus instrument bound is invalid.");
        }
        if (MaximumSeries is < 1 or > 100_000)
        {
            throw new InvalidDataException(
                "The Prometheus series bound is invalid.");
        }
        if (MaximumTagsPerSeries is < 0 or >
            StructuredLogOptions.MaximumFields)
        {
            throw new InvalidDataException(
                "The Prometheus tag bound is invalid.");
        }
        if (MaximumSnapshotBytes is < 1_024 or > 16_777_216)
        {
            throw new InvalidDataException(
                "The Prometheus snapshot byte bound is invalid.");
        }
    }
}

internal readonly record struct PrometheusCollectorRuntimeSnapshot(
    int Instruments,
    int Series,
    long Measurements,
    long DroppedInstruments,
    long DroppedSeries,
    long DroppedTags,
    long DroppedMeasurements,
    long TruncatedSnapshots);

internal readonly record struct PrometheusRenderSnapshot(
    PrometheusCollectorRuntimeSnapshot Collector,
    ImmutableArray<PrometheusSeriesSnapshot> Series);

internal sealed record PrometheusSeriesSnapshot(
    string Key,
    string MetricName,
    MetricAggregationKind Kind,
    ImmutableArray<PrometheusMetricLabel> Labels,
    double Value,
    long Count,
    double Sum,
    ImmutableArray<long> HistogramBuckets);

internal enum MetricAggregationKind : byte
{
    Counter = 1,
    UpDownCounter = 2,
    Gauge = 3,
    Histogram = 4
}

internal readonly record struct PrometheusMetricLabel(
    string Name,
    string Value);

internal sealed class PrometheusInstrumentBinding(
    BoundedPrometheusCollector owner,
    string metricName,
    MetricAggregationKind kind,
    bool isObservable)
{
    public BoundedPrometheusCollector Owner { get; } = owner;

    public string MetricName { get; } = metricName;

    public MetricAggregationKind Kind { get; } = kind;

    public bool IsObservable { get; } = isObservable;
}

internal sealed class PrometheusSeries(
    string key,
    string metricName,
    MetricAggregationKind kind,
    PrometheusMetricLabel[] labels,
    bool isObservable)
{
    public string Key { get; } = key;

    public string MetricName { get; } = metricName;

    public MetricAggregationKind Kind { get; } = kind;

    public PrometheusMetricLabel[] Labels { get; } = labels;

    public bool IsObservable { get; } = isObservable;

    public long LastObservedEpoch { get; set; }

    public double Value { get; set; }

    public long Count { get; set; }

    public double Sum { get; set; }

    public long[] HistogramBuckets { get; } =
        kind == MetricAggregationKind.Histogram
            ? new long[PrometheusHistogramPolicy.MillisecondBounds.Length + 1]
            : [];
}

internal static class PrometheusHistogramPolicy
{
    // Server histograms currently measure durations in milliseconds. These
    // fixed buckets keep exporter memory and output deterministic while making
    // histogram_quantile queries valid.
    private static readonly double[] Bounds =
    [
        0.5d,
        1d,
        2.5d,
        5d,
        10d,
        25d,
        50d,
        100d,
        250d,
        500d,
        1_000d,
        2_500d,
        5_000d,
        10_000d,
        30_000d
    ];

    public static ReadOnlySpan<double> MillisecondBounds => Bounds;
}

internal static class PrometheusMetricPolicy
{
    private const int MaximumMetricNameLength = 128;
    private const int MaximumTagNameLength = 64;

    private static readonly string[] ForbiddenTagFragments =
    [
        "account",
        "character",
        "player_id",
        "username",
        "password",
        "secret",
        "token",
        "cookie",
        "ticket_id",
        "session_id",
        "connection_id",
        "operation_id",
        "trace_id",
        "span_id",
        "ip_address",
        "remote",
        "payload",
        "message",
        "exception"
    ];

    public static bool TryNormalizeMetricName(
        string value,
        out string normalized) =>
        TryNormalizeName(
            value,
            MaximumMetricNameLength,
            forbidSensitive: false,
            out normalized);

    public static bool TryNormalizeTagName(
        string value,
        out string normalized) =>
        TryNormalizeName(
            value,
            MaximumTagNameLength,
            forbidSensitive: true,
            out normalized);

    public static bool TryNormalizeTagValue(
        object? value,
        out string normalized)
    {
        switch (value)
        {
            case string text when SafeTelemetryCode.IsSafe(text):
                normalized = text;
                return true;
            case bool boolean:
                normalized = boolean ? "true" : "false";
                return true;
            default:
                normalized = string.Empty;
                return false;
        }
    }

    public static string Number(double value) =>
        value switch
        {
            double.PositiveInfinity => "+Inf",
            double.NegativeInfinity => "-Inf",
            _ when double.IsNaN(value) => "NaN",
            _ => value.ToString("R", CultureInfo.InvariantCulture)
        };

    private static bool TryNormalizeName(
        string value,
        int maximumLength,
        bool forbidSensitive,
        out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength)
        {
            return false;
        }

        Span<char> output = stackalloc char[value.Length + 1];
        var written = 0;
        foreach (var character in value)
        {
            if (character is >= 'a' and <= 'z' or
                >= '0' and <= '9' or '_')
            {
                output[written++] = character;
            }
            else if (character is >= 'A' and <= 'Z')
            {
                output[written++] = char.ToLowerInvariant(character);
            }
            else if (character is '.' or '-')
            {
                output[written++] = '_';
            }
            else
            {
                return false;
            }
        }

        if (written == 0)
        {
            return false;
        }
        if (output[0] is >= '0' and <= '9')
        {
            for (var index = written; index > 0; index--)
            {
                output[index] = output[index - 1];
            }
            output[0] = '_';
            written++;
        }

        normalized = new string(output[..written]);
        if (!forbidSensitive)
        {
            return true;
        }

        foreach (var fragment in ForbiddenTagFragments)
        {
            if (normalized.Contains(
                    fragment,
                    StringComparison.Ordinal))
            {
                normalized = string.Empty;
                return false;
            }
        }

        return true;
    }
}
