using System.Data.Common;
using System.Diagnostics;

namespace Godswar.Server.Application.Characters;

internal sealed partial class CharacterCheckpointCoordinator
{
    private async Task RunWorkerAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            while (await WaitToReadWithHeartbeatAsync(
                       cancellationToken))
            {
                while (_queue.Reader.TryRead(out var key))
                {
                    CheckpointWorkItem work;
                    PendingEntry entry;
                    lock (_sync)
                    {
                        if (!_pending.TryGetValue(key, out entry!))
                        {
                            TouchHeartbeatLocked();
                            continue;
                        }

                        entry.Queued = false;
                        if (entry.Invalidated)
                        {
                            _pending.Remove(key);
                            TouchHeartbeatLocked();
                            TryCompleteQueueIfDrainedLocked();
                            continue;
                        }
                        if (entry.Active || entry.RetryScheduled)
                        {
                            continue;
                        }

                        entry.Active = true;
                        _activeWrites++;
                        work = entry.Latest;
                        TouchHeartbeatLocked();
                    }

                    await ProcessAsync(
                        key,
                        entry,
                        work,
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            ReportAsynchronousFault(error);
            throw;
        }
    }

    private async Task ProcessAsync(
        CheckpointKey key,
        PendingEntry entry,
        CheckpointWorkItem work,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await WriteOnceAsync(
                work,
                cancellationToken);
            ValidateWriteResult(work, result);
            if (result.Status ==
                    CharacterCheckpointWriteStatus.RevisionConflict)
            {
                throw new InvalidOperationException(
                    $"The {work.Facet.ToMetricTag()} checkpoint " +
                    $"{work.Revision} conflicts with stored data.");
            }

            lock (_sync)
            {
                CompleteActiveLocked(entry);
                if (!_pending.TryGetValue(key, out var current) ||
                    !ReferenceEquals(current, entry))
                {
                    TryCompleteQueueIfDrainedLocked();
                    return;
                }

                if (entry.Latest.Owner != work.Owner ||
                    entry.Latest.Revision > work.Revision)
                {
                    entry.FailureCount = 0;
                    QueueLocked(key, entry);
                }
                else
                {
                    _pending.Remove(key);
                }
                TouchHeartbeatLocked();
                TryCompleteQueueIfDrainedLocked();
            }
        }
        catch (Exception error)
            when (IsTransient(error) &&
                !cancellationToken.IsCancellationRequested)
        {
            ScheduleRetryOrThrow(
                key,
                entry,
                work,
                error,
                cancellationToken);
        }
        catch
        {
            lock (_sync)
            {
                CompleteActiveLocked(entry);
                TouchHeartbeatLocked();
            }
            throw;
        }
    }

