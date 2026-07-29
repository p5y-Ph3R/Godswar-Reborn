using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class
    PostgresEquipmentBagTransferCommandExecutor :
    IEquipmentBagTransferCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly IPostgresEquipmentBagTransferCommandProbe? _probe;

    public PostgresEquipmentBagTransferCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        IPostgresEquipmentBagTransferCommandProbe? probe = null)
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

    public async Task<EquipmentBagTransferExecutionResult>
        ExecuteAsync(
            CommandEnvelope<EquipmentBagTransferCommand> envelope,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation =
                EquipmentBagTransferCommandEnvelope.Validate(envelope);
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return EquipmentBagTransferExecutionResult
                    .RequestHashConflict();
            }
            if (validation != CommandEnvelopeValidation.Valid ||
                !IsCanonicalCompactItemState(
                    envelope.Command
                        .ExpectedEquipmentCompactItemState) ||
                !IsCanonicalCompactItemState(
                    envelope.Command
                        .ExpectedKitBagCompactItemState))
            {
                outcome = "invalid_intent";
                return EquipmentBagTransferExecutionResult
                    .InvalidIntent();
            }

            var context = new TransferCommandContext(
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
                EquipmentBagTransferPersistenceCodec
                    .CommandFamilyCode,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    public async Task<EquipmentBagTransferExecutionResult>
        TryReplayAsync(
            CommandSubject subject,
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

    private async Task<EquipmentBagTransferExecutionResult>
        ExecuteTransactionAsync(
            TransferCommandContext context,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(context.OperationId);
        var requestHash = DecodeDigest(context.RequestHash);
        var principalKey = context.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey =
            EquipmentBagTransferPersistenceCodec.AggregateKey(
                context.Subject.CharacterId);

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var character = await LockCharacterAsync(
            connection,
            transaction,
            context.Subject,
            cancellationToken);
        if (character is null)
        {
            return EquipmentBagTransferExecutionResult
                .PreconditionFailed();
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
                    context.Command.EquipmentSlot,
                    context.Command.KitBagSlot) ||
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
                return EquipmentBagTransferExecutionResult
                    .RequestHashConflict();
            }

            await RecordDuplicateAsync(
                connection,
                transaction,
                existing.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return EquipmentBagTransferExecutionResult
                .Duplicate(receipt);
        }

        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                context.Subject.AccountId,
                context.Subject.CharacterId,
                _commandTimeoutSeconds,
                cancellationToken))
        {
            return EquipmentBagTransferExecutionResult
                .PreconditionFailed();
        }

        var slots = await LockTransferSlotsAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            context.Command.EquipmentSlot,
            context.Command.KitBagSlot,
            cancellationToken);
        if (slots.Equipment?.Item.IsEmpty == true ||
            slots.KitBag?.Item.IsEmpty == true)
        {
            throw new InvalidDataException(
                "A physical equipment transfer row decoded as an " +
                "empty item.");
        }
        var equipmentState =
            slots.Equipment?.Item.ToCompactString() ?? "[]";
        var kitBagState =
            slots.KitBag?.Item.ToCompactString() ?? "[]";
        var status = ResolveStateStatus(
            context.Command,
            equipmentState,
            kitBagState);
        if (status is null)
        {
            status = await ValidateTransferAsync(
                connection,
                transaction,
                context,
                character,
                slots,
                cancellationToken);
        }

        if (status is not EquipmentBagTransferResultStatus.Equipped
            and not EquipmentBagTransferResultStatus.Unequipped)
        {
            var evidence = await PersistResultEvidenceAsync(
                connection,
                transaction,
                context,
                principalKey,
                aggregateKey,
                operationId,
                requestHash,
                status.Value,
                equipmentState,
                kitBagState,
                character.InventoryRevision,
                null,
                cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return EquipmentBagTransferExecutionResult
                .TerminalRejected(evidence.Receipt);
        }

        return await PersistTransferAsync(
            connection,
            transaction,
            context,
            slots,
            status.Value,
            character.InventoryRevision,
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
            PostgresEquipmentBagTransferCommandStage.BeforeCommit,
            0,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ReachAsync(
            PostgresEquipmentBagTransferCommandStage.AfterCommit,
            0,
            cancellationToken);
    }

    private async ValueTask ReachAsync(
        PostgresEquipmentBagTransferCommandStage stage,
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

    private static EquipmentBagTransferResultStatus?
        ResolveStateStatus(
            EquipmentBagTransferCommand command,
            string equipmentState,
            string kitBagState)
    {
        if (!string.Equals(
                command.ExpectedEquipmentCompactItemState,
                equipmentState,
                StringComparison.Ordinal))
        {
            return EquipmentBagTransferResultStatus
                .StaleEquipment;
        }
        if (!string.Equals(
                command.ExpectedKitBagCompactItemState,
                kitBagState,
                StringComparison.Ordinal))
        {
            return EquipmentBagTransferResultStatus.StaleKitBag;
        }
        if (command.MountRuntimeBlocked &&
            (equipmentState == "[]") != (kitBagState == "[]"))
        {
            return EquipmentBagTransferResultStatus
                .RideRuntimeBlocked;
        }
        if (equipmentState == "[]" && kitBagState == "[]")
        {
            return EquipmentBagTransferResultStatus.BothEmpty;
        }
        if (equipmentState != "[]" && kitBagState != "[]")
        {
            return EquipmentBagTransferResultStatus.BothOccupied;
        }
        return null;
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
        int equipmentSlot,
        int kitBagSlot) =>
        subject.AccountId > 0 &&
        subject.CharacterId > 0 &&
        operationId != Guid.Empty &&
        equipmentSlot is >=
                EquipmentBagTransferCommandEnvelope.MinimumEquipmentSlot
            and <=
                EquipmentBagTransferCommandEnvelope.MaximumEquipmentSlot &&
        kitBagSlot is >=
                EquipmentBagTransferCommandEnvelope.MinimumKitBagSlot
            and <=
                EquipmentBagTransferCommandEnvelope.MaximumKitBagSlot;

    private static bool IsSupportedEquipmentSlot(int slot) =>
        slot is >= EquipmentSlots.Head and <= EquipmentSlots.Stylish ||
        slot is >= EquipmentSlots.MountHead and <= EquipmentSlots.Mount;

    private static bool HasRequestedSlots(
        EquipmentBagTransferExecutionReceipt receipt,
        int equipmentSlot,
        int kitBagSlot) =>
        receipt.EquipmentSlot == equipmentSlot &&
        receipt.KitBagSlot == kitBagSlot;

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
        EquipmentBagTransferDisposition disposition) =>
        disposition switch
        {
            EquipmentBagTransferDisposition.Committed =>
                "committed",
            EquipmentBagTransferDisposition.Duplicate =>
                "duplicate",
            EquipmentBagTransferDisposition.TerminalRejected =>
                "terminal_rejected",
            EquipmentBagTransferDisposition.ReplayNotFound =>
                "replay_not_found",
            EquipmentBagTransferDisposition
                .RequestHashConflict =>
                "request_hash_conflict",
            EquipmentBagTransferDisposition.InvalidIntent =>
                "invalid_intent",
            EquipmentBagTransferDisposition
                .PreconditionFailed =>
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

    private sealed record TransferCommandContext(
        CommandSubject Subject,
        string OperationId,
        string RequestHash,
        EquipmentBagTransferCommand Command);

    private sealed record LockedCharacter(
        long InventoryRevision,
        short Profession,
        int CharacterLevel);

    private sealed record StoredInbox(
        long InboxId,
        byte[] RequestHash,
        short ResultContractVersion,
        string ResultCode,
        string ResultPayload,
        byte[] ResultHash,
        long AuditId);

    private sealed record LockedItem(
        long ItemInstanceId,
        short Location,
        short SlotIndex,
        CompactItemEntry Item,
        string BeforeState);

    private sealed record LockedTransferSlots(
        LockedItem? Equipment,
        LockedItem? KitBag);

    private sealed record PersistedResultEvidence(
        long InboxId,
        byte[] Payload,
        EquipmentBagTransferExecutionReceipt Receipt);
}
