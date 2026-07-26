namespace Godswar.Server.Networking.Secure.Realtime;

internal enum SecureRealtimeMailboxOfferStatus : byte
{
    Accepted = 1,
    Replaced = 2,
    Disposed = 3
}

internal readonly record struct SecureRealtimeMailboxSnapshot(
    bool HasItem,
    bool IsDisposed,
    ulong Accepted,
    ulong Replaced,
    ulong Taken);

internal sealed class SecureRealtimeSingleSlot<T> : IDisposable
    where T : struct
{
    private readonly object _gate = new();
    private T _item;
    private bool _hasItem;
    private bool _disposed;
    private ulong _accepted;
    private ulong _replaced;
    private ulong _taken;

    public SecureRealtimeMailboxOfferStatus Offer(in T item)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return SecureRealtimeMailboxOfferStatus.Disposed;
            }

            var replaced = _hasItem;
            _item = item;
            _hasItem = true;
            _accepted = IncrementSaturating(_accepted);
            if (replaced)
            {
                _replaced = IncrementSaturating(_replaced);
            }

            return replaced
                ? SecureRealtimeMailboxOfferStatus.Replaced
                : SecureRealtimeMailboxOfferStatus.Accepted;
        }
    }

    public bool TryTake(out T item)
    {
        lock (_gate)
        {
            if (_disposed || !_hasItem)
            {
                item = default;
                return false;
            }

            item = _item;
            _item = default;
            _hasItem = false;
            _taken = IncrementSaturating(_taken);
            return true;
        }
    }

    public SecureRealtimeMailboxSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new SecureRealtimeMailboxSnapshot(
                _hasItem,
                _disposed,
                _accepted,
                _replaced,
                _taken);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _item = default;
            _hasItem = false;
            _disposed = true;
        }
    }

    internal static ulong IncrementSaturating(ulong value) =>
        value == ulong.MaxValue ? value : value + 1;
}
