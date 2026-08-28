using Godswar.Server.Networking;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
#if DEBUG
    private sealed class SwitchableMedusaTransport
        : ILegacyByteTransport
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly object _sync = new();
        private readonly MemoryStream _written = new();
        private TaskCompletionSource _writeGate = CompletedGate();
        private TaskCompletionSource _writeStarted = NewGate();
        private int _disconnected;
        private int _writeCount;
        private Exception? _nextWriteFailure;

        public string RemoteEndPoint => "medusa-controlled:1";

        public int WriteCount => Volatile.Read(ref _writeCount);

        public bool IsDisconnected =>
            Volatile.Read(ref _disconnected) != 0;

        public Task WriteStarted
        {
            get
            {
                lock (_sync)
                {
                    return _writeStarted.Task;
                }
            }
        }

        public byte[] WrittenBytes
        {
            get
            {
                lock (_sync)
                {
                    return _written.ToArray();
                }
            }
        }

        public void BlockWrites()
        {
            lock (_sync)
            {
                _writeGate = NewGate();
                _writeStarted = NewGate();
            }
        }

        public void ReleaseWrites()
        {
            TaskCompletionSource gate;
            lock (_sync)
            {
                gate = _writeGate;
            }
            gate.TrySetResult();
        }

        public void FailBlockedWrite(Exception error)
        {
            ArgumentNullException.ThrowIfNull(error);
            Interlocked.Exchange(ref _nextWriteFailure, error);
            ReleaseWrites();
        }

        public ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);

        public async ValueTask WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken)
        {
            Task gate;
            TaskCompletionSource started;
            lock (_sync)
            {
                gate = _writeGate.Task;
                started = _writeStarted;
            }
            started.TrySetResult();
            using var linked = CancellationTokenSource
                .CreateLinkedTokenSource(
                    cancellationToken,
                    _lifetime.Token);
            await gate.WaitAsync(linked.Token);
            if (Interlocked.Exchange(
                    ref _nextWriteFailure,
                    null) is { } failure)
            {
                throw failure;
            }
            lock (_sync)
            {
                _written.Write(source.Span);
            }
            Interlocked.Increment(ref _writeCount);
        }

        public void Disconnect()
        {
            if (Interlocked.Exchange(ref _disconnected, 1) == 0)
            {
                _lifetime.Cancel();
            }
        }

        public ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
            return ValueTask.CompletedTask;
        }

        private static TaskCompletionSource NewGate() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource CompletedGate()
        {
            var gate = NewGate();
            gate.TrySetResult();
            return gate;
        }
    }
#endif
}
