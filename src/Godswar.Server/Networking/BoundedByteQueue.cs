namespace Godswar.Server.Networking;

/// <summary>
/// A FIFO queue whose admitted contents are bounded by both item count and
/// caller-supplied byte cost.
/// </summary>
/// <remarks>
/// Producers wait instead of dropping reliable data. Waiting producers are
/// admitted in FIFO order, so a smaller item never bypasses an earlier item.
/// Call <see cref="Complete"/> before <see cref="TryDrain"/> when draining a
/// queue during shutdown; otherwise newly available capacity may admit
/// waiting producers after the current contents are removed.
/// </remarks>
internal sealed class BoundedByteQueue<T>
    where T : class
{
    private readonly object _gate = new();
    private readonly Queue<BoundedByteQueueEntry<T>> _items = [];
    private readonly LinkedList<PendingEnqueue> _waitingProducers = [];
    private readonly LinkedList<PendingDequeue> _waitingConsumers = [];
    private readonly int _capacityItems;
    private readonly long _capacityBytes;

    private long _currentBytes;
    private int _highWaterItems;
    private long _highWaterBytes;
    private bool _isCompleted;
    private Exception? _completionError;

    public BoundedByteQueue(int capacityItems, long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);

        _capacityItems = capacityItems;
        _capacityBytes = capacityBytes;
    }

    /// <summary>
    /// Enqueues an item once both its item and byte capacity are available.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="item"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="byteCount"/> is negative or exceeds the entire queue
    /// byte capacity.
    /// </exception>
    /// <exception cref="BoundedByteQueueCompletedException">
    /// The queue completed without an error before the item was admitted.
    /// </exception>
    /// <remarks>
    /// Cancellation only cancels an item that is still waiting for admission.
    /// Once this operation succeeds, the item is owned by the queue.
    /// </remarks>
    public ValueTask EnqueueAsync(
        T item,
        int byteCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);

        if (byteCount > _capacityBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteCount),
                byteCount,
                $"An item cannot exceed the queue byte capacity of {_capacityBytes}.");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(cancellationToken);
        }

        PendingEnqueue? pending = null;
        Exception? completionError = null;

        lock (_gate)
        {
            if (_isCompleted)
            {
                completionError = CreateProducerCompletionException();
            }
            else if (_waitingProducers.Count == 0 && CanAdmit(byteCount))
            {
                EnqueueItemLocked(new BoundedByteQueueEntry<T>(item, byteCount));
                PumpLocked();
            }
            else
            {
                pending = new PendingEnqueue(this, item, byteCount, cancellationToken);
                pending.Node = _waitingProducers.AddLast(pending);
                PumpLocked();
            }
        }

        if (completionError is not null)
        {
            return ValueTask.FromException(completionError);
        }

        if (pending is null)
        {
            return ValueTask.CompletedTask;
        }

        pending.RegisterCancellation();
        return new ValueTask(pending.Task);
    }

    /// <summary>
    /// Dequeues the oldest admitted item, waiting if the live queue is empty.
    /// </summary>
    /// <remarks>
    /// Normal completion is returned as a result with
    /// <see cref="DequeueResult{T}.HasItem"/> set to <see langword="false"/>.
    /// If the queue completed with an error, that exact exception is surfaced
    /// only after every previously admitted item has been dequeued.
    /// </remarks>
    public ValueTask<DequeueResult<T>> DequeueAsync(
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<DequeueResult<T>>(cancellationToken);
        }

        PendingDequeue? pending = null;
        DequeueResult<T> immediateResult = default;
        Exception? completionError = null;
        var hasImmediateResult = false;

        lock (_gate)
        {
            if (_items.Count > 0)
            {
                immediateResult = DequeueItemLocked();
                hasImmediateResult = true;
                PumpLocked();
            }
            else if (_isCompleted)
            {
                if (_completionError is null)
                {
                    immediateResult = DequeueResult<T>.Completed;
                    hasImmediateResult = true;
                }
                else
                {
                    completionError = _completionError;
                }
            }
            else
            {
                pending = new PendingDequeue(this, cancellationToken);
                pending.Node = _waitingConsumers.AddLast(pending);
            }
        }

        if (completionError is not null)
        {
            return ValueTask.FromException<DequeueResult<T>>(completionError);
        }

        if (hasImmediateResult)
        {
            return ValueTask.FromResult(immediateResult);
        }

        pending!.RegisterCancellation();
        return new ValueTask<DequeueResult<T>>(pending.Task);
    }

    /// <summary>
    /// Stops new admissions and wakes every waiter.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> for the first completion; otherwise
    /// <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Previously admitted items remain available in FIFO order. A supplied
    /// error is surfaced to consumers only after those items drain.
    /// </remarks>
    public bool Complete(Exception? error = null)
    {
        lock (_gate)
        {
            if (_isCompleted)
            {
                return false;
            }

            _isCompleted = true;
            _completionError = error;

            var producerError = CreateProducerCompletionException();
            while (_waitingProducers.First is { } producerNode)
            {
                _waitingProducers.RemoveFirst();
                producerNode.Value.Node = null;
                producerNode.Value.SetException(producerError);
            }

            PumpLocked();
            return true;
        }
    }

    /// <summary>
    /// Atomically removes and returns the contents admitted at the start of
    /// this call.
    /// </summary>
    public IReadOnlyList<BoundedByteQueueEntry<T>> TryDrain()
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                PumpLocked();
                return Array.Empty<BoundedByteQueueEntry<T>>();
            }

            var drained = new BoundedByteQueueEntry<T>[_items.Count];
            for (var index = 0; index < drained.Length; index++)
            {
                drained[index] = DequeueEntryLocked();
            }

            PumpLocked();
            return drained;
        }
    }

    /// <summary>
    /// Captures capacity, current accounting, waiters, and lifetime high-water
    /// marks under one lock.
    /// </summary>
    public BoundedByteQueueSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new BoundedByteQueueSnapshot(
                CapacityItems: _capacityItems,
                CapacityBytes: _capacityBytes,
                CurrentItems: _items.Count,
                CurrentBytes: _currentBytes,
                HighWaterItems: _highWaterItems,
                HighWaterBytes: _highWaterBytes,
                WaitingProducers: _waitingProducers.Count,
                WaitingConsumers: _waitingConsumers.Count,
                IsCompleted: _isCompleted);
        }
    }

    private bool CanAdmit(int byteCount)
    {
        return _items.Count < _capacityItems
            && _currentBytes <= _capacityBytes - byteCount;
    }

    private void EnqueueItemLocked(BoundedByteQueueEntry<T> entry)
    {
        _items.Enqueue(entry);
        _currentBytes += entry.ByteCount;
        _highWaterItems = Math.Max(_highWaterItems, _items.Count);
        _highWaterBytes = Math.Max(_highWaterBytes, _currentBytes);
    }

    private DequeueResult<T> DequeueItemLocked()
    {
        var entry = DequeueEntryLocked();
        return DequeueResult<T>.FromEntry(entry);
    }

    private BoundedByteQueueEntry<T> DequeueEntryLocked()
    {
        var entry = _items.Dequeue();
        _currentBytes -= entry.ByteCount;
        return entry;
    }

    private void PumpLocked()
    {
        while (true)
        {
            while (_items.Count > 0 && _waitingConsumers.First is { } consumerNode)
            {
                _waitingConsumers.RemoveFirst();
                consumerNode.Value.Node = null;
                consumerNode.Value.SetResult(DequeueItemLocked());
            }

            if (_isCompleted)
            {
                if (_items.Count == 0)
                {
                    CompleteWaitingConsumersLocked();
                }

                return;
            }

            var producerNode = _waitingProducers.First;
            if (producerNode is null || !CanAdmit(producerNode.Value.ByteCount))
            {
                return;
            }

            _waitingProducers.RemoveFirst();
            producerNode.Value.Node = null;
            EnqueueItemLocked(new BoundedByteQueueEntry<T>(
                producerNode.Value.Item,
                producerNode.Value.ByteCount));
            producerNode.Value.SetResult();
        }
    }

    private void CompleteWaitingConsumersLocked()
    {
        while (_waitingConsumers.First is { } consumerNode)
        {
            _waitingConsumers.RemoveFirst();
            consumerNode.Value.Node = null;

            if (_completionError is null)
            {
                consumerNode.Value.SetResult(DequeueResult<T>.Completed);
            }
            else
            {
                consumerNode.Value.SetException(_completionError);
            }
        }
    }

    private Exception CreateProducerCompletionException()
    {
        return _completionError
            ?? new BoundedByteQueueCompletedException();
    }

    private void Cancel(PendingEnqueue pending)
    {
        lock (_gate)
        {
            if (pending.Node is null)
            {
                return;
            }

            _waitingProducers.Remove(pending.Node);
            pending.Node = null;
            pending.SetCanceled();
            PumpLocked();
        }
    }

    private void Cancel(PendingDequeue pending)
    {
        lock (_gate)
        {
            if (pending.Node is null)
            {
                return;
            }

            _waitingConsumers.Remove(pending.Node);
            pending.Node = null;
            pending.SetCanceled();
        }
    }

    private abstract class PendingOperation
    {
        private readonly object _registrationGate = new();
        private CancellationTokenRegistration _registration;
        private bool _hasRegistration;
        private bool _isFinished;

        protected PendingOperation(CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
        }

        protected CancellationToken CancellationToken { get; }

        protected void RegisterCancellation(object state, Action<object?> callback)
        {
            if (!CancellationToken.CanBeCanceled)
            {
                return;
            }

            var registration = CancellationToken.UnsafeRegister(callback, state);
            var unregister = false;

            lock (_registrationGate)
            {
                if (_isFinished)
                {
                    unregister = true;
                }
                else
                {
                    _registration = registration;
                    _hasRegistration = true;
                }
            }

            if (unregister)
            {
                registration.Unregister();
            }
        }

        protected void Finish()
        {
            CancellationTokenRegistration registration = default;
            var unregister = false;

            lock (_registrationGate)
            {
                _isFinished = true;
                if (_hasRegistration)
                {
                    registration = _registration;
                    _hasRegistration = false;
                    unregister = true;
                }
            }

            if (unregister)
            {
                registration.Unregister();
            }
        }
    }

    private sealed class PendingEnqueue : PendingOperation
    {
        private readonly BoundedByteQueue<T> _owner;
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingEnqueue(
            BoundedByteQueue<T> owner,
            T item,
            int byteCount,
            CancellationToken cancellationToken)
            : base(cancellationToken)
        {
            _owner = owner;
            Item = item;
            ByteCount = byteCount;
        }

        public T Item { get; }

        public int ByteCount { get; }

        public LinkedListNode<PendingEnqueue>? Node { get; set; }

        public Task Task => _completion.Task;

        public void RegisterCancellation()
        {
            RegisterCancellation(
                this,
                static state =>
                {
                    var pending = (PendingEnqueue)state!;
                    pending._owner.Cancel(pending);
                });
        }

        public void SetResult()
        {
            Finish();
            _completion.TrySetResult();
        }

        public void SetCanceled()
        {
            Finish();
            _completion.TrySetCanceled(CancellationToken);
        }

        public void SetException(Exception error)
        {
            Finish();
            _completion.TrySetException(error);
        }
    }

    private sealed class PendingDequeue : PendingOperation
    {
        private readonly BoundedByteQueue<T> _owner;
        private readonly TaskCompletionSource<DequeueResult<T>> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PendingDequeue(
            BoundedByteQueue<T> owner,
            CancellationToken cancellationToken)
            : base(cancellationToken)
        {
            _owner = owner;
        }

        public LinkedListNode<PendingDequeue>? Node { get; set; }

        public Task<DequeueResult<T>> Task => _completion.Task;

        public void RegisterCancellation()
        {
            RegisterCancellation(
                this,
                static state =>
                {
                    var pending = (PendingDequeue)state!;
                    pending._owner.Cancel(pending);
                });
        }

        public void SetResult(DequeueResult<T> result)
        {
            Finish();
            _completion.TrySetResult(result);
        }

        public void SetCanceled()
        {
            Finish();
            _completion.TrySetCanceled(CancellationToken);
        }

        public void SetException(Exception error)
        {
            Finish();
            _completion.TrySetException(error);
        }
    }
}
