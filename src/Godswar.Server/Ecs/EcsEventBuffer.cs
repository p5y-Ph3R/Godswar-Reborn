namespace Godswar.Server.Ecs;

internal interface IEcsEventStream
{
    void Clear();
}

/// <summary>
/// Per-tick, strongly typed event streams. Events retain publication order.
/// </summary>
internal sealed class EcsEventBuffer
{
    private readonly List<IEcsEventStream> _registeredStreams = [];
    private IEcsEventStream?[] _streamsByType = new IEcsEventStream[8];

    public int RegisteredEventTypeCount => _registeredStreams.Count;

    public void Publish<T>(in T value)
        where T : struct =>
        GetOrCreateStream<T>().Add(value);

    public ReadOnlySpan<T> Read<T>()
        where T : struct
    {
        var typeId = EcsEventType<T>.Id;
        return (uint)typeId < (uint)_streamsByType.Length &&
               _streamsByType[typeId] is EcsEventStream<T> stream
            ? stream.AsSpan()
            : ReadOnlySpan<T>.Empty;
    }

    public int Count<T>()
        where T : struct
    {
        var typeId = EcsEventType<T>.Id;
        return (uint)typeId < (uint)_streamsByType.Length &&
               _streamsByType[typeId] is EcsEventStream<T> stream
            ? stream.Count
            : 0;
    }

    public void Clear()
    {
        foreach (var stream in _registeredStreams)
        {
            stream.Clear();
        }
    }

    private EcsEventStream<T> GetOrCreateStream<T>()
        where T : struct
    {
        var typeId = EcsEventType<T>.Id;
        EnsureStreamCapacity(typeId + 1);

        if (_streamsByType[typeId] is EcsEventStream<T> stream)
        {
            return stream;
        }

        if (_streamsByType[typeId] is not null)
        {
            throw new InvalidOperationException(
                "Two ECS event types were assigned the same internal ID.");
        }

        stream = new EcsEventStream<T>();
        _streamsByType[typeId] = stream;
        _registeredStreams.Add(stream);
        return stream;
    }

    private void EnsureStreamCapacity(int required)
    {
        if (_streamsByType.Length >= required)
        {
            return;
        }

        var newCapacity = Math.Max(required, _streamsByType.Length * 2);
        Array.Resize(ref _streamsByType, newCapacity);
    }

    private sealed class EcsEventStream<T> : IEcsEventStream
        where T : struct
    {
        private T[] _events = new T[4];

        public int Count { get; private set; }

        public void Add(in T value)
        {
            if (Count == _events.Length)
            {
                Array.Resize(ref _events, _events.Length * 2);
            }

            _events[Count++] = value;
        }

        public ReadOnlySpan<T> AsSpan() => _events.AsSpan(0, Count);

        public void Clear()
        {
            Array.Clear(_events, 0, Count);
            Count = 0;
        }
    }
}
