using System.Diagnostics;
using System.Globalization;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolySuitCommandExecutor
{
    public async Task<HolySuitExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        HolySuitCommandOperation operation,
        HolySuitOperationIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        CommandFamily? family = Enum.IsDefined(operation)
            ? HolySuitCommandEnvelope.Family(operation)
            : null;
        var outcome = "provider_unavailable";
        try
        {
            if (subject.AccountId <= 0 || subject.CharacterId <= 0 ||
                !Enum.IsDefined(operation) ||
                !identity.IsSecureClient && !identity.IsRawLocalServer)
            {
                outcome = "invalid_intent";
                return HolySuitExecutionResult.InvalidIntent();
            }

            var operationId = DecodeDigest(
                HolySuitCommandEnvelope.CreateOperationId(
                    subject,
                    operation,
                    identity));
            var principalKey = subject.AccountId.ToString(
                CultureInfo.InvariantCulture);
            var aggregateKey = HolySuitPersistenceCodec.AggregateKey(
                subject.CharacterId);
            await using var connection =
                await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction =
                await connection.BeginTransactionAsync(cancellationToken);
            var ownershipResult = await _ownershipGuard.LockCurrentAsync(
                connection,
                transaction,
                subject,
                ownership,
                cancellationToken);
            if (ownershipResult.Status ==
                PlayerOwnershipValidationStatus.CharacterNotFound)
            {
                await transaction.RollbackAsync(cancellationToken);
                outcome = "precondition_failed";
                return HolySuitExecutionResult.PreconditionFailed();
            }
            ownershipResult.RequireCurrent();

            if (await LockCharacterAsync(
                connection,
                transaction,
                subject,
                cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                outcome = "precondition_failed";
                return HolySuitExecutionResult.PreconditionFailed();
            }

            var stored = await ReadInboxAsync(
                connection,
                transaction,
                family!.Value,
                principalKey,
                aggregateKey,
                operationId,
                cancellationToken);
            if (stored is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                outcome = "replay_not_found";
                return HolySuitExecutionResult.ReplayNotFound();
            }

            var receipt = ValidateStoredResult(stored, family.Value);
            await RecordDuplicateAsync(
                connection,
                transaction,
                stored.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            (await _ownershipGuard.ValidateCurrentAsync(
                subject,
                ownership,
                cancellationToken)).RequireCurrent();
            outcome = "duplicate";
            return HolySuitExecutionResult.Duplicate(receipt);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            if (family.HasValue && IsHolySuitFamily(family.Value))
            {
                PostgresCommandMetrics.RecordInbox(
                    HolySuitPersistenceCodec.CommandFamilyCode(family.Value),
                    outcome,
                    Stopwatch.GetElapsedTime(started));
            }
        }
    }
}
