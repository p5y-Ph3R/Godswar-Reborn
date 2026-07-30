using System.Collections.Immutable;
using System.Diagnostics.Metrics;

namespace Godswar.Server.Operations.Observability;

internal sealed partial class BoundedPrometheusCollector : IDisposable
{
    private readonly Dictionary<Instrument, PrometheusInstrumentBinding>
        _bindings = new(ReferenceEqualityComparer.Instance);
    private readonly string[] _allowedMeterPrefixes;
    private readonly object _collectionGate = new();
    private readonly object _gate = new();
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, Instrument> _metricOwners =
        new(StringComparer.Ordinal);
    private readonly PrometheusCollectorOptions _options;
    private readonly Dictionary<string, PrometheusSeries> _series =
        new(StringComparer.Ordinal);

    private int _disposed;
    private long _collectionEpoch;
    private long _currentCollectionEpoch;
    private long _droppedInstruments;
    private long _droppedMeasurements;
    private long _droppedSeries;
    private long _droppedTags;
    private long _measurements;
    private long _truncatedSnapshots;

    public BoundedPrometheusCollector(
        PrometheusCollectorOptions? options = null)
    {
        _options = options ?? new PrometheusCollectorOptions();
        _options.Validate();
        _allowedMeterPrefixes = [.. _options.AllowedMeterPrefixes];

        _listener.InstrumentPublished = OnInstrumentPublished;
        _listener.SetMeasurementEventCallback<long>(
            static (instrument, measurement, tags, state) =>
                ((PrometheusInstrumentBinding)state!)
                    .Owner.RecordMeasurement(
                        (PrometheusInstrumentBinding)state!,
                        measurement,
                        tags));
        _listener.SetMeasurementEventCallback<double>(
            static (instrument, measurement, tags, state) =>
                ((PrometheusInstrumentBinding)state!)
                    .Owner.RecordMeasurement(
                        (PrometheusInstrumentBinding)state!,
                        measurement,
                        tags));
        _listener.SetMeasurementEventCallback<int>(
            static (instrument, measurement, tags, state) =>
                ((PrometheusInstrumentBinding)state!)
                    .Owner.RecordMeasurement(
                        (PrometheusInstrumentBinding)state!,
                        measurement,
                        tags));
        _listener.SetMeasurementEventCallback<float>(
            static (instrument, measurement, tags, state) =>
                ((PrometheusInstrumentBinding)state!)
                    .Owner.RecordMeasurement(
                        (PrometheusInstrumentBinding)state!,
                        measurement,
                        tags));
        _listener.Start();
    }

    public PrometheusCollectorRuntimeSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return GetRuntimeSnapshotLocked();
        }
    }

    public string CollectSnapshot()
    {
        lock (_collectionGate)
        {
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref _disposed) != 0,
                this);
            lock (_gate)
            {
                _currentCollectionEpoch = checked(++_collectionEpoch);
            }

            _listener.RecordObservableInstruments();
            PrometheusRenderSnapshot snapshot;
            lock (_gate)
            {
                RemoveStaleObservableSeriesLocked(
                    _currentCollectionEpoch);
                _currentCollectionEpoch = 0;
                snapshot = CaptureRenderSnapshotLocked();
            }

            _options.SnapshotCaptured?.Invoke();
            return Render(snapshot);
        }
    }

    public void Dispose()
    {
        lock (_collectionGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _listener.Dispose();
            lock (_gate)
            {
                _bindings.Clear();
                _metricOwners.Clear();
                _series.Clear();
            }
        }
    }

    private void OnInstrumentPublished(
        Instrument instrument,
        MeterListener listener)
    {
        if (Volatile.Read(ref _disposed) != 0 ||
            !_allowedMeterPrefixes.Any(prefix =>
                instrument.Meter.Name.StartsWith(
                    prefix,
                    StringComparison.Ordinal)))
        {
            return;
        }
        if (!PrometheusMetricPolicy.TryNormalizeMetricName(
                instrument.Name,
                out var metricName) ||
            !TryGetAggregationKind(instrument, out var kind) ||
            (kind == MetricAggregationKind.Histogram &&
                !StringComparer.Ordinal.Equals(instrument.Unit, "ms")))
        {
            Interlocked.Increment(ref _droppedInstruments);
            return;
        }

        lock (_gate)
        {
            if (_disposed != 0 || _bindings.ContainsKey(instrument))
            {
                return;
            }
            if (_bindings.Count >= _options.MaximumInstruments ||
                (_metricOwners.TryGetValue(metricName, out var owner) &&
                    !ReferenceEquals(owner, instrument)))
            {
                Interlocked.Increment(ref _droppedInstruments);
                return;
            }

            var binding = new PrometheusInstrumentBinding(
                this,
                metricName,
                kind,
                instrument.GetType().Name.StartsWith(
                    "Observable",
                    StringComparison.Ordinal));
            _bindings.Add(instrument, binding);
            _metricOwners.TryAdd(metricName, instrument);
            listener.EnableMeasurementEvents(instrument, binding);
        }
    }

    private void RecordMeasurement<T>(
        PrometheusInstrumentBinding binding,
        T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct, IConvertible
    {
        double numeric;
        try
        {
            numeric = Convert.ToDouble(
                measurement,
                System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            Interlocked.Increment(ref _droppedMeasurements);
            return;
        }
        if (!double.IsFinite(numeric) ||
            (binding.Kind == MetricAggregationKind.Counter &&
                numeric < 0) ||
            (binding.Kind == MetricAggregationKind.Histogram &&
                numeric < 0))
        {
            Interlocked.Increment(ref _droppedMeasurements);
            return;
        }
        if (!TryNormalizeTags(tags, out var labels))
        {
            Interlocked.Increment(ref _droppedTags);
            return;
        }

        var key = BuildSeriesKey(binding, labels);
        lock (_gate)
        {
            if (_disposed != 0)
            {
                return;
            }
            if (!_series.TryGetValue(key, out var series))
            {
                if (_series.Count >= _options.MaximumSeries)
                {
                    Interlocked.Increment(ref _droppedSeries);
                    return;
                }
                series = new PrometheusSeries(
                    key,
                    binding.MetricName,
                    binding.Kind,
                    labels,
                    binding.IsObservable);
                _series.Add(key, series);
            }

            if (binding.IsObservable)
            {
                series.LastObservedEpoch = _currentCollectionEpoch;
            }
            ApplyMeasurement(
                series,
                numeric,
                binding.IsObservable);
            Interlocked.Increment(ref _measurements);
        }
    }

    private bool TryNormalizeTags(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        out PrometheusMetricLabel[] labels)
    {
        labels = [];
        if (tags.Length > _options.MaximumTagsPerSeries)
        {
            return false;
        }

        var output = new PrometheusMetricLabel[tags.Length];
        for (var index = 0; index < tags.Length; index++)
        {
            if (!PrometheusMetricPolicy.TryNormalizeTagName(
                    tags[index].Key,
                    out var name) ||
                !PrometheusMetricPolicy.TryNormalizeTagValue(
                    tags[index].Value,
                    out var value))
            {
                return false;
            }
            output[index] = new PrometheusMetricLabel(name, value);
        }

        Array.Sort(
            output,
            static (left, right) =>
                StringComparer.Ordinal.Compare(left.Name, right.Name));
        for (var index = 1; index < output.Length; index++)
        {
            if (output[index - 1].Name == output[index].Name)
            {
                return false;
            }
        }

        labels = output;
        return true;
    }

    private static string BuildSeriesKey(
        PrometheusInstrumentBinding binding,
        IReadOnlyList<PrometheusMetricLabel> labels)
    {
        var key = new System.Text.StringBuilder(
            binding.MetricName.Length + labels.Count * 80);
        key.Append(binding.MetricName);
        key.Append('|');
        key.Append((byte)binding.Kind);
        foreach (var label in labels)
        {
            key.Append('|');
            key.Append(label.Name);
            key.Append('=');
            key.Append(label.Value);
        }
        return key.ToString();
    }

    private static void ApplyMeasurement(
        PrometheusSeries series,
        double value,
        bool isObservable)
    {
        switch (series.Kind)
        {
            case MetricAggregationKind.Counter:
            case MetricAggregationKind.UpDownCounter:
                if (isObservable)
                {
                    series.Value = value;
                }
                else
                {
                    series.Value += value;
                }
                break;
            case MetricAggregationKind.Gauge:
                series.Value = value;
                break;
            case MetricAggregationKind.Histogram:
                series.Count++;
                series.Sum += value;
                var bounds =
                    PrometheusHistogramPolicy.MillisecondBounds;
                for (var index = 0; index < bounds.Length; index++)
                {
                    if (value <= bounds[index])
                    {
                        series.HistogramBuckets[index]++;
                    }
                }
                series.HistogramBuckets[^1]++;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(series));
        }
    }

    private void RemoveStaleObservableSeriesLocked(long epoch)
    {
        List<string>? stale = null;
        foreach (var pair in _series)
        {
            if (pair.Value.IsObservable &&
                pair.Value.LastObservedEpoch != epoch)
            {
                stale ??= new List<string>();
                stale.Add(pair.Key);
            }
        }

        if (stale is null)
        {
            return;
        }
        foreach (var key in stale)
        {
            _series.Remove(key);
        }
    }

    private PrometheusRenderSnapshot CaptureRenderSnapshotLocked()
    {
        var snapshots =
            ImmutableArray.CreateBuilder<PrometheusSeriesSnapshot>(
                _series.Count);
        foreach (var series in _series.Values)
        {
            snapshots.Add(new PrometheusSeriesSnapshot(
                series.Key,
                series.MetricName,
                series.Kind,
                ImmutableArray.CreateRange(series.Labels),
                series.Value,
                series.Count,
                series.Sum,
                ImmutableArray.CreateRange(
                    series.HistogramBuckets)));
        }

        return new PrometheusRenderSnapshot(
            GetRuntimeSnapshotLocked(),
            snapshots.MoveToImmutable());
    }

    private PrometheusCollectorRuntimeSnapshot GetRuntimeSnapshotLocked() =>
        new(
            _bindings.Count,
            _series.Count,
            Interlocked.Read(ref _measurements),
            Interlocked.Read(ref _droppedInstruments),
            Interlocked.Read(ref _droppedSeries),
            Interlocked.Read(ref _droppedTags),
            Interlocked.Read(ref _droppedMeasurements),
            Interlocked.Read(ref _truncatedSnapshots));

    private static bool TryGetAggregationKind(
        Instrument instrument,
        out MetricAggregationKind kind)
    {
        var name = instrument.GetType().Name;
        if (name.StartsWith(
                "ObservableGauge",
                StringComparison.Ordinal) ||
            name.StartsWith("Gauge", StringComparison.Ordinal))
        {
            kind = MetricAggregationKind.Gauge;
            return true;
        }
        if (name.StartsWith(
                "ObservableUpDownCounter",
                StringComparison.Ordinal) ||
            name.StartsWith("UpDownCounter", StringComparison.Ordinal))
        {
            kind = MetricAggregationKind.UpDownCounter;
            return true;
        }
        if (name.StartsWith(
                "ObservableCounter",
                StringComparison.Ordinal) ||
            name.StartsWith("Counter", StringComparison.Ordinal))
        {
            kind = MetricAggregationKind.Counter;
            return true;
        }
        if (name.StartsWith("Histogram", StringComparison.Ordinal))
        {
            kind = MetricAggregationKind.Histogram;
            return true;
        }

        kind = default;
        return false;
    }
}
