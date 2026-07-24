using System.Threading.Channels;
using Godswar.Server.Networking;

namespace Godswar.Server.ProtocolChecks;

internal sealed class ControlledLegacyByteTransport : ILegacyByteTransport
{
    private readonly CancellationTokenSource _disconnected = new();
    private readonly Channel<byte[]> _inbound = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly object _writeSync = new();
    private readonly MemoryStream _written = new();
    private readonly TaskCompletionSource _writeGate =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _writeStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private byte[]? _currentInbound;
    private int _currentInboundOffset;
    private int _activeWrites;
    private int _disconnectCount;
    private int _disposeStarted;
    private int _maximumConcurrentWrites;
    private int _readCallCount;
    private int _writeCount;

    public ControlledLegacyByteTransport(bool blockWrites = false)
    {
        if (!blockWrites)
        {
            _writeGate.TrySetResult();
        }
    }

    public string RemoteEndPoint => "controlled:1234";

    public int DisconnectCount => Volatile.Read(ref _disconnectCount);

    public int MaximumConcurrentWrites =>
        Volatile.Read(ref _maximumConcurrentWrites);

    public int ReadCallCount => Volatile.Read(ref _readCallCount);

    public int WriteCount => Volatile.Read(ref _writeCount);

    public Task WriteStarted => _writeStarted.Task;

    public byte[] WrittenBytes
    {
        get
        {
            lock (_writeSync)
            {
                return _written.ToArray();
            }
        }
    }

    public void QueueInbound(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new ArgumentException(
                "A controlled inbound chunk must contain at least one byte.",
                nameof(bytes));
        }

        if (!_inbound.Writer.TryWrite(bytes.ToArray()))
        {
            throw new InvalidOperationException(
                "The controlled inbound stream is already complete.");
        }
    }

    public void CompleteInbound()
    {
        _inbound.Writer.TryComplete();
    }

    public void ReleaseWrites()
    {
        _writeGate.TrySetResult();
    }

    public async Task WaitForReadCallsAsync(
        int expectedCount,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (ReadCallCount < expectedCount)
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Only {ReadCallCount} of {expectedCount} transport reads started.");
            }

            await Task.Delay(1);
        }
    }

    public async ValueTask<int> ReadAsync(
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _readCallCount);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disconnected.Token);

        while (_currentInbound is null
               || _currentInboundOffset >= _currentInbound.Length)
        {
            if (!await _inbound.Reader.WaitToReadAsync(linked.Token))
            {
                return 0;
            }

            if (_inbound.Reader.TryRead(out var chunk))
            {
                _currentInbound = chunk;
                _currentInboundOffset = 0;
            }
        }

        var count = Math.Min(
            destination.Length,
            _currentInbound.Length - _currentInboundOffset);
        _currentInbound.AsMemory(_currentInboundOffset, count)
            .CopyTo(destination);
        _currentInboundOffset += count;
        return count;
    }

    public async ValueTask WriteAsync(
        ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken)
    {
        var activeWrites = Interlocked.Increment(ref _activeWrites);
        UpdateMaximumConcurrentWrites(activeWrites);
        _writeStarted.TrySetResult();

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disconnected.Token);
            await _writeGate.Task.WaitAsync(linked.Token);
            var copy = source.ToArray();
            lock (_writeSync)
            {
                _written.Write(copy);
            }

            Interlocked.Increment(ref _writeCount);
        }
        finally
        {
            Interlocked.Decrement(ref _activeWrites);
        }
    }

    public void Disconnect()
    {
        if (Interlocked.CompareExchange(ref _disconnectCount, 1, 0) != 0)
        {
            return;
        }

        _disconnected.Cancel();
        _inbound.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 0)
        {
            Disconnect();
            _disconnected.Dispose();
            _written.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void UpdateMaximumConcurrentWrites(int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maximumConcurrentWrites);
            if (candidate <= current
                || Interlocked.CompareExchange(
                    ref _maximumConcurrentWrites,
                    candidate,
                    current) == current)
            {
                return;
            }
        }
    }
}
