namespace Godswar.Server.Game.Maps;

/// <summary>
/// Serializes character-position writes and fences saves captured before a
/// world relocation.
/// </summary>
/// <remarks>
/// A relocation publishes a pending epoch before it waits for the persistence
/// gate. This rejects queued old-world saves while allowing a callback that is
/// already in flight to finish. The relocation then acquires the same gate and
/// becomes the final write from the old epoch.
///
/// Position callbacks must not call back into this coordinator.
/// </remarks>
internal sealed class CharacterPositionPersistenceCoordinator
{
    private readonly object _epochGate = new();
    private readonly SemaphoreSlim _persistenceGate = new(1, 1);
    private readonly SemaphoreSlim _relocationGate = new(1, 1);

    private long _committedEpoch;
    private long? _pendingEpoch;

    /// <summary>
    /// Captures the last successfully committed position epoch.
    /// </summary>
    public long CaptureEpoch()
    {
        lock (_epochGate)
        {
            return _committedEpoch;
        }
    }

    /// <summary>
    /// Runs a normal position save only when its captured epoch remains
    /// current. Returns <see langword="false"/> without invoking the callback
    /// when a relocation has invalidated the epoch.
    /// </summary>
    public async Task<bool> PersistIfCurrentAsync(
        long epoch,
        Func<CancellationToken, Task> persistAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistAsync);

        if (!IsPersistable(epoch))
        {
            return false;
        }

        var gateEntered = false;
        try
        {
            await _persistenceGate.WaitAsync(cancellationToken);
            gateEntered = true;

            // A relocation may have published its pending epoch while this
            // save was queued behind another write.
            if (!IsPersistable(epoch))
            {
                return false;
            }

            await persistAsync(cancellationToken);
            return true;
        }
        finally
        {
            if (gateEntered)
            {
                _persistenceGate.Release();
            }
        }
    }

    /// <summary>
    /// Invalidates the current epoch, waits for any in-flight save, and
    /// persists the relocation as the final write from the old world.
    /// </summary>
    /// <returns>The newly committed epoch.</returns>
    /// <remarks>
    /// If persistence fails or is cancelled, the pending epoch is removed and
    /// the previously committed epoch becomes current again.
    /// </remarks>
    public async Task<long> AdvanceAndPersistAsync(
        Func<CancellationToken, Task> persistAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistAsync);

        var relocationGateEntered = false;
        var persistenceGateEntered = false;
        var pendingInstalled = false;
        var nextEpoch = 0L;

        try
        {
            await _relocationGate.WaitAsync(cancellationToken);
            relocationGateEntered = true;

            lock (_epochGate)
            {
                if (_pendingEpoch.HasValue)
                {
                    throw new InvalidOperationException(
                        "A character-position relocation is already pending.");
                }

                nextEpoch = checked(_committedEpoch + 1);
                _pendingEpoch = nextEpoch;
                pendingInstalled = true;
            }

            // An old-world callback that already entered is allowed to finish.
            // This callback then wins the serialized write order.
            await _persistenceGate.WaitAsync(cancellationToken);
            persistenceGateEntered = true;
            await persistAsync(cancellationToken);

            lock (_epochGate)
            {
                if (_pendingEpoch != nextEpoch)
                {
                    throw new InvalidOperationException(
                        "The character-position relocation epoch changed unexpectedly.");
                }

                _committedEpoch = nextEpoch;
                _pendingEpoch = null;
                pendingInstalled = false;
            }

            return nextEpoch;
        }
        finally
        {
            // Clear the fence before releasing the persistence gate. A queued
            // save can then observe either the committed new epoch or the
            // restored old epoch, never an abandoned pending epoch.
            if (pendingInstalled)
            {
                lock (_epochGate)
                {
                    if (_pendingEpoch == nextEpoch)
                    {
                        _pendingEpoch = null;
                    }
                }
            }

            if (persistenceGateEntered)
            {
                _persistenceGate.Release();
            }

            if (relocationGateEntered)
            {
                _relocationGate.Release();
            }
        }
    }

    private bool IsPersistable(long epoch)
    {
        lock (_epochGate)
        {
            // CaptureEpoch deliberately returns the committed epoch while a
            // relocation is pending. Saves captured during that window are
            // rejected and must capture again after relocation completes.
            return !_pendingEpoch.HasValue &&
                   epoch == _committedEpoch;
        }
    }
}
