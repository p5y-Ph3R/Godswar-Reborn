namespace Godswar.Server.Networking.Secure;

internal sealed class TlsHandshakeGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore;
    private int _disposed;

    public TlsHandshakeGate(int maximumConcurrentHandshakes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maximumConcurrentHandshakes);
        _semaphore = new SemaphoreSlim(
            maximumConcurrentHandshakes,
            maximumConcurrentHandshakes);
    }

    internal int AvailableSlots => _semaphore.CurrentCount;

    public async ValueTask<IDisposable> AcquireAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        await _semaphore.WaitAsync(cancellationToken);
        return new Lease(this);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _semaphore.Dispose();
        }
    }

    private void Release()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _semaphore.Release();
        }
    }

    private sealed class Lease(TlsHandshakeGate owner) : IDisposable
    {
        private TlsHandshakeGate? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
