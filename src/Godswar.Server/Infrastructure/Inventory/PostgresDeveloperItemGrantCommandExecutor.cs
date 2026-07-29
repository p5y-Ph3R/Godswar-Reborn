using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresDeveloperItemGrantCommandExecutor :
    IDeveloperItemGrantCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly IPostgresDeveloperItemGrantCommandProbe? _probe;

    public PostgresDeveloperItemGrantCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        IPostgresDeveloperItemGrantCommandProbe? probe = null)
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

    public async Task<DeveloperItemGrantExecutionResult> ExecuteAsync(
        CommandEnvelope<DeveloperItemGrantCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation =
                DeveloperItemGrantCommandEnvelope.Validate(envelope);
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return DeveloperItemGrantExecutionResult
                    .RequestHashConflict();
            }

            if (validation != CommandEnvelopeValidation.Valid)
            {
                outcome = "invalid_intent";
                return DeveloperItemGrantExecutionResult.InvalidIntent();
            }

            var result = await ExecuteTransactionAsync(
                envelope,
                cancellationToken);
            outcome = result.Disposition switch
            {
                DeveloperItemGrantExecutionDisposition.Committed =>
                    "committed",
                DeveloperItemGrantExecutionDisposition.Duplicate =>
                    "duplicate",
                DeveloperItemGrantExecutionDisposition
                    .RequestHashConflict =>
                    "request_hash_conflict",
                DeveloperItemGrantExecutionDisposition.InvalidIntent =>
                    "invalid_intent",
                DeveloperItemGrantExecutionDisposition
                    .PreconditionFailed =>
                    "precondition_failed",
                _ => throw new InvalidOperationException(
                    "Unknown inventory grant command outcome.")
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
                DeveloperItemGrantPersistenceCodec.CommandFamily,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<DeveloperItemGrantExecutionResult>
        ExecuteTransactionAsync(
            CommandEnvelope<DeveloperItemGrantCommand> envelope,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(envelope.OperationId);
        var requestHash = DecodeDigest(envelope.RequestHash);
        var principalKey = envelope.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey =
            DeveloperItemGrantPersistenceCodec.AggregateKey(
                envelope.Subject.CharacterId);

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        var character = await LockCharacterAsync(
            connection,
            transaction,
            envelope,
            cancellationToken);
        if (character is null)
        {
            return DeveloperItemGrantExecutionResult.PreconditionFailed();
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
                return DeveloperItemGrantExecutionResult
                    .RequestHashConflict();
            }

            var replayReceipt = ValidateStoredResult(existing);
            await RecordDuplicateAsync(
                connection,
                transaction,
                existing.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return DeveloperItemGrantExecutionResult.Duplicate(
                replayReceipt);
        }

        if (!DeveloperGrantMaterialCatalog.TryResolve(
                envelope.Command.ItemId,
                out var material))
        {
            return DeveloperItemGrantExecutionResult.InvalidIntent();
        }

        if (!await EnsureCharacterEconomyBaselineAsync(
                connection,
                transaction,
                envelope,
                cancellationToken))
        {
            return DeveloperItemGrantExecutionResult.PreconditionFailed();
        }

        var items = await LockKitBagAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            material,
            cancellationToken);
        if (!HasCapacity(items, material, envelope.Command.Quantity))
        {
            return DeveloperItemGrantExecutionResult.PreconditionFailed();
        }

        var inventoryRevision =
            checked(character.Value.InventoryRevision + 1);
        var eventId = Guid.NewGuid();
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            envelope,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
        var receipt = new DeveloperItemGrantExecutionReceipt(
            envelope.Subject.CharacterId,
            envelope.Command.ItemId,
            envelope.Command.Quantity,
            inventoryRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            eventId);
        var payload =
            DeveloperItemGrantPersistenceCodec.Encode(receipt);
        var resultHash =
            DeveloperItemGrantPersistenceCodec.Hash(payload);

        await ReachAsync(
            PostgresDeveloperItemGrantCommandStage.AuditInserted,
            cancellationToken);
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
        await ReachAsync(
            PostgresDeveloperItemGrantCommandStage.InboxInserted,
            cancellationToken);

        var mutations = await ApplyInventoryGrantAsync(
            connection,
            transaction,
            envelope,
            material,
            items,
            inventoryRevision,
            cancellationToken);
        await ReachAsync(
            PostgresDeveloperItemGrantCommandStage.InventoryMutated,
            cancellationToken);
        await InsertInventoryLedgerAsync(
            connection,
            transaction,
            inboxId,
            envelope,
            inventoryRevision,
            mutations,
            cancellationToken);
        await ReachAsync(
            PostgresDeveloperItemGrantCommandStage.LedgerInserted,
            cancellationToken);

        await InsertOutboxAsync(
            connection,
            transaction,
            inboxId,
            aggregateKey,
            inventoryRevision,
            eventId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresDeveloperItemGrantCommandStage.OutboxInserted,
            cancellationToken);
        await ReachAsync(
            PostgresDeveloperItemGrantCommandStage.BeforeCommit,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ReachAsync(
            PostgresDeveloperItemGrantCommandStage.AfterCommit,
            cancellationToken);
        return DeveloperItemGrantExecutionResult.Committed(receipt);
    }

    private async ValueTask ReachAsync(
        PostgresDeveloperItemGrantCommandStage stage,
        CancellationToken cancellationToken)
    {
        if (_probe is not null)
        {
            await _probe.ReachedAsync(stage, cancellationToken);
        }
    }

    private static bool HasCapacity(
        LockedKitBag items,
        DeveloperGrantMaterialDefinition material,
        int quantity)
    {
        var stackCapacity = items.FillableStacks.Sum(item =>
            Math.Max(0, material.StackCap - item.Stack));
        var slotCapacity =
            (long)items.EmptySlots.Count * material.StackCap;
        return stackCapacity + slotCapacity >= quantity;
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

    private readonly record struct LockedCharacter(
        long InventoryRevision);

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
        short Stack,
        string BeforeState);

    private sealed record LockedKitBag(
        IReadOnlyList<LockedKitBagItem> FillableStacks,
        IReadOnlyList<short> EmptySlots);

    private sealed record InventoryMutation(
        long ItemInstanceId,
        string MutationKind,
        string? BeforeState,
        string? AfterState);
}
