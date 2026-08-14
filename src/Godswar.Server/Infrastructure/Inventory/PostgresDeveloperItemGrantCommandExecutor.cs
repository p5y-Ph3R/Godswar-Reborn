using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresDeveloperItemGrantCommandExecutor :
    IDeveloperItemGrantCommandExecutor
{
    private const uint FirstSocketSpellItemId = 4270;
    private const uint LastSocketSpellItemId = 4273;

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly IPostgresDeveloperItemGrantCommandProbe? _probe;
    private readonly GameplayItemContent _itemContent;

    public PostgresDeveloperItemGrantCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        GameplayItemContent itemContent,
        IPostgresDeveloperItemGrantCommandProbe? probe = null)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _ownershipGuard = new PostgresPlayerOwnershipGuard(_dataSource);
        _itemContent = itemContent ?? throw new ArgumentNullException(
            nameof(itemContent));
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
            if (result.Receipt is not null)
            {
                (await _ownershipGuard.ValidateCurrentAsync(
                    envelope.Subject,
                    envelope.Ownership,
                    cancellationToken)).RequireCurrent();
            }
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

        var ownership = await _ownershipGuard.LockCurrentAsync(
            connection,
            transaction,
            envelope.Subject,
            envelope.Ownership,
            cancellationToken);
        if (ownership.Status ==
            PlayerOwnershipValidationStatus.CharacterNotFound)
        {
            await transaction.RollbackAsync(cancellationToken);
            return DeveloperItemGrantExecutionResult
                .PreconditionFailed();
        }
        ownership.RequireCurrent();

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
                ? DeveloperItemGrantExecutionResult
                    .PreconditionFailed()
                : DeveloperItemGrantExecutionResult.Duplicate(
                    replayReceipt);
        }

        if (!TryResolveGrantItem(
                envelope.Command.ItemId,
                out var grantItem))
        {
            return DeveloperItemGrantExecutionResult.InvalidIntent();
        }

        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                envelope.Subject.AccountId,
                envelope.Subject.CharacterId,
                _commandTimeoutSeconds,
                cancellationToken))
        {
            return DeveloperItemGrantExecutionResult.PreconditionFailed();
        }

        var items = await LockKitBagAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            grantItem,
            cancellationToken);
        if (!HasCapacity(items, grantItem, envelope.Command.Quantity))
        {
            await InsertInsufficientCapacityResultAsync(
                connection,
                transaction,
                envelope,
                principalKey,
                aggregateKey,
                operationId,
                requestHash,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
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
            grantItem,
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
            grantItem.LedgerReasonCode,
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
        DeveloperGrantItemDefinition grantItem,
        int quantity)
    {
        var stackCapacity = items.FillableStacks.Sum(item =>
            Math.Max(0, grantItem.StackCap - item.Stack));
        var slotCapacity =
            (long)items.EmptySlots.Count * grantItem.StackCap;
        return stackCapacity + slotCapacity >= quantity;
    }

    private bool TryResolveGrantItem(
        uint itemId,
        out DeveloperGrantItemDefinition grantItem)
    {
        if (_itemContent.Templates.Materials.TryResolveDeveloper(
                itemId,
                out var material))
        {
            grantItem = new DeveloperGrantItemDefinition(
                material.ItemId,
                material.StackCap,
                material.GrantedBound,
                "developer_material_grant");
            return true;
        }

        if (_itemContent.DeveloperItems.TryResolveDeveloper(
                itemId,
                out var catalogItem))
        {
            grantItem = new DeveloperGrantItemDefinition(
                catalogItem.ItemId,
                catalogItem.StackCap,
                catalogItem.GrantedBound,
                itemId == PinnedDeveloperItemGrantCatalog
                    .PermanentChristmasCostumeItemId
                    ? "developer_costume_grant"
                    : itemId is >= FirstSocketSpellItemId and
                        <= LastSocketSpellItemId
                    ? "developer_socket_spell_grant"
                    : PinnedDeveloperItemGrantCatalog
                        .IsPetConsumableDeveloperGrant(itemId)
                    ? "developer_pet_consumable_grant"
                    : "developer_empty_holy_box_grant");
            return true;
        }

        if (_itemContent.DeveloperMounts.TryResolveGrantable(
                itemId,
                out _))
        {
            grantItem = new DeveloperGrantItemDefinition(
                itemId,
                StackCap: 1,
                GrantedBound: 1,
                LedgerReasonCode: "developer_mount_grant");
            return true;
        }

        grantItem = default;
        return false;
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

    private readonly record struct DeveloperGrantItemDefinition(
        uint ItemId,
        short StackCap,
        short GrantedBound,
        string LedgerReasonCode);
}
