using System.Diagnostics;
using System.Globalization;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class
    PostgresGearMentorMaterialConversionCommandExecutor
{
    public async Task<ClassSuitExecutionResult> ExecuteAsync(
        CommandEnvelope<ClassSuitCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var validation = ClassSuitCommandEnvelope.Validate(envelope);
        if (validation == CommandEnvelopeValidation.RequestHashConflict)
        {
            return ClassSuitExecutionResult.RequestHashConflict();
        }
        if (validation != CommandEnvelopeValidation.Valid ||
            !HasCanonicalClassSuitSelections(envelope.Command))
        {
            return ClassSuitExecutionResult.InvalidIntent();
        }

        var result = await ExecuteClassSuitTransactionAsync(
            new ClassSuitCommandContext(
                envelope.Subject,
                envelope.Ownership,
                envelope.OperationId,
                envelope.RequestHash,
                envelope.Command),
            cancellationToken);
        if (result.Receipt is not null)
        {
            (await _ownershipGuard.ValidateCurrentAsync(
                envelope.Subject,
                envelope.Ownership,
                cancellationToken)).RequireCurrent();
        }
        return result;
    }

    public async Task<ClassSuitExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        ClassSuitReplayIntent replayIntent,
        ClassSuitOperationIdentity identity,
        CancellationToken cancellationToken = default)
    {
        if (subject.AccountId <= 0 ||
            subject.CharacterId <= 0 ||
            !replayIntent.IsValid ||
            !identity.IsSecureClient)
        {
            return ClassSuitExecutionResult.InvalidIntent();
        }

        var family = ClassSuitCommandEnvelope.Family(
            replayIntent.Operation);
        var aggregateKey = ClassSuitPersistenceCodec.AggregateKey(
            subject.CharacterId);
        var principalKey = subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var operationId = DecodeDigest(
            ClassSuitCommandEnvelope.CreateOperationId(
                subject,
                replayIntent.Operation,
                identity));

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
            return ClassSuitExecutionResult.PreconditionFailed();
        }
        ownershipResult.RequireCurrent();

        var stored = await ReadClassSuitInboxAsync(
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
            return ClassSuitExecutionResult.ReplayNotFound();
        }

        var receipt = ClassSuitPersistenceCodec.DecodeAndVerify(
            stored.ResultPayload,
            stored.ResultHash,
            stored.ResultCode,
            stored.AuditId,
            family);
        if (receipt.ReplayIntent != replayIntent)
        {
            await RecordRequestConflictAsync(
                connection,
                transaction,
                stored.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            (await _ownershipGuard.ValidateCurrentAsync(
                subject,
                ownership,
                cancellationToken)).RequireCurrent();
            return ClassSuitExecutionResult.RequestHashConflict();
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
        return ClassSuitExecutionResult.Duplicate(receipt);
    }

    private async Task<ClassSuitExecutionResult>
        ExecuteClassSuitTransactionAsync(
            ClassSuitCommandContext context,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(context.OperationId);
        var requestHash = DecodeDigest(context.RequestHash);
        var family = context.Family;
        var principalKey = context.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey = ClassSuitPersistenceCodec.AggregateKey(
            context.Subject.CharacterId);

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var ownership = await _ownershipGuard.LockCurrentAsync(
            connection,
            transaction,
            context.Subject,
            context.Ownership,
            cancellationToken);
        if (ownership.Status ==
            PlayerOwnershipValidationStatus.CharacterNotFound)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ClassSuitExecutionResult.PreconditionFailed();
        }
        ownership.RequireCurrent();

        var character = await LockClassSuitCharacterAsync(
            connection,
            transaction,
            context.Subject,
            cancellationToken);
        if (character is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ClassSuitExecutionResult.PreconditionFailed();
        }

        var existing = await ReadClassSuitInboxAsync(
            connection,
            transaction,
            family,
            principalKey,
            aggregateKey,
            operationId,
            cancellationToken);
        if (existing is not null)
        {
            if (!System.Security.Cryptography.CryptographicOperations
                    .FixedTimeEquals(existing.RequestHash, requestHash))
            {
                await RecordRequestConflictAsync(
                    connection,
                    transaction,
                    existing.InboxId,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return ClassSuitExecutionResult.RequestHashConflict();
            }

            var replay = ClassSuitPersistenceCodec.DecodeAndVerify(
                existing.ResultPayload,
                existing.ResultHash,
                existing.ResultCode,
                existing.AuditId,
                family);
            await RecordDuplicateAsync(
                connection,
                transaction,
                existing.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ClassSuitExecutionResult.Duplicate(replay);
        }

        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                context.Subject.AccountId,
                context.Subject.CharacterId,
                _commandTimeoutSeconds,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return ClassSuitExecutionResult.PreconditionFailed();
        }

        var bag = await LockKitBagAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            cancellationToken);
        var plan = CreateClassSuitPlan(
            bag.CompactProjection,
            character.Value.Profession,
            character.Value.PlayerLevel,
            context.Command);
        var status = plan.Status;
        if (!plan.Committed)
        {
            var receipt = await PersistClassSuitTerminalAsync(
                connection,
                transaction,
                context,
                character.Value.InventoryRevision,
                status,
                principalKey,
                aggregateKey,
                operationId,
                requestHash,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ClassSuitExecutionResult.TerminalRejected(receipt);
        }

        return await PersistClassSuitCommitAsync(
            connection,
            transaction,
            context,
            character.Value.InventoryRevision,
            plan,
            bag,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
    }

    private static bool HasCanonicalClassSuitSelections(
        ClassSuitCommand command)
    {
        foreach (var selection in new ClassSuitCommandSelection?[]
                 {
                     command.Gear,
                     command.PrimaryMaterial,
                     command.SecondaryMaterial
                 })
        {
            if (!selection.HasValue)
            {
                continue;
            }

            var item = CompactItemEntry.Parse(
                selection.Value.ExpectedCompactItemState);
            if (item.IsEmpty || !string.Equals(
                    item.ToCompactString(),
                    selection.Value.ExpectedCompactItemState,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private readonly record struct ClassSuitCommandContext(
        CommandSubject Subject,
        PlayerOwnershipFence Ownership,
        string OperationId,
        string RequestHash,
        ClassSuitCommand Command)
    {
        public CommandFamily Family =>
            ClassSuitCommandEnvelope.Family(Command.Operation);

        public ClassSuitReplayIntent ReplayIntent =>
            ClassSuitReplayIntent.FromCommand(Command);
    }

    private readonly record struct LockedClassSuitCharacter(
        byte Profession,
        int PlayerLevel,
        long InventoryRevision);

    private sealed record ClassSuitStoredInbox(
        long InboxId,
        byte[] RequestHash,
        string ResultCode,
        string ResultPayload,
        byte[] ResultHash,
        long AuditId);

    private sealed record ClassSuitPlan(
        ClassSuitCommandResultStatus Status,
        IReadOnlyList<ClassSuitSlotMutation> Mutations)
    {
        public bool Committed =>
            Status == ClassSuitCommandResultStatus.Succeeded;
    }
}
