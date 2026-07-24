namespace Godswar.Server.Networking;

internal readonly record struct BoundedByteQueueEntry<T>(T Item, int ByteCount)
    where T : class;

internal readonly record struct BoundedByteQueueSnapshot(
    int CapacityItems,
    long CapacityBytes,
    int CurrentItems,
    long CurrentBytes,
    int HighWaterItems,
    long HighWaterBytes,
    int WaitingProducers,
    int WaitingConsumers,
    bool IsCompleted);

internal readonly struct DequeueResult<T>
    where T : class
{
    private readonly T? _item;

    private DequeueResult(T item, int byteCount)
    {
        _item = item;
        ByteCount = byteCount;
        HasItem = true;
    }

    public bool HasItem { get; }

    public T Item => HasItem
        ? _item!
        : throw new InvalidOperationException("A completed dequeue result has no item.");

    public int ByteCount { get; }

    public static DequeueResult<T> Completed => default;

    internal static DequeueResult<T> FromEntry(BoundedByteQueueEntry<T> entry)
    {
        return new DequeueResult<T>(entry.Item, entry.ByteCount);
    }
}

internal sealed class BoundedByteQueueCompletedException : InvalidOperationException
{
    public BoundedByteQueueCompletedException()
        : base("The bounded byte queue has completed.")
    {
    }
}
