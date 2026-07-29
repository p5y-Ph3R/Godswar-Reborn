using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresDeveloperBagClearCommandExecutor :
    IDeveloperBagClearCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;

    public PostgresDeveloperBagClearCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options)
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
    }

    public async Task<DeveloperBagClearExecutionResult> ExecuteAsync(
        CommandEnvelope<DeveloperBagClearCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation =
                DeveloperBagClearCommandEnvelope.Validate(envelope);
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return DeveloperBagClearExecutionResult
                    .RequestHashConflict();
            }

            if (validation != CommandEnvelopeValidation.Valid)
            {
                outcome = "invalid_intent";
                return DeveloperBagClearExecutionResult.InvalidIntent();
            }

            var result = await ExecuteTransactionAsync(
                envelope,
                cancellationToken);
            outcome = result.Disposition switch
            {
                DeveloperBagClearExecutionDisposition.Committed =>
                    "committed",
                DeveloperBagClearExecutionDisposition.Duplicate =>
                    "duplicate",
                DeveloperBagClearExecutionDisposition
                    .RequestHashConflict =>
                    "request_hash_conflict",
                DeveloperBagClearExecutionDisposition.InvalidIntent =>
                    "invalid_intent",
                DeveloperBagClearExecutionDisposition
                    .PreconditionFailed =>
                    "precondition_failed",
                _ => throw new InvalidOperationException(
                    "Unknown bag-clear command outcome.")
            };
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
                DeveloperBagClearPersistenceCodec.CommandFamily,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<DeveloperBagClearExecutionResult>
        ExecuteTransactionAsync(
            CommandEnvelope<DeveloperBagClearCommand> envelope,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(envelope.OperationId);
        var requestHash = DecodeDigest(envelope.RequestHash);
        var principalKey = envelope.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey =
            DeveloperBagClearPersistenceCodec.AggregateKey(
                envelope.Subject.CharacterId);

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        var inventoryRevision = await LockCharacterAsync(
            connection,
            transaction,
            envelope,
            cancellationToken);
        if (!inventoryRevision.HasValue)
        {
            return DeveloperBagClearExecutionResult.PreconditionFailed();
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
                return DeveloperBagClearExecutionResult
                    .RequestHashConflict();
            }

            var replayReceipt = ValidateStoredResult(
                existing,
                envelope);
            await RecordDuplicateAsync(
                connection,
                transaction,
                existing.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return replayReceipt is null
                ? DeveloperBagClearExecutionResult.PreconditionFailed()
                : DeveloperBagClearExecutionResult.Duplicate(
                    replayReceipt);
        }

        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                envelope.Subject.AccountId,
                envelope.Subject.CharacterId,
                _commandTimeoutSeconds,
                cancellationToken))
        {
            return DeveloperBagClearExecutionResult.PreconditionFailed();
        }

        var items = await LockKitBagAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        if (items.Count == 0)
        {
            await InsertEmptyBagResultAsync(
                connection,
                transaction,
                envelope,
                principalKey,
                aggregateKey,
                operationId,
                requestHash,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return DeveloperBagClearExecutionResult.PreconditionFailed();
        }

        var nextRevision = checked(inventoryRevision.Value + 1);
        var eventId = Guid.NewGuid();
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            envelope,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            items.Count,
            cancellationToken);
        var receipt = new DeveloperBagClearExecutionReceipt(
            envelope.Subject.CharacterId,
            items.Select(static item => item.SlotIndex).ToArray(),
            nextRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            eventId);
        var payload = DeveloperBagClearPersistenceCodec.Encode(receipt);
        var resultHash =
            DeveloperBagClearPersistenceCodec.Hash(payload);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            resultHash,
            auditId,
            payload,
            cancellationToken);

        await DeleteItemsAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            items,
            cancellationToken);
        await AdvanceRevisionAsync(
            connection,
            transaction,
            envelope,
            inventoryRevision.Value,
            nextRevision,
            cancellationToken);
        await InsertInventoryLedgerAsync(
            connection,
            transaction,
            inboxId,
            envelope,
            nextRevision,
            items,
            cancellationToken);
        await InsertOutboxAsync(
            connection,
            transaction,
            inboxId,
            aggregateKey,
            nextRevision,
            eventId,
            payload,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return DeveloperBagClearExecutionResult.Committed(receipt);
    }

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
        string BeforeState);
}
