namespace Godswar.Server.Application.Characters;

internal sealed partial class CharacterCheckpointCoordinator
{
    public async Task<CharacterCheckpointOwnership?> AcquireAsync(
        int accountId,
        int characterId,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId);
        if (ownerId == Guid.Empty)
        {
            throw new ArgumentException(
                "Checkpoint owner ID cannot be empty.",
                nameof(ownerId));
        }

        var ownership = await ExecuteDirectAsync(
            token => _store.AcquireAsync(
                accountId,
                characterId,
                ownerId,
                token),
            cancellationToken);
        if (ownership is not { } acquired)
        {
            return null;
        }

        acquired.Validate();
        if (acquired.Owner.OwnerId != ownerId)
        {
            throw new InvalidDataException(
                "The checkpoint store returned ownership for a " +
                "different owner ID.");
        }

        RemoveOtherOwnerPending(
            accountId,
            characterId,
            acquired.Owner);
        return acquired;
    }

    public Task<CharacterCheckpointWriteResult> FlushThroughAsync(
        CharacterPositionCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        checkpoint.Validate();
        return FlushThroughAsync(
            CheckpointWorkItem.From(checkpoint),
            cancellationToken);
    }

    public Task<CharacterCheckpointWriteResult> FlushThroughAsync(
        CharacterVitalsCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        checkpoint.Validate();
        return FlushThroughAsync(
            CheckpointWorkItem.From(checkpoint),
            cancellationToken);
    }

    public async Task<CharacterCheckpointReleaseStatus> ReleaseAsync(
        int accountId,
        int characterId,
        CharacterCheckpointOwner owner,
        CancellationToken cancellationToken = default)
    {
        CharacterCheckpointValidation.ValidateIdentity(
            accountId,
            characterId,
            owner);
        var result = await ExecuteDirectAsync(
            token => _store.ReleaseAsync(
                accountId,
                characterId,
                owner,
                token),
            cancellationToken);
        if (!Enum.IsDefined(result))
        {
            throw new InvalidDataException(
                "The checkpoint store returned an unknown release status.");
        }

        if (result is CharacterCheckpointReleaseStatus.Released or
            CharacterCheckpointReleaseStatus.AlreadyReleased)
        {
            RemoveOwnerPending(accountId, characterId, owner);
        }
        return result;
    }

    private async Task<CharacterCheckpointWriteResult> FlushThroughAsync(
        CheckpointWorkItem work,
        CancellationToken cancellationToken)
    {
        await EnterDirectAsync(cancellationToken);
        try
        {
            using var lifetime =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _disposeStop.Token);
            var started = _timeProvider.GetTimestamp();
            var failureCount = 0;
            while (true)
            {
                try
                {
                    var result = await WriteOnceAsync(
                        work,
                        lifetime.Token);
                    ValidateWriteResult(work, result);
                    RemoveSatisfiedPending(work, result);
                    return result;
                }
                catch (Exception error)
                    when (IsTransient(error) &&
                        !cancellationToken.IsCancellationRequested &&
                        !_disposeStop.IsCancellationRequested)
                {
                    var age = _timeProvider.GetElapsedTime(
                        started,
                        _timeProvider.GetTimestamp());
                    if (age >= _options.MaximumRetryAge)
                    {
                        throw new CharacterCheckpointRetryExhaustedException(
                            work.Facet,
                            error);
                    }

                    failureCount++;
                    _metrics.RecordRetry(work.Facet);
                    var delay = JitteredRetryDelay(failureCount);
                    var remaining = _options.MaximumRetryAge - age;
                    await Task.Delay(
                        delay <= remaining ? delay : remaining,
                        _timeProvider,
                        lifetime.Token);
                }
            }
        }
        finally
        {
            ExitDirect();
        }
    }

    private async Task<T> ExecuteDirectAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await EnterDirectAsync(cancellationToken);
        try
        {
            using var deadline = new CancellationTokenSource(
                _options.CommandTimeout,
                _timeProvider);
            using var lifetime =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _disposeStop.Token,
                    deadline.Token);
            try
            {
                return await operation(lifetime.Token);
            }
            catch (OperationCanceledException)
                when (deadline.IsCancellationRequested &&
                    !cancellationToken.IsCancellationRequested &&
                    !_disposeStop.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "The checkpoint store command exceeded its deadline.");
            }
        }
        finally
        {
            ExitDirect();
        }
    }

    private async Task EnterDirectAsync(
        CancellationToken cancellationToken)
    {
        EnsureReadyForDirectOperation();
        var entered = await _directOperations.WaitAsync(
            _options.DirectAdmissionTimeout,
            cancellationToken);
        if (!entered)
        {
            throw new CharacterCheckpointAdmissionException();
        }

        try
        {
            lock (_sync)
            {
                EnsureReadyForDirectOperationLocked();
                if (_activeDirectOperations == 0)
                {
                    _directOperationsDrained =
                        new TaskCompletionSource(
                            TaskCreationOptions
                                .RunContinuationsAsynchronously);
                }
                _activeDirectOperations++;
                TouchHeartbeatLocked();
            }
        }
        catch
        {
            _directOperations.Release();
            throw;
        }
    }

    private void ExitDirect()
    {
        lock (_sync)
        {
            if (_activeDirectOperations <= 0)
            {
                throw new InvalidOperationException(
                    "Checkpoint direct-operation accounting underflowed.");
            }
            _activeDirectOperations--;
            if (_activeDirectOperations == 0)
            {
                _directOperationsDrained.TrySetResult();
            }
            TouchHeartbeatLocked();
            TryCompleteQueueIfDrainedLocked();
        }
        _directOperations.Release();
    }

    private void EnsureReadyForDirectOperation()
    {
        lock (_sync)
        {
            EnsureReadyForDirectOperationLocked();
        }
    }

    private void EnsureReadyForDirectOperationLocked()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (_state != CharacterCheckpointRuntimeState.Ready)
        {
            throw new InvalidOperationException(
                "The checkpoint coordinator is not accepting direct " +
                "operations.");
        }
    }
}
