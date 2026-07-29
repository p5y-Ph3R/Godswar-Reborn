using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresKitBagItemDeleteCommandExecutor :
    IKitBagItemDeleteCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly IPostgresKitBagItemDeleteCommandProbe? _probe;

    public PostgresKitBagItemDeleteCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        IPostgresKitBagItemDeleteCommandProbe? probe = null)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _commandTimeoutSeconds = Math.Max(
            1,
            (int)Math.Ceiling(options.CommandTimeout.TotalSeconds));
        _maximumOutboxAttempts =
            checked((short)options.MaximumDeliveryAttempts);
        _probe = probe;
    }

    public async Task<KitBagItemDeleteExecutionResult> ExecuteAsync(
        CommandEnvelope<KitBagItemDeleteCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation =
                KitBagItemDeleteCommandEnvelope.Validate(envelope);
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return KitBagItemDeleteExecutionResult
                    .RequestHashConflict();
            }
            if (validation != CommandEnvelopeValidation.Valid ||
                !IsCanonicalCompactItemState(
                    envelope.Command.ExpectedCompactItemState))
            {
                outcome = "invalid_intent";
                return KitBagItemDeleteExecutionResult.InvalidIntent();
            }

            var context = new KitBagItemDeleteCommandContext(
                envelope.Subject,
                envelope.OperationId,
                envelope.RequestHash,
                envelope.Command);
            var result = await ExecuteTransactionAsync(
                context,
                cancellationToken);
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
                KitBagItemDeletePersistenceCodec.CommandFamilyCode,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    public async Task<KitBagItemDeleteExecutionResult> TryReplayAsync(
        CommandSubject subject,
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
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
                return KitBagItemDeleteExecutionResult.InvalidIntent();
            }

            var operationId = DecodeDigest(
                KitBagItemDeleteCommandEnvelope.CreateOperationId(
                    subject,
                    clientOperationId));
            var principalKey = subject.AccountId.ToString(
                CultureInfo.InvariantCulture);
            var aggregateKey =
                KitBagItemDeletePersistenceCodec.AggregateKey(
                    subject.CharacterId);

            await using var connection =
                await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction =
                await connection.BeginTransactionAsync(cancellationToken);
            if (await LockCharacterAsync(
                    connection,
                    transaction,
                    subject,
                    cancellationToken) is null)
            {
                outcome = "precondition_failed";
                return KitBagItemDeleteExecutionResult
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
                return KitBagItemDeleteExecutionResult.ReplayNotFound();
            }

            var receipt = ValidateStoredResult(stored, subject);
            await RecordDuplicateAsync(
                connection,
                transaction,
                stored.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            outcome = "duplicate";
            return KitBagItemDeleteExecutionResult.Duplicate(receipt);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            PostgresCommandMetrics.RecordInbox(
                KitBagItemDeletePersistenceCodec.CommandFamilyCode,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<KitBagItemDeleteExecutionResult>
        ExecuteTransactionAsync(
            KitBagItemDeleteCommandContext context,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(context.OperationId);
        var requestHash = DecodeDigest(context.RequestHash);
        var principalKey = context.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey =
            KitBagItemDeletePersistenceCodec.AggregateKey(
                context.Subject.CharacterId);

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var inventoryRevision = await LockCharacterAsync(
            connection,
            transaction,
            context.Subject,
            cancellationToken);
        if (!inventoryRevision.HasValue)
        {
            return KitBagItemDeleteExecutionResult.PreconditionFailed();
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
            if (!CryptographicOperations.FixedTimeEquals(
                    existing.RequestHash,
                    requestHash))
            {
                await RecordRequestConflictAsync(
                    connection,
                    transaction,
                    existing.InboxId,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return KitBagItemDeleteExecutionResult
                    .RequestHashConflict();
            }

            var receipt = ValidateStoredResult(
                existing,
                context.Subject);
            await RecordDuplicateAsync(
                connection,
                transaction,
                existing.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return KitBagItemDeleteExecutionResult.Duplicate(receipt);
        }

        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                context.Subject.AccountId,
                context.Subject.CharacterId,
                _commandTimeoutSeconds,
                cancellationToken))
        {
            return KitBagItemDeleteExecutionResult.PreconditionFailed();
        }

        var item = await LockKitBagSlotAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            context.Command.KitBagSlot,
            cancellationToken);
        var authoritativeState =
            item?.Item.ToCompactString() ?? "[]";
        var status = ResolveStatus(
            context.Command.ExpectedCompactItemState,
            authoritativeState);
        if (status != KitBagItemDeleteResultStatus.Deleted)
        {
            var receipt = await PersistResultEvidenceAsync(
                connection,
                transaction,
                context,
                principalKey,
                aggregateKey,
                operationId,
                requestHash,
                status,
                authoritativeState,
                inventoryRevision.Value,
                outboxEventId: null,
                cancellationToken);
            await ReachAsync(
                PostgresKitBagItemDeleteCommandStage.BeforeCommit,
                0,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await ReachAsync(
                PostgresKitBagItemDeleteCommandStage.AfterCommit,
                0,
                cancellationToken);
            return KitBagItemDeleteExecutionResult
                .TerminalRejected(receipt.Receipt);
        }

        if (item is null)
        {
            throw new InvalidDataException(
                "A validated non-empty deletion has no locked item.");
        }

        return await PersistDeletionAsync(
            connection,
            transaction,
            context,
            item,
            inventoryRevision.Value,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
    }

    private async Task<KitBagItemDeleteExecutionResult>
        PersistDeletionAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            KitBagItemDeleteCommandContext context,
            LockedKitBagItem item,
            long inventoryRevision,
            string principalKey,
            string aggregateKey,
            byte[] operationId,
            byte[] requestHash,
            CancellationToken cancellationToken)
    {
        var nextRevision = checked(inventoryRevision + 1);
        var eventId = Guid.NewGuid();
        var evidence = await PersistResultEvidenceAsync(
            connection,
            transaction,
            context,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            KitBagItemDeleteResultStatus.Deleted,
            item.Item.ToCompactString(),
            nextRevision,
            eventId,
            cancellationToken);
        await DeleteItemAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            item,
            cancellationToken);
        await ReachAsync(
            PostgresKitBagItemDeleteCommandStage.ItemDeleted,
            0,
            cancellationToken);
        await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            context.Subject,
            inventoryRevision,
            nextRevision,
            cancellationToken);
        await ReachAsync(
            PostgresKitBagItemDeleteCommandStage
                .InventoryRevisionAdvanced,
            0,
            cancellationToken);
        await InsertInventoryLedgerAsync(
            connection,
            transaction,
            evidence.InboxId,
            context.Subject,
            nextRevision,
            item,
            cancellationToken);
        await ReachAsync(
            PostgresKitBagItemDeleteCommandStage
                .InventoryLedgerInserted,
            0,
            cancellationToken);
        await InsertOutboxAsync(
            connection,
            transaction,
            evidence.InboxId,
            aggregateKey,
            nextRevision,
            eventId,
            evidence.Payload,
            cancellationToken);
        await ReachAsync(
            PostgresKitBagItemDeleteCommandStage.OutboxInserted,
            0,
            cancellationToken);
        await ReachAsync(
            PostgresKitBagItemDeleteCommandStage.BeforeCommit,
            0,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ReachAsync(
            PostgresKitBagItemDeleteCommandStage.AfterCommit,
            0,
            cancellationToken);
        return KitBagItemDeleteExecutionResult.Committed(
            evidence.Receipt);
    }

    private async ValueTask ReachAsync(
        PostgresKitBagItemDeleteCommandStage stage,
        int ordinal,
        CancellationToken cancellationToken)
    {
        if (_probe is not null)
        {
            await _probe.ReachedAsync(
                stage,
                ordinal,
                cancellationToken);
        }
    }

    private static KitBagItemDeleteResultStatus ResolveStatus(
        string expectedState,
        string authoritativeState)
    {
        if (string.Equals(
                expectedState,
                authoritativeState,
                StringComparison.Ordinal))
        {
            return expectedState == "[]"
                ? KitBagItemDeleteResultStatus.EmptySlot
                : KitBagItemDeleteResultStatus.Deleted;
        }

        return KitBagItemDeleteResultStatus.StaleSelection;
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

    private static string OutcomeCode(
        KitBagItemDeleteExecutionDisposition disposition) =>
        disposition switch
        {
            KitBagItemDeleteExecutionDisposition.Committed =>
                "committed",
            KitBagItemDeleteExecutionDisposition.Duplicate =>
                "duplicate",
            KitBagItemDeleteExecutionDisposition.TerminalRejected =>
                "terminal_rejected",
            KitBagItemDeleteExecutionDisposition.ReplayNotFound =>
                "replay_not_found",
            KitBagItemDeleteExecutionDisposition.RequestHashConflict =>
                "request_hash_conflict",
            KitBagItemDeleteExecutionDisposition.InvalidIntent =>
                "invalid_intent",
            KitBagItemDeleteExecutionDisposition.PreconditionFailed =>
                "precondition_failed",
            _ => throw new ArgumentOutOfRangeException(
                nameof(disposition))
        };

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

    private NpgsqlCommand CreateCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _commandTimeoutSeconds
        };

    private sealed record KitBagItemDeleteCommandContext(
        CommandSubject Subject,
        string OperationId,
        string RequestHash,
        KitBagItemDeleteCommand Command);

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

    private sealed record PersistedResultEvidence(
        long InboxId,
        byte[] Payload,
        KitBagItemDeleteExecutionReceipt Receipt);
}
