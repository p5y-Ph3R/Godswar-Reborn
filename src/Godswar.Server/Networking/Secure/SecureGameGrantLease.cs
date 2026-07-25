namespace Godswar.Server.Networking.Secure;

internal sealed class SecureGameGrantLease : IDisposable
{
    private readonly object _gate = new();
    private readonly Guid _generationId;
    private readonly Guid _grantId;
    private readonly InMemoryGameTicketStore _owner;
    private LeaseState _state;

    internal SecureGameGrantLease(
        InMemoryGameTicketStore owner,
        Guid generationId,
        Guid grantId,
        SecureGameGrant grant)
    {
        _owner = owner;
        _generationId = generationId;
        _grantId = grantId;
        Grant = grant;
    }

    public SecureGameGrant Grant { get; }

    public bool IsCommitted
    {
        get
        {
            lock (_gate)
            {
                return _state == LeaseState.Committed;
            }
        }
    }

    public bool IsDisposed
    {
        get
        {
            lock (_gate)
            {
                return _state == LeaseState.Disposed;
            }
        }
    }

    public bool Commit()
    {
        lock (_gate)
        {
            if (_state == LeaseState.Committed)
            {
                return true;
            }
            if (_state == LeaseState.Disposed)
            {
                return false;
            }
            if (_state == LeaseState.Pending &&
                !TryActivateCore())
            {
                return false;
            }

            _state = LeaseState.Committed;
            return true;
        }
    }

    /// <summary>
    /// Makes the ticket redeemable after the grant frame is physically sent.
    /// The lease remains revocable until the matching redirect is also sent
    /// and <see cref="Commit"/> preserves it.
    /// </summary>
    public bool Activate()
    {
        lock (_gate)
        {
            if (_state is LeaseState.Activated or LeaseState.Committed)
            {
                return true;
            }
            if (_state != LeaseState.Pending)
            {
                return false;
            }

            return TryActivateCore();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_state == LeaseState.Disposed)
            {
                return;
            }

            if (_state is LeaseState.Pending or LeaseState.Activated)
            {
                _owner.RevokeGrant(_generationId, _grantId);
            }

            _state = LeaseState.Disposed;
            Grant.Dispose();
        }
    }

    private bool TryActivateCore()
    {
        if (!_owner.TryCommit(_generationId, _grantId))
        {
            _state = LeaseState.Disposed;
            Grant.Dispose();
            return false;
        }

        _state = LeaseState.Activated;
        return true;
    }

    private enum LeaseState : byte
    {
        Pending = 0,
        Activated = 1,
        Committed = 2,
        Disposed = 3
    }
}
