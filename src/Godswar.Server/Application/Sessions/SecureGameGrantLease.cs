namespace Godswar.Server.Application.Sessions;

internal sealed class SecureGameGrantLease : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Guid _generationId;
    private readonly Guid _grantId;
    private readonly ISecureGameGrantLeaseAuthority _owner;
    private int _state;

    internal SecureGameGrantLease(
        ISecureGameGrantLeaseAuthority owner,
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
        => ReadState() == LeaseState.Committed;

    public bool IsDisposed
        => ReadState() == LeaseState.Disposed;

    public async ValueTask<bool> CommitAsync(
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        using var lifetime =
            deadline.CreateCancellationSource(cancellationToken);
        await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            if (ReadState() == LeaseState.Committed)
            {
                return true;
            }
            if (ReadState() == LeaseState.Disposed)
            {
                return false;
            }
            if (ReadState() == LeaseState.Pending &&
                !await TryActivateCoreAsync(
                        deadline,
                        lifetime.Token)
                    .ConfigureAwait(false))
            {
                return false;
            }

            WriteState(LeaseState.Committed);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Makes the ticket redeemable. Login flows should use
    /// <see cref="CommitAsync"/> only after the matching redirect write has
    /// completed, so a failed redirect cannot leave an activated ticket.
    /// </summary>
    public async ValueTask<bool> ActivateAsync(
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        using var lifetime =
            deadline.CreateCancellationSource(cancellationToken);
        await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            if (ReadState() is
                LeaseState.Activated or LeaseState.Committed)
            {
                return true;
            }
            if (ReadState() != LeaseState.Pending)
            {
                return false;
            }

            return await TryActivateCoreAsync(
                    deadline,
                    lifetime.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync() =>
        RevokeAsync(
            SecureTicketOperationDeadline.Default,
            CancellationToken.None);

    public async ValueTask RevokeAsync(
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        using var lifetime =
            deadline.CreateCancellationSource(cancellationToken);
        await _gate.WaitAsync(lifetime.Token).ConfigureAwait(false);
        try
        {
            if (ReadState() == LeaseState.Disposed)
            {
                return;
            }

            if (ReadState() is LeaseState.Pending or LeaseState.Activated)
            {
                await _owner.RevokeGrantAsync(
                        _generationId,
                        _grantId,
                        deadline,
                        lifetime.Token)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            WriteState(LeaseState.Disposed);
            Grant.Dispose();
            _gate.Release();
        }
    }

    private async ValueTask<bool> TryActivateCoreAsync(
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken)
    {
        if (!await _owner.TryActivateGrantAsync(
                _generationId,
                _grantId,
                deadline,
                cancellationToken)
            .ConfigureAwait(false))
        {
            WriteState(LeaseState.Disposed);
            Grant.Dispose();
            return false;
        }

        WriteState(LeaseState.Activated);
        return true;
    }

    private LeaseState ReadState() =>
        (LeaseState)Volatile.Read(ref _state);

    private void WriteState(LeaseState state) =>
        Volatile.Write(ref _state, (int)state);

    private enum LeaseState : byte
    {
        Pending = 0,
        Activated = 1,
        Committed = 2,
        Disposed = 3
    }
}
