namespace Godswar.Server.Networking.Secure;

internal sealed partial class InMemoryGameTicketStore
{
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> TryActivateGrantAsync(
        Guid generationId,
        Guid grantId,
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken)
    {
        ValidateOperation(deadline, cancellationToken);
        lock (_gate)
        {
            if (_disposed ||
                !_tickets.TryGetValue(grantId, out var record) ||
                record.GenerationId != generationId)
            {
                return ValueTask.FromResult(false);
            }
            if (IsExpired(record, _timeProvider.GetTimestamp()))
            {
                RemoveTicket(grantId, record, removeGeneration: true);
                return ValueTask.FromResult(false);
            }

            record.Committed = true;
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask RevokeGrantAsync(
        Guid generationId,
        Guid grantId,
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken)
    {
        ValidateOperation(deadline, cancellationToken);
        lock (_gate)
        {
            if (_disposed ||
                !_tickets.TryGetValue(grantId, out var record) ||
                record.GenerationId != generationId)
            {
                return ValueTask.CompletedTask;
            }

            RemoveTicket(grantId, record, removeGeneration: false);
            return ValueTask.CompletedTask;
        }
    }

    private static void ValidateOperation(
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken)
    {
        deadline.Validate();
        cancellationToken.ThrowIfCancellationRequested();
    }
}
