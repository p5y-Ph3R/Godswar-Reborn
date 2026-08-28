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
internal sealed partial class BoundedByteQueue<T>
    where T : class
{
    private readonly object _gate = new();
    private readonly Queue<BoundedByteQueueEntry<T>> _items;
    private readonly LinkedList<PendingEnqueue> _waitingProducers = [];
    private readonly LinkedList<PendingDequeue> _waitingConsumers = [];
    private readonly int _capacityItems;
    private readonly long _capacityBytes;

    private long _currentBytes;
    private int _highWaterItems;
    private long _highWaterBytes;
    private bool _isSealed;
    private bool _isCompleted;
    private Exception? _completionError;

    public BoundedByteQueue(int capacityItems, long capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);

        _capacityItems = capacityItems;
        _capacityBytes = capacityBytes;
        // Batch admission mutates the queue only after every entry has been
        // validated. Reserve the entire authored item capacity up front so
        // Queue<T> cannot grow (and fail) after a batch prefix is inserted.
        _items = new Queue<BoundedByteQueueEntry<T>>(capacityItems);
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
            if (_isSealed)
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

            _isSealed = true;
            _isCompleted = true;
            _completionError ??= error;

            var producerError = CreateProducerCompletionException();
            while (_waitingProducers.First is { } producerNode)
            {
                var settled = producerNode.Value.IsAdmitted
                    ? producerNode.Value.SetResult()
                    : producerNode.Value.SetException(producerError);
                if (!settled)
                {
                    break;
                }
                _waitingProducers.RemoveFirst();
                producerNode.Value.Node = null;
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
                var entry = _items.Peek();
                if (!consumerNode.Value.SetResult(
                        DequeueResult<T>.FromEntry(entry)))
                {
                    return;
                }
                _ = DequeueEntryLocked();
                _waitingConsumers.RemoveFirst();
                consumerNode.Value.Node = null;
            }

            if (_isCompleted)
            {
                if (_items.Count == 0)
                {
                    CompleteWaitingConsumersLocked();
                }

                return;
            }
            if (_isSealed)
            {
                return;
            }

            var producerNode = _waitingProducers.First;
            if (producerNode is null)
            {
                return;
            }
            if (!producerNode.Value.IsAdmitted)
            {
                if (!CanAdmit(producerNode.Value.ByteCount))
                {
                    return;
                }
                EnqueueItemLocked(new BoundedByteQueueEntry<T>(
                    producerNode.Value.Item,
                    producerNode.Value.ByteCount));
                producerNode.Value.IsAdmitted = true;
            }
            if (!producerNode.Value.SetResult())
            {
                return;
            }
            _waitingProducers.RemoveFirst();
            producerNode.Value.Node = null;
        }
    }

    private void CompleteWaitingConsumersLocked()
    {
        while (_waitingConsumers.First is { } consumerNode)
        {
            var completed = _completionError is null
                ? consumerNode.Value.SetResult(
                    DequeueResult<T>.Completed)
                : consumerNode.Value.SetException(_completionError);
            if (!completed)
            {
                return;
            }
            _waitingConsumers.RemoveFirst();
            consumerNode.Value.Node = null;
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

            var settled = pending.IsAdmitted
                ? pending.SetResult()
                : pending.SetCanceled();
            if (settled)
            {
                _waitingProducers.Remove(pending.Node);
                pending.Node = null;
            }
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

            if (pending.SetCanceled())
            {
                _waitingConsumers.Remove(pending.Node);
                pending.Node = null;
            }
        }
    }

}
