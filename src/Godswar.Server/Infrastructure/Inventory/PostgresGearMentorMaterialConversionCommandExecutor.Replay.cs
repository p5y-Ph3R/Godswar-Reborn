using System.Diagnostics;
using System.Globalization;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Messaging;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class
    PostgresGearMentorMaterialConversionCommandExecutor
{
    public Task<GearMentorMaterialConversionExecutionResult>
        TryReplayTransformAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            Guid clientOperationId,
            CancellationToken cancellationToken = default) =>
        TryReplayAsync(
            subject,
            ownership,
            clientOperationId,
            CommandFamily.GearMentorTransformCrystal,
            GearMentorTransformCrystalCommandEnvelope.CreateOperationId,
            cancellationToken);

    public Task<GearMentorMaterialConversionExecutionResult>
        TryReplayCombineAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            Guid clientOperationId,
            CancellationToken cancellationToken = default) =>
        TryReplayAsync(
            subject,
            ownership,
            clientOperationId,
            CommandFamily.GearMentorCombineGemPieces,
            GearMentorCombineGemPiecesCommandEnvelope.CreateOperationId,
            cancellationToken);

    private async Task<GearMentorMaterialConversionExecutionResult>
        TryReplayAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            Guid clientOperationId,
            CommandFamily family,
            Func<CommandSubject, Guid, string> operationIdFactory,
            CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            if (subject.AccountId <= 0 ||
                subject.CharacterId <= 0 ||
                clientOperationId == Guid.Empty)
            {
                outcome = "invalid_intent";
                return GearMentorMaterialConversionExecutionResult
                    .InvalidIntent();
            }

            var operationId = DecodeDigest(
                operationIdFactory(subject, clientOperationId));
            var principalKey = subject.AccountId.ToString(
                CultureInfo.InvariantCulture);
            var aggregateKey =
                GearMentorMaterialConversionPersistenceCodec
                    .AggregateKey(subject.CharacterId);

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
                return GearMentorMaterialConversionExecutionResult
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
                return GearMentorMaterialConversionExecutionResult
                    .PreconditionFailed();
            }

            var stored = await ReadInboxAsync(
                connection,
                transaction,
                family,
                principalKey,
                aggregateKey,
                operationId,
                cancellationToken);
            if (stored is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                outcome = "replay_not_found";
                return GearMentorMaterialConversionExecutionResult
                    .ReplayNotFound();
            }

            var receipt = ValidateStoredResult(stored, family);
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
            return GearMentorMaterialConversionExecutionResult
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
                GearMentorMaterialConversionPersistenceCodec
                    .CommandFamilyCode(family),
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }
}
