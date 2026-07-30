namespace Godswar.Server.Application.Characters;

internal sealed partial class CharacterCheckpointCoordinator
{
    public CharacterCheckpointEnqueueResult TryEnqueue(
        CharacterPositionCheckpoint checkpoint)
    {
        checkpoint.Validate();
        return TryEnqueue(CheckpointWorkItem.From(checkpoint));
    }

    public CharacterCheckpointEnqueueResult TryEnqueue(
        CharacterVitalsCheckpoint checkpoint)
    {
        checkpoint.Validate();
        return TryEnqueue(CheckpointWorkItem.From(checkpoint));
    }

    private CharacterCheckpointEnqueueResult TryEnqueue(
        CheckpointWorkItem item)
    {
        CharacterCheckpointEnqueueResult result;
        lock (_sync)
        {
            if (_state != CharacterCheckpointRuntimeState.Ready)
            {
                result = new(
                    CharacterCheckpointEnqueueStatus.NotReady,
                    null);
            }
            else if (_pending.TryGetValue(
                         item.Key,
                         out var existing))
            {
                result = CoalesceLocked(existing, item);
            }
            else if (_pending.Count >= _options.QueueCapacity)
            {
                result = new(
                    CharacterCheckpointEnqueueStatus.Saturated,
                    null);
            }
            else
            {
                var entry = new PendingEntry(
                    item,
                    _timeProvider.GetUtcNow());
                _pending.Add(item.Key, entry);
                QueueLocked(item.Key, entry);
                result = new(
                    CharacterCheckpointEnqueueStatus.Accepted,
                    item.Revision);
            }

            TouchHeartbeatLocked();
        }

        _metrics.RecordEnqueue(item.Facet, result.Status);
        return result;
    }

    private CharacterCheckpointEnqueueResult CoalesceLocked(
        PendingEntry entry,
        CheckpointWorkItem item)
    {
        var current = entry.Latest;
        if (item.Owner.Generation < current.Owner.Generation ||
            item.Owner.Generation == current.Owner.Generation &&
            item.Owner.OwnerId != current.Owner.OwnerId)
        {
            return new(
                CharacterCheckpointEnqueueStatus.OwnershipLost,
                current.Revision);
        }

        if (item.Owner.Generation == current.Owner.Generation)
        {
            if (item.Revision < current.Revision)
            {
                return new(
                    CharacterCheckpointEnqueueStatus.IgnoredStale,
                    current.Revision);
            }
            if (item.Revision == current.Revision)
            {
                return new(
                    item.HasSameValue(current)
                        ? CharacterCheckpointEnqueueStatus.IgnoredStale
                        : CharacterCheckpointEnqueueStatus
                            .RevisionConflict,
                    current.Revision);
            }
        }
        else
        {
            entry.FirstEnqueuedAt = _timeProvider.GetUtcNow();
            entry.FailureCount = 0;
        }

        entry.Latest = item;
        entry.Invalidated = false;
        if (!entry.Active &&
            !entry.Queued &&
            !entry.RetryScheduled)
        {
            QueueLocked(item.Key, entry);
        }
        return new(
            CharacterCheckpointEnqueueStatus.Coalesced,
            item.Revision);
    }

    private void QueueLocked(
        CheckpointKey key,
        PendingEntry entry)
    {
        if (entry.Queued)
        {
            return;
        }

        entry.Queued = true;
        if (!_queue.Writer.TryWrite(key))
        {
            entry.Queued = false;
            throw new InvalidOperationException(
                "A reserved checkpoint queue slot could not be written.");
        }
    }

    private void RemoveSatisfiedPending(
        CheckpointWorkItem completed,
        CharacterCheckpointWriteResult result)
    {
        if (!result.Satisfies(completed.Revision))
        {
            return;
        }

        lock (_sync)
        {
            if (_pending.TryGetValue(
                    completed.Key,
                    out var entry) &&
                entry.Latest.Owner == completed.Owner &&
                result.StoredRevision is { } stored &&
                entry.Latest.Revision <= stored)
            {
                InvalidateOrRemoveLocked(completed.Key, entry);
            }
            TouchHeartbeatLocked();
            TryCompleteQueueIfDrainedLocked();
        }
    }

    private void RemoveOwnerPending(
        int accountId,
        int characterId,
        CharacterCheckpointOwner owner)
    {
        lock (_sync)
        {
            foreach (var pair in _pending
                         .Where(pair =>
                             pair.Key.AccountId == accountId &&
                             pair.Key.CharacterId == characterId &&
                             pair.Value.Latest.Owner == owner)
                         .ToArray())
            {
                InvalidateOrRemoveLocked(pair.Key, pair.Value);
            }
            TouchHeartbeatLocked();
            TryCompleteQueueIfDrainedLocked();
        }
    }

    private void RemoveOtherOwnerPending(
        int accountId,
        int characterId,
        CharacterCheckpointOwner owner)
    {
        lock (_sync)
        {
            foreach (var pair in _pending
                         .Where(pair =>
                             pair.Key.AccountId == accountId &&
                             pair.Key.CharacterId == characterId &&
                             pair.Value.Latest.Owner != owner)
                         .ToArray())
            {
                InvalidateOrRemoveLocked(pair.Key, pair.Value);
            }
            TouchHeartbeatLocked();
            TryCompleteQueueIfDrainedLocked();
        }
    }

    private void InvalidateOrRemoveLocked(
        CheckpointKey key,
        PendingEntry entry)
    {
        if (entry.Queued || entry.RetryScheduled)
        {
            entry.Invalidated = true;
            return;
        }

        _pending.Remove(key);
    }
}
