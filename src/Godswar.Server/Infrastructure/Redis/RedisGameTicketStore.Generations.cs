using Godswar.Server.Application.Coordination;
using Godswar.Server.Application.Sessions;
using StackExchange.Redis;

namespace Godswar.Server.Infrastructure.Redis;

internal sealed partial class RedisGameTicketStore
{
    public async ValueTask<SecureLoginGenerationResult> BeginLoginAsync(
        int accountId,
        string username,
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ValidateOperation(deadline, cancellationToken);
        SecureTicketModelValidation.ValidateAccount(accountId, username);
        ThrowIfDisposed();

        var generationId =
            SecureTicketModelValidation.CreateNonzeroId();
        using var lifetime =
            deadline.CreateCancellationSource(cancellationToken);
        var result = await _executor.ExecuteAsync(
            RedisCoordinationOperationFamily.Ticket,
            CoordinationDeadline.FromNow(
                deadline.Timeout,
                _timeProvider),
            database => database.ScriptEvaluateAsync(
                RedisGameTicketScripts.BeginGeneration,
                [
                    _keys.LoginAccount(accountId),
                    _keys.TicketGenerationRegistry(),
                    _keys.OutstandingTicketRegistry()
                ],
                [
                    _authorityId.ToString("N"),
                    generationId.ToString("N"),
                    accountId,
                    username,
                    _capacity,
                    RetentionMilliseconds(),
                    TicketTtlMilliseconds()
                ]),
            lifetime.Token);
        var response =
            RedisGameTicketResultReader.ReadOperation(result);
        UpdateSnapshot(response.Counts);
        return response.Status switch
        {
            1 => new SecureLoginGenerationResult(
                SecureLoginGenerationStatus.Started,
                new SecureLoginGeneration(
                    _authorityId,
                    generationId,
                    accountId,
                    username)),
            0 => new SecureLoginGenerationResult(
                SecureLoginGenerationStatus.CapacityExceeded,
                null),
            _ => throw new InvalidDataException(
                "Redis returned an invalid generation status.")
        };
    }

    public async ValueTask RevokeGenerationAsync(
        SecureLoginGeneration generation,
        SecureTicketOperationDeadline deadline,
        CancellationToken cancellationToken = default)
    {
        ValidateOperation(deadline, cancellationToken);
        ArgumentNullException.ThrowIfNull(generation);
        if (Volatile.Read(ref _disposed) != 0 ||
            generation.AuthorityId != _authorityId)
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
                RedisGameTicketScripts.RevokeGeneration,
                [
                    _keys.LoginAccount(generation.AccountId),
                    _keys.TicketGenerationRegistry(),
                    _keys.OutstandingTicketRegistry()
                ],
                [
                    _authorityId.ToString("N"),
                    generation.GenerationId.ToString("N")
                ]),
            lifetime.Token);
        UpdateSnapshot(
            RedisGameTicketResultReader.ReadCounts(result));
    }
}
