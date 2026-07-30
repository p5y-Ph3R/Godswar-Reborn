using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresKitBagItemMoveCommandExecutor :
    IKitBagItemMoveCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly IPostgresKitBagItemMoveCommandProbe? _probe;

    public PostgresKitBagItemMoveCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        IPostgresKitBagItemMoveCommandProbe? probe = null)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _ownershipGuard = new PostgresPlayerOwnershipGuard(_dataSource);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _commandTimeoutSeconds = Math.Max(
            1,
            (int)Math.Ceiling(options.CommandTimeout.TotalSeconds));
        _maximumOutboxAttempts =
            checked((short)options.MaximumDeliveryAttempts);
        _probe = probe;
    }

    public async Task<KitBagItemMoveExecutionResult> ExecuteAsync(
        CommandEnvelope<KitBagItemMoveCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation =
                KitBagItemMoveCommandEnvelope.Validate(envelope);
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return KitBagItemMoveExecutionResult
                    .RequestHashConflict();
            }
            if (validation != CommandEnvelopeValidation.Valid ||
                !IsCanonicalCompactItemState(
                    envelope.Command
                        .ExpectedSourceCompactItemState) ||
                !IsCanonicalCompactItemState(
                    envelope.Command
                        .ExpectedDestinationCompactItemState))
            {
                outcome = "invalid_intent";
                return KitBagItemMoveExecutionResult.InvalidIntent();
            }

            var context = new KitBagItemMoveCommandContext(
                envelope.Subject,
                envelope.Ownership,
                envelope.OperationId,
                envelope.RequestHash,
                envelope.Command);
            var result = await ExecuteTransactionAsync(
                context,
                cancellationToken);
            if (result.Receipt is not null)
            {
                (await _ownershipGuard.ValidateCurrentAsync(
                    envelope.Subject,
                    envelope.Ownership,
                    cancellationToken)).RequireCurrent();
            }
            outcome = OutcomeCode(result.Disposition);
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            PostgresCommandMetrics.RecordInbox(
                KitBagItemMovePersistenceCodec.CommandFamilyCode,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    public async Task<KitBagItemMoveExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        Guid clientOperationId,
        int sourceKitBagSlot,
        int destinationKitBagSlot,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            if (!IsValidReplayIdentity(
                    subject,
                    clientOperationId,
                    sourceKitBagSlot,
                    destinationKitBagSlot))
            {
                outcome = "invalid_intent";
                return KitBagItemMoveExecutionResult.InvalidIntent();
            }

            var operationId = DecodeDigest(
                KitBagItemMoveCommandEnvelope.CreateOperationId(
                    subject,
                    clientOperationId));
            var principalKey = subject.AccountId.ToString(
                CultureInfo.InvariantCulture);
            var aggregateKey =
                KitBagItemMovePersistenceCodec.AggregateKey(
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
                return KitBagItemMoveExecutionResult
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
                return KitBagItemMoveExecutionResult
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
                return KitBagItemMoveExecutionResult.ReplayNotFound();
            }

            var receipt = ValidateStoredResult(stored, subject);
            if (!HasRequestedSlots(
                    receipt,
                    sourceKitBagSlot,
                    destinationKitBagSlot))
            {
                await RecordRequestConflictAsync(
                    connection,
                    transaction,
                    stored.InboxId,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                outcome = "request_hash_conflict";
                return KitBagItemMoveExecutionResult
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
            return KitBagItemMoveExecutionResult.Duplicate(receipt);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            PostgresCommandMetrics.RecordInbox(
                KitBagItemMovePersistenceCodec.CommandFamilyCode,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<KitBagItemMoveExecutionResult>
        ExecuteTransactionAsync(
            KitBagItemMoveCommandContext context,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(context.OperationId);
        var requestHash = DecodeDigest(context.RequestHash);
        var principalKey = context.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey =
            KitBagItemMovePersistenceCodec.AggregateKey(
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
            return KitBagItemMoveExecutionResult
                .PreconditionFailed();
        }
        ownership.RequireCurrent();

        var inventoryRevision = await LockCharacterAsync(
            connection,
            transaction,
            context.Subject,
            cancellationToken);
        if (!inventoryRevision.HasValue)
        {
            return KitBagItemMoveExecutionResult.PreconditionFailed();
        }

        var existing = await ReadInboxAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            cancellationToken);
        if (existing is not null)
        {
            var receipt = ValidateStoredResult(
                existing,
                context.Subject);
            if (!HasRequestedSlots(
                    receipt,
                    context.Command.SourceKitBagSlot,
                    context.Command.DestinationKitBagSlot) ||
                !CryptographicOperations.FixedTimeEquals(
                    existing.RequestHash,
                    requestHash))
            {
                await RecordRequestConflictAsync(
                    connection,
                    transaction,
                    existing.InboxId,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return KitBagItemMoveExecutionResult
                    .RequestHashConflict();
            }

            await RecordDuplicateAsync(
                connection,
                transaction,
                existing.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return KitBagItemMoveExecutionResult.Duplicate(receipt);
        }

        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                context.Subject.AccountId,
                context.Subject.CharacterId,
                _commandTimeoutSeconds,
                cancellationToken))
        {
            return KitBagItemMoveExecutionResult.PreconditionFailed();
        }

        var slots = await LockKitBagSlotsAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            context.Command.SourceKitBagSlot,
            context.Command.DestinationKitBagSlot,
            cancellationToken);
        var sourceState =
            slots.Source?.Item.ToCompactString() ?? "[]";
        var destinationState =
            slots.Destination?.Item.ToCompactString() ?? "[]";
        var status = ResolveStatus(
            context.Command,
            sourceState,
            destinationState);
        if (status is not KitBagItemMoveResultStatus.Moved and
            not KitBagItemMoveResultStatus.Swapped)
        {
            var evidence = await PersistResultEvidenceAsync(
                connection,
                transaction,
                context,
                principalKey,
                aggregateKey,
                operationId,
                requestHash,
                status,
                sourceState,
                destinationState,
                inventoryRevision.Value,
                null,
                cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return KitBagItemMoveExecutionResult.TerminalRejected(
                evidence.Receipt);
        }
        if (slots.Source is null)
        {
            throw new InvalidDataException(
                "Validated movement has no locked source item.");
        }

        return await PersistMovementAsync(
            connection,
            transaction,
            context,
            slots,
            status,
            inventoryRevision.Value,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
    }

    private async Task CommitAsync(
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ReachAsync(
            PostgresKitBagItemMoveCommandStage.BeforeCommit,
            0,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ReachAsync(
            PostgresKitBagItemMoveCommandStage.AfterCommit,
            0,
            cancellationToken);
    }

    private async ValueTask ReachAsync(
        PostgresKitBagItemMoveCommandStage stage,
        int ordinal,
        CancellationToken cancellationToken)
    {
        if (_probe is not null)
        {
            await _probe.ReachedAsync(stage, ordinal, cancellationToken);
        }
    }

    private static KitBagItemMoveResultStatus ResolveStatus(
        KitBagItemMoveCommand command,
        string sourceState,
        string destinationState)
    {
        if (!string.Equals(
                command.ExpectedSourceCompactItemState,
                sourceState,
                StringComparison.Ordinal))
        {
            return KitBagItemMoveResultStatus.StaleSource;
        }
        if (sourceState == "[]")
        {
            return KitBagItemMoveResultStatus.EmptySource;
        }
        if (!string.Equals(
                command.ExpectedDestinationCompactItemState,
                destinationState,
                StringComparison.Ordinal))
        {
            return KitBagItemMoveResultStatus.StaleDestination;
        }
        return destinationState == "[]"
            ? KitBagItemMoveResultStatus.Moved
            : KitBagItemMoveResultStatus.Swapped;
    }

    private static bool IsCanonicalCompactItemState(string value)
    {
        try
        {
            return string.Equals(
                CompactItemEntry.Parse(value).ToCompactString(),
                value,
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is FormatException or
                OverflowException or
                ArgumentException)
        {
            return false;
        }
    }

    private static bool IsValidReplayIdentity(
        CommandSubject subject,
        Guid operationId,
        int source,
        int destination) =>
        subject.AccountId > 0 &&
        subject.CharacterId > 0 &&
        operationId != Guid.Empty &&
        source is >= KitBagItemMoveCommandEnvelope.MinimumKitBagSlot and
            <= KitBagItemMoveCommandEnvelope.MaximumKitBagSlot &&
        destination is >=
                KitBagItemMoveCommandEnvelope.MinimumKitBagSlot and
            <= KitBagItemMoveCommandEnvelope.MaximumKitBagSlot &&
        source != destination;

    private static bool HasRequestedSlots(
        KitBagItemMoveExecutionReceipt receipt,
        int source,
        int destination) =>
        receipt.SourceKitBagSlot == source &&
        receipt.DestinationKitBagSlot == destination;

    private static byte[] DecodeDigest(string value)
    {
        var bytes = Convert.FromHexString(value);
        if (bytes.Length != CommandEnvelopeContract.DigestBytes)
        {
            throw new InvalidDataException(
                "The command digest has an invalid size.");
        }
        return bytes;
    }

    private static string OutcomeCode(
        KitBagItemMoveExecutionDisposition disposition) =>
        disposition switch
        {
            KitBagItemMoveExecutionDisposition.Committed => "committed",
            KitBagItemMoveExecutionDisposition.Duplicate => "duplicate",
            KitBagItemMoveExecutionDisposition.TerminalRejected =>
                "terminal_rejected",
            KitBagItemMoveExecutionDisposition.ReplayNotFound =>
                "replay_not_found",
            KitBagItemMoveExecutionDisposition.RequestHashConflict =>
                "request_hash_conflict",
            KitBagItemMoveExecutionDisposition.InvalidIntent =>
                "invalid_intent",
            KitBagItemMoveExecutionDisposition.PreconditionFailed =>
                "precondition_failed",
            _ => throw new ArgumentOutOfRangeException(
                nameof(disposition))
        };

    private NpgsqlCommand CreateCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _commandTimeoutSeconds
        };

    private sealed record KitBagItemMoveCommandContext(
        CommandSubject Subject,
        PlayerOwnershipFence Ownership,
        string OperationId,
        string RequestHash,
        KitBagItemMoveCommand Command);

    private sealed record StoredInbox(
        long InboxId,
        byte[] RequestHash,
        short ResultContractVersion,
        string ResultCode,
        string ResultPayload,
        byte[] ResultHash,
        long AuditId);

    private sealed record LockedKitBagItem(
        long ItemInstanceId,
        short SlotIndex,
        CompactItemEntry Item,
        string BeforeState);

    private sealed record LockedKitBagSlots(
        LockedKitBagItem? Source,
        LockedKitBagItem? Destination);

    private sealed record PersistedResultEvidence(
        long InboxId,
        byte[] Payload,
        KitBagItemMoveExecutionReceipt Receipt);
}
