using System.Text;

namespace Godswar.Server.Operations.Observability;

internal sealed partial class BoundedPrometheusCollector
{
    private string Render(PrometheusRenderSnapshot snapshot)
    {
        var output = new StringBuilder(
            Math.Min(_options.MaximumSnapshotBytes, 16_384));
        AppendCollectorState(output, snapshot.Collector);
        string? previousType = null;
        foreach (var series in snapshot.Series
                     .OrderBy(static value => value.MetricName)
                     .ThenBy(static value => value.Key))
        {
            var typeKey = $"{series.MetricName}|{(byte)series.Kind}";
            if (!StringComparer.Ordinal.Equals(previousType, typeKey))
            {
                if (!AppendBounded(
                        output,
                        $"# TYPE {series.MetricName} " +
                        $"{TypeCode(series.Kind)}\n"))
                {
                    MarkSnapshotTruncated();
                    break;
                }
                previousType = typeKey;
            }

            var renderedSeries = new StringBuilder(2_048);
            if (!AppendSeries(renderedSeries, series) ||
                !AppendBounded(output, renderedSeries.ToString()))
            {
                MarkSnapshotTruncated();
                break;
            }
        }
        return output.ToString();
    }

    private bool AppendSeries(
        StringBuilder output,
        PrometheusSeriesSnapshot series)
    {
        if (series.Kind != MetricAggregationKind.Histogram)
        {
            return AppendSample(
                output,
                series.MetricName,
                series.Labels,
                series.Value);
        }

        var bounds = PrometheusHistogramPolicy.MillisecondBounds;
        for (var index = 0; index < bounds.Length; index++)
        {
            if (!AppendSample(
                    output,
                    $"{series.MetricName}_bucket",
                    series.Labels,
                    series.HistogramBuckets[index],
                    "le",
                    PrometheusMetricPolicy.Number(bounds[index])))
            {
                return false;
            }
        }
        return AppendSample(
                output,
                $"{series.MetricName}_bucket",
                series.Labels,
                series.HistogramBuckets[^1],
                "le",
                "+Inf") &&
            AppendSample(
                output,
                $"{series.MetricName}_sum",
                series.Labels,
                series.Sum) &&
            AppendSample(
                output,
                $"{series.MetricName}_count",
                series.Labels,
                series.Count);
    }

    private bool AppendSample(
        StringBuilder output,
        string name,
        IReadOnlyList<PrometheusMetricLabel> labels,
        double value,
        string? generatedLabelName = null,
        string? generatedLabelValue = null)
    {
        var line = new StringBuilder(name.Length + labels.Count * 80 + 32);
        line.Append(name);
        if (labels.Count > 0 || generatedLabelName is not null)
        {
            line.Append('{');
            for (var index = 0; index < labels.Count; index++)
            {
                if (index > 0)
                {
                    line.Append(',');
                }
                line.Append(labels[index].Name);
                line.Append("=\"");
                line.Append(labels[index].Value);
                line.Append('"');
            }
            if (generatedLabelName is not null)
            {
                if (labels.Count > 0)
                {
                    line.Append(',');
                }
                line.Append(generatedLabelName);
                line.Append("=\"");
                line.Append(generatedLabelValue);
                line.Append('"');
            }
            line.Append('}');
        }
        line.Append(' ');
        line.Append(PrometheusMetricPolicy.Number(value));
        line.Append('\n');
        return AppendBounded(output, line.ToString());
    }

    private void AppendCollectorState(
        StringBuilder output,
        PrometheusCollectorRuntimeSnapshot snapshot)
    {
        var state = new[]
        {
            ("instruments", (long)snapshot.Instruments),
            ("series", (long)snapshot.Series),
            ("measurements", snapshot.Measurements),
            ("dropped_instruments", snapshot.DroppedInstruments),
            ("dropped_series", snapshot.DroppedSeries),
            ("dropped_tags", snapshot.DroppedTags),
            ("dropped_measurements", snapshot.DroppedMeasurements),
            ("truncated_snapshots", snapshot.TruncatedSnapshots)
        };
        foreach (var item in state)
        {
            if (!AppendBounded(
                    output,
                    "godswar_server_metrics_collector" +
                    $"{{state=\"{item.Item1}\"}} {item.Item2}\n"))
            {
                break;
            }
        }
    }

    private bool AppendBounded(
        StringBuilder output,
        string value)
    {
        if (value.Length >
            _options.MaximumSnapshotBytes - output.Length)
        {
            return false;
        }

        output.Append(value);
        return true;
    }

    private void MarkSnapshotTruncated()
    {
        Interlocked.Increment(ref _truncatedSnapshots);
    }

    private static string TypeCode(MetricAggregationKind kind) =>
        kind switch
        {
            MetricAggregationKind.Counter => "counter",
            MetricAggregationKind.UpDownCounter => "gauge",
            MetricAggregationKind.Gauge => "gauge",
            MetricAggregationKind.Histogram => "histogram",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
}
