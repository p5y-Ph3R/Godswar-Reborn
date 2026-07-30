using System.Diagnostics;
using System.Globalization;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class
    PostgresEquipmentBagTransferCommandExecutor
{
    public async Task<EquipmentBagTransferExecutionResult>
        TryReplayAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            Guid clientOperationId,
            int equipmentSlot,
            int kitBagSlot,
            CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            if (!IsValidReplayIdentity(
                    subject,
                    clientOperationId,
                    equipmentSlot,
                    kitBagSlot))
            {
                outcome = "invalid_intent";
                return EquipmentBagTransferExecutionResult
                    .InvalidIntent();
            }

            var operationId = DecodeDigest(
                EquipmentBagTransferCommandEnvelope.CreateOperationId(
                    subject,
                    clientOperationId));
            var principalKey = subject.AccountId.ToString(
                CultureInfo.InvariantCulture);
            var aggregateKey =
                EquipmentBagTransferPersistenceCodec.AggregateKey(
                    subject.CharacterId);
            await using var connection =
                await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction =
                await connection.BeginTransactionAsync(cancellationToken);
            var ownershipResult =
                await _ownershipGuard.LockCurrentAsync(
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
                return EquipmentBagTransferExecutionResult
                    .PreconditionFailed();
            }
            ownershipResult.RequireCurrent();

            if (await LockCharacterAsync(
                    connection,
                    transaction,
                    subject,
                    cancellationToken) is null)
            {
                outcome = "precondition_failed";
                return EquipmentBagTransferExecutionResult
                    .PreconditionFailed();
            }

            var stored = await ReadInboxAsync(
                connection,
                transaction,
                principalKey,
                aggregateKey,
                operationId,
                cancellationToken);
            if (stored is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                outcome = "replay_not_found";
                return EquipmentBagTransferExecutionResult
                    .ReplayNotFound();
            }

            var receipt = ValidateStoredResult(stored, subject);
            if (!HasRequestedSlots(
                    receipt,
                    equipmentSlot,
                    kitBagSlot))
            {
                await RecordRequestConflictAsync(
                    connection,
                    transaction,
                    stored.InboxId,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                outcome = "request_hash_conflict";
                return EquipmentBagTransferExecutionResult
                    .RequestHashConflict();
            }

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
            return EquipmentBagTransferExecutionResult
                .Duplicate(receipt);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            PostgresCommandMetrics.RecordInbox(
                EquipmentBagTransferPersistenceCodec
                    .CommandFamilyCode,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }
}