    private async Task<CharacterCheckpointWriteResult> WriteOnceAsync(
        CheckpointWorkItem work,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var deadline = new CancellationTokenSource(
            _options.CommandTimeout,
            _timeProvider);
        using var lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);
        try
        {
            var result = work.Facet switch
            {
                CharacterCheckpointFacet.Position =>
                    await _store.WritePositionAsync(
                        work.Position,
                        lifetime.Token),
                CharacterCheckpointFacet.Vitals =>
                    await _store.WriteVitalsAsync(
                        work.Vitals,
                        lifetime.Token),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(work))
            };
            _metrics.RecordWrite(
                work.Facet,
                result.Status,
                Stopwatch.GetElapsedTime(started));
            return result;
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
        {
            var timeout = new TimeoutException(
                "The checkpoint store command exceeded its deadline.");
            _metrics.RecordFailure(
                work.Facet,
                "timeout",
                Stopwatch.GetElapsedTime(started));
            throw timeout;
        }
        catch
        {
            _metrics.RecordFailure(
                work.Facet,
                "failure",
                Stopwatch.GetElapsedTime(started));
            throw;
        }
    }

    private void ScheduleRetryOrThrow(
        CheckpointKey key,
        PendingEntry entry,
        CheckpointWorkItem work,
        Exception error,
        CancellationToken cancellationToken)
    {
        TimeSpan delay;
        lock (_sync)
        {
            CompleteActiveLocked(entry);
            if (!_pending.TryGetValue(key, out var current) ||
                !ReferenceEquals(current, entry))
            {
                TryCompleteQueueIfDrainedLocked();
                return;
            }

            var age = NonNegative(
                _timeProvider.GetUtcNow() -
                entry.FirstEnqueuedAt);
            if (age >= _options.MaximumRetryAge)
            {
                _pending.Remove(key);
                TouchHeartbeatLocked();
                TryCompleteQueueIfDrainedLocked();
                throw new CharacterCheckpointRetryExhaustedException(
                    work.Facet,
                    error);
            }

            entry.FailureCount++;
            entry.RetryScheduled = true;
            _scheduledRetries++;
            delay = JitteredRetryDelay(entry.FailureCount);
            TouchHeartbeatLocked();
        }

        _metrics.RecordRetry(work.Facet);
        _ = RetryAfterAsync(
            key,
            entry,
            delay,
            cancellationToken);
    }

    private async Task RetryAfterAsync(
        CheckpointKey key,
        PendingEntry entry,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                delay,
                _timeProvider,
                cancellationToken);
            lock (_sync)
            {
                CompleteRetryLocked(entry);
                if (_pending.TryGetValue(key, out var current) &&
                    ReferenceEquals(current, entry) &&
                    !entry.Invalidated &&
                    !entry.Active &&
                    !entry.Queued)
                {
                    QueueLocked(key, entry);
                }
                else if (entry.Invalidated)
                {
                    _pending.Remove(key);
                }
                TouchHeartbeatLocked();
                TryCompleteQueueIfDrainedLocked();
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            lock (_sync)
            {
                CompleteRetryLocked(entry);
                TryCompleteQueueIfDrainedLocked();
            }
        }
        catch (Exception error)
        {
            lock (_sync)
            {
                CompleteRetryLocked(entry);
            }
            ReportAsynchronousFault(error);
        }
    }

    private async Task<bool> WaitToReadWithHeartbeatAsync(
        CancellationToken cancellationToken)
    {
        var wait = _queue.Reader.WaitToReadAsync(cancellationToken);
        if (wait.IsCompletedSuccessfully)
        {
            return wait.Result;
        }

        var read = wait.AsTask();
        using var heartbeat = new PeriodicTimer(
            TimeSpan.FromSeconds(1),
            _timeProvider);
        while (!read.IsCompleted)
        {
            var tick = heartbeat.WaitForNextTickAsync(
                    cancellationToken)
                .AsTask();
            if (await Task.WhenAny(read, tick) == read)
            {
                break;
            }
            if (!await tick)
            {
                break;
            }
            lock (_sync)
            {
                TouchHeartbeatLocked();
            }
        }
        return await read;
    }

    private void CompleteActiveLocked(PendingEntry entry)
    {
        if (!entry.Active)
        {
            return;
        }

        entry.Active = false;
        if (_activeWrites <= 0)
        {
            throw new InvalidOperationException(
                "Checkpoint active-write accounting underflowed.");
        }
        _activeWrites--;
    }

    private void CompleteRetryLocked(PendingEntry entry)
    {
        if (!entry.RetryScheduled)
        {
            return;
        }

        entry.RetryScheduled = false;
        if (_scheduledRetries <= 0)
        {
            throw new InvalidOperationException(
                "Checkpoint retry accounting underflowed.");
        }
        _scheduledRetries--;
    }

    private static bool IsTransient(Exception error) =>
        error is IOException or TimeoutException or DbException;

    private static void ValidateWriteResult(
        CheckpointWorkItem work,
        CharacterCheckpointWriteResult result)
    {
        if (!Enum.IsDefined(result.Status))
        {
            throw new InvalidDataException(
                "The checkpoint store returned an unknown write status.");
        }
        if (result.StoredRevision is < 0)
        {
            throw new InvalidDataException(
                "The checkpoint store returned a negative revision.");
        }
        if ((result.Status is
                 CharacterCheckpointWriteStatus.Applied or
                 CharacterCheckpointWriteStatus.AlreadyApplied or
                 CharacterCheckpointWriteStatus.Superseded) &&
            !result.Satisfies(work.Revision))
        {
            throw new InvalidDataException(
                "A successful checkpoint result did not satisfy the " +
                "requested revision.");
        }
    }
}
