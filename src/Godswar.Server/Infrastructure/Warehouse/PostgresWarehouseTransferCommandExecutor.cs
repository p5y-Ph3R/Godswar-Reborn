using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Items;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Warehouse;

internal sealed partial class PostgresWarehouseTransferCommandExecutor :
    IWarehouseTransferCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard;
    private readonly IItemTemplateCatalog _itemTemplates;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly IPostgresWarehouseCommandProbe? _probe;

    public PostgresWarehouseTransferCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        IItemTemplateCatalog itemTemplates,
        IPostgresWarehouseCommandProbe? probe = null)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _ownershipGuard = new PostgresPlayerOwnershipGuard(_dataSource);
        _itemTemplates = itemTemplates ??
            throw new ArgumentNullException(nameof(itemTemplates));
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _commandTimeoutSeconds = Math.Max(
            1,
            (int)Math.Ceiling(options.CommandTimeout.TotalSeconds));
        _maximumOutboxAttempts = checked((short)options.MaximumDeliveryAttempts);
        _probe = probe;
    }

    public async Task<WarehouseTransferExecutionResult> ExecuteAsync(
        CommandEnvelope<WarehouseTransferCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation = WarehouseTransferCommandEnvelope.Validate(envelope);
            if (validation == CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return WarehouseTransferExecutionResult.Terminal(
                    WarehouseTransferExecutionDisposition.RequestHashConflict);
            }
            if (validation != CommandEnvelopeValidation.Valid ||
                !IsCanonicalState(
                    envelope.Command.ExpectedSourceCompactItemState) ||
                !IsCanonicalState(
                    envelope.Command.ExpectedDestinationCompactItemState))
            {
                outcome = "invalid_intent";
                return WarehouseTransferExecutionResult.Terminal(
                    WarehouseTransferExecutionDisposition.InvalidIntent);
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
                WarehouseTransferPersistenceCodec.CommandFamilyCode,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    public async Task<WarehouseTransferExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        WarehouseTransferReplayIntent intent,
        WarehouseOperationIdentity identity,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidReplay(subject, intent, identity))
        {
            return WarehouseTransferExecutionResult.Terminal(
                WarehouseTransferExecutionDisposition.InvalidIntent);
        }

        var operationId = DecodeDigest(
            CommandEnvelopeContract.DeriveOperationId(
                CommandFamily.WarehouseTransfer,
                subject,
                WarehouseCommandIdentityRules.CreateScope(identity)));
        var principalKey = subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey = WarehouseTransferPersistenceCodec.AggregateKey(
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
            return WarehouseTransferExecutionResult.Terminal(
                WarehouseTransferExecutionDisposition.PreconditionFailed);
        }
        ownershipResult.RequireCurrent();
        if (await LockCharacterAsync(
                connection,
                transaction,
                subject,
                intent.RealmId,
                cancellationToken) is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return WarehouseTransferExecutionResult.Terminal(
                WarehouseTransferExecutionDisposition.PreconditionFailed);
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
            return WarehouseTransferExecutionResult.Terminal(
                WarehouseTransferExecutionDisposition.ReplayNotFound);
        }
        var receipt = ValidateStored(stored, subject);
        if (!MatchesStableIntent(receipt, intent))
        {
            await UpdateInboxEvidenceAsync(
                connection,
                transaction,
                stored.InboxId,
                duplicate: false,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return WarehouseTransferExecutionResult.Terminal(
                WarehouseTransferExecutionDisposition.RequestHashConflict);
        }
        await UpdateInboxEvidenceAsync(
            connection,
            transaction,
            stored.InboxId,
            duplicate: true,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        (await _ownershipGuard.ValidateCurrentAsync(
            subject,
            ownership,
            cancellationToken)).RequireCurrent();
        return WarehouseTransferExecutionResult.Terminal(
            WarehouseTransferExecutionDisposition.Duplicate,
            receipt);
    }

    private async Task<WarehouseTransferExecutionResult>
        ExecuteTransactionAsync(
            CommandEnvelope<WarehouseTransferCommand> envelope,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(envelope.OperationId);
        var requestHash = DecodeDigest(envelope.RequestHash);
        var principalKey = envelope.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey = WarehouseTransferPersistenceCodec.AggregateKey(
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
            return WarehouseTransferExecutionResult.Terminal(
                WarehouseTransferExecutionDisposition.PreconditionFailed);
        }
        ownership.RequireCurrent();
        var character = await LockCharacterAsync(
            connection,
            transaction,
            envelope.Subject,
            envelope.Command.RealmId,
            cancellationToken);
        if (character is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return WarehouseTransferExecutionResult.Terminal(
                WarehouseTransferExecutionDisposition.PreconditionFailed);
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
            var receipt = ValidateStored(existing, envelope.Subject);
            if (!CryptographicOperations.FixedTimeEquals(
                    existing.RequestHash,
                    requestHash) ||
                !MatchesCommand(receipt, envelope.Command))
            {
                await UpdateInboxEvidenceAsync(
                    connection,
                    transaction,
                    existing.InboxId,
                    duplicate: false,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return WarehouseTransferExecutionResult.Terminal(
                    WarehouseTransferExecutionDisposition.RequestHashConflict);
            }
            await UpdateInboxEvidenceAsync(
                connection,
                transaction,
                existing.InboxId,
                duplicate: true,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return WarehouseTransferExecutionResult.Terminal(
                WarehouseTransferExecutionDisposition.Duplicate,
                receipt);
        }

        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                envelope.Subject.AccountId,
                envelope.Subject.CharacterId,
                _commandTimeoutSeconds,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return WarehouseTransferExecutionResult.Terminal(
                WarehouseTransferExecutionDisposition.PreconditionFailed);
        }

        var plan = await BuildPlanAsync(
            connection,
            transaction,
            envelope.Command,
            envelope.Subject.CharacterId,
            character,
            cancellationToken);
        var eventId = plan.Succeeded ? Guid.NewGuid() : (Guid?)null;
        var nextRevision = plan.Succeeded
            ? checked(character.InventoryRevision + 1)
            : character.InventoryRevision;
        var evidence = await PersistEvidenceAsync(
            connection,
            transaction,
            envelope,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            plan,
            character,
            nextRevision,
            eventId,
            cancellationToken);
        if (!plan.Succeeded)
        {
            await transaction.CommitAsync(cancellationToken);
            return WarehouseTransferExecutionResult.Terminal(
                WarehouseTransferExecutionDisposition.TerminalRejected,
                evidence.Receipt);
        }

        await ApplyPlanAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            plan,
            cancellationToken);
        await AdvanceInventoryRevisionAsync(
            connection,
            transaction,
            envelope.Subject,
            character.InventoryRevision,
            nextRevision,
            cancellationToken);
        await InsertPlanLedgersAsync(
            connection,
            transaction,
            envelope.Subject,
            evidence.InboxId,
            nextRevision,
            plan,
            cancellationToken);
        await InsertTransferOutboxAsync(
            connection,
            transaction,
            evidence.InboxId,
            aggregateKey,
            nextRevision,
            eventId!.Value,
            evidence.Payload,
            cancellationToken);
        if (_probe is not null)
        {
            await _probe.ReachedAsync(
                PostgresWarehouseCommandStage.TransferBeforeCommit,
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return WarehouseTransferExecutionResult.Terminal(
            WarehouseTransferExecutionDisposition.Committed,
            evidence.Receipt);
    }

    private NpgsqlCommand CreateCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _commandTimeoutSeconds
        };

    private static bool IsCanonicalState(string state)
    {
        try
        {
            return string.Equals(
                CompactItemEntry.Parse(state).ToCompactString(),
                state,
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is FormatException or OverflowException or
                ArgumentException)
        {
            return false;
        }
    }

    private static bool IsValidReplay(
        CommandSubject subject,
        WarehouseTransferReplayIntent intent,
        WarehouseOperationIdentity identity) =>
        subject.AccountId > 0 &&
        subject.CharacterId > 0 &&
        (identity.IsSecureClient || identity.IsRawLocalServer) &&
        intent.RealmId > 0 &&
        intent.Money == 0 &&
        intent.StorageType == WarehouseStorageType.Normal &&
        Enum.IsDefined(intent.Operation) &&
        intent.Operation switch
        {
            WarehouseTransferOperation.Deposit =>
                (WarehouseCapacityPolicy.IsValidWarehouseSlot(
                     intent.WarehouseSlot) ||
                 intent.WarehouseSlot == -1) &&
                WarehouseCapacityPolicy.IsValidKitBagSlot(intent.KitBagSlot) &&
                intent.DestinationWarehouseSlot == -1,
            WarehouseTransferOperation.Withdraw =>
                WarehouseCapacityPolicy.IsValidWarehouseSlot(
                    intent.WarehouseSlot) &&
                (WarehouseCapacityPolicy.IsValidKitBagSlot(intent.KitBagSlot) ||
                 intent.KitBagSlot == -1) &&
                intent.DestinationWarehouseSlot == -1,
            WarehouseTransferOperation.InternalMove =>
                WarehouseCapacityPolicy.IsValidWarehouseSlot(
                    intent.WarehouseSlot) &&
                intent.KitBagSlot == -1 &&
                WarehouseCapacityPolicy.IsValidWarehouseSlot(
                    intent.DestinationWarehouseSlot) &&
                intent.WarehouseSlot != intent.DestinationWarehouseSlot,
            _ => false
        };

    private static bool MatchesStableIntent(
        WarehouseTransferExecutionReceipt receipt,
        WarehouseTransferReplayIntent intent) =>
        receipt.Operation == intent.Operation &&
        receipt.WarehouseSlot == intent.WarehouseSlot &&
        receipt.KitBagSlot == intent.KitBagSlot &&
        receipt.DestinationWarehouseSlot == intent.DestinationWarehouseSlot;

    private static bool MatchesCommand(
        WarehouseTransferExecutionReceipt receipt,
        WarehouseTransferCommand command) =>
        receipt.Operation == command.Operation &&
        receipt.WarehouseSlot == command.WarehouseSlot &&
        receipt.KitBagSlot == command.KitBagSlot &&
        receipt.DestinationWarehouseSlot == command.DestinationWarehouseSlot;

    private static byte[] DecodeDigest(string digest)
    {
        var value = Convert.FromHexString(digest);
        return value.Length == CommandEnvelopeContract.DigestBytes
            ? value
            : throw new InvalidDataException("Warehouse digest size is invalid.");
    }

    private static string OutcomeCode(
        WarehouseTransferExecutionDisposition disposition) =>
        disposition.ToString().ToLowerInvariant();

    private sealed record LockedCharacter(
        int Capacity,
        long WarehouseRevision,
        long InventoryRevision);

    private sealed record LockedItem(
        long ItemInstanceId,
        short Location,
        short Slot,
        CompactItemEntry Item,
        string BeforeState,
        bool LinkedSealedPet);

    private sealed record TransferPlan(
        WarehouseTransferResultStatus Status,
        LockedItem? Source,
        LockedItem? Destination,
        IReadOnlyList<LockedItem> StackDestinations,
        short SourceLocation,
        short DestinationLocation,
        int SourceSlot,
        int DestinationSlot,
        int ActualWarehouseSlot,
        int ActualKitBagSlot,
        int MovedQuantity,
        int SourceAfterStack,
        IReadOnlyList<WarehouseItemMutation> Mutations)
    {
        public bool Succeeded => Status is
            WarehouseTransferResultStatus.Deposited or
            WarehouseTransferResultStatus.Withdrawn or
            WarehouseTransferResultStatus.InternalMoved or
            WarehouseTransferResultStatus.Stacked or
            WarehouseTransferResultStatus.Swapped;
    }

    private sealed record StoredInbox(
        long InboxId,
        byte[] RequestHash,
        short ContractVersion,
        string ResultCode,
        string ResultPayload,
        byte[] ResultHash,
        long AuditId);

    private sealed record PersistedEvidence(
        long InboxId,
        byte[] Payload,
        WarehouseTransferExecutionReceipt Receipt);
}
