namespace Godswar.Server.Networking;

internal sealed partial class BoundedByteQueue<T>
    where T : class
{
#if DEBUG
    private Action? _protocolCheckAfterBatchCommit;

    internal void ProtocolCheckFailNextBatchAfterCommit()
    {
        _protocolCheckAfterBatchCommit = () =>
            throw new InvalidOperationException(
                "simulated exact batch post-commit failure");
    }
#endif

    /// <summary>
    /// Attempts to admit a complete ordered batch without waiting. Earlier
    /// waiting producers are never bypassed. A false result leaves the queue
    /// unchanged.
    /// </summary>
    public bool TryEnqueueBatch(
        IReadOnlyList<BoundedByteQueueEntry<T>> entries,
        out Exception? postCommitError)
    {
        ArgumentNullException.ThrowIfNull(entries);
        postCommitError = null;
        if (entries.Count == 0)
        {
            return true;
        }

        long totalBytes = 0;
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry.Item);
            ArgumentOutOfRangeException.ThrowIfNegative(entry.ByteCount);
            if (entry.ByteCount > _capacityBytes ||
                totalBytes > _capacityBytes - entry.ByteCount)
            {
                return false;
            }

            totalBytes += entry.ByteCount;
        }

        lock (_gate)
        {
            if (_isSealed ||
                _waitingProducers.Count != 0 ||
                entries.Count > _capacityItems - _items.Count ||
                _currentBytes > _capacityBytes - totalBytes)
            {
                return false;
            }

            foreach (var entry in entries)
            {
                EnqueueItemLocked(entry);
            }
            try
            {
#if DEBUG
                Interlocked.Exchange(
                    ref _protocolCheckAfterBatchCommit,
                    null)?.Invoke();
#endif
                PumpLocked();
            }
            catch (Exception error)
            {
                // Appending the fully validated batch is the ownership
                // commit. Seal under the same queue lock so no producer can
                // race into the failed epoch before the owner finalizes it.
                _isSealed = true;
                _completionError ??= error;
                postCommitError = error;
            }
            return true;
        }
    }

    internal void Seal(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_gate)
        {
            if (_isCompleted)
            {
                return;
            }

            _isSealed = true;
            _completionError ??= error;
        }
    }

    internal bool TryTakeTerminalEntry(
        out BoundedByteQueueEntry<T> entry)
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                entry = default;
                return false;
            }

            entry = DequeueEntryLocked();
            return true;
        }
    }
}
