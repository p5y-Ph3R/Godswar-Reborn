namespace Godswar.Server.Phase5A;

internal sealed class BoundedPercentileSampler
{
    private readonly double[] _samples;
    private ulong _randomState;
    private long _seen;
    private int _count;

    public BoundedPercentileSampler(int capacity, uint seed)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        if (seed == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seed));
        }

        _samples = new double[capacity];
        _randomState = seed;
    }

    public int Capacity => _samples.Length;

    public int Count => _count;

    public long Seen => _seen;

    public void Add(double value)
    {
        if (!double.IsFinite(value) || value < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        _seen = checked(_seen + 1);
        if (_count < _samples.Length)
        {
            _samples[_count++] = value;
            return;
        }

        var candidate = NextUInt64() % checked((ulong)_seen);
        if (candidate < checked((ulong)_samples.Length))
        {
            _samples[checked((int)candidate)] = value;
        }
    }

    public PercentileSummary Summarize()
    {
        if (_count == 0)
        {
            return new PercentileSummary(
                0,
                0,
                0d,
                0d,
                0d,
                0d,
                0d);
        }

        var sorted = _samples[.._count].ToArray();
        Array.Sort(sorted);
        return new PercentileSummary(
            _seen,
            _count,
            sorted[0],
            Percentile(sorted, 0.50d),
            Percentile(sorted, 0.95d),
            Percentile(sorted, 0.99d),
            sorted[^1]);
    }

    private ulong NextUInt64()
    {
        _randomState ^= _randomState << 13;
        _randomState ^= _randomState >> 7;
        _randomState ^= _randomState << 17;
        return _randomState;
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var rank = percentile * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sorted[lower];
        }

        var weight = rank - lower;
        return sorted[lower] * (1d - weight) +
            sorted[upper] * weight;
    }
}

internal sealed record PercentileSummary(
    long Observations,
    int RetainedSamples,
    double MinimumMilliseconds,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaximumMilliseconds);
