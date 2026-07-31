using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Sessions;
using StackExchange.Redis;

namespace Godswar.Server.Infrastructure.Redis;

internal sealed partial class RedisGameTicketStore
{
    public async ValueTask<bool> TryActivateGrantAsync(
        Guid generationId,
        Guid grantId,
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken)
    {
        ValidateOperation(deadline, cancellationToken);
        if (generationId == Guid.Empty || grantId == Guid.Empty)
        {
            return false;
        }
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        using var lifetime =
            deadline.CreateCancellationSource(cancellationToken);
        var result = await _executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Ticket,
            CoordinationDeadline.FromNow(
                deadline.Timeout,
                _timeProvider),
            database => database.ScriptEvaluateAsync(
                RedisGameTicketScripts.Activate,
                [_keys.TicketGrant(grantId)],
                [
                    _authorityId.ToString("N"),
                    generationId.ToString("N"),
                    grantId.ToString("N")
                ]),
            lifetime.Token);
        return RedisResultReader.Integer(result) == 1;
    }

    public async ValueTask RevokeGrantAsync(
        Guid generationId,
        Guid grantId,
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken)
    {
        ValidateOperation(deadline, cancellationToken);
        if (generationId == Guid.Empty ||
            grantId == Guid.Empty ||
            Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        using var lifetime =
            deadline.CreateCancellationSource(cancellationToken);
        var result = await _executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Ticket,
            CoordinationDeadline.FromNow(
                deadline.Timeout,
                _timeProvider),
            database => database.ScriptEvaluateAsync(
                RedisGameTicketScripts.RevokeGrant,
                [
                    _keys.TicketGrant(grantId),
                    _keys.OutstandingTicketRegistry()
                ],
                [
                    _authorityId.ToString("N"),
                    generationId.ToString("N"),
                    grantId.ToString("N")
                ]),
            lifetime.Token);
        UpdateOutstanding(RedisResultReader.Integer(result));
    }
}
