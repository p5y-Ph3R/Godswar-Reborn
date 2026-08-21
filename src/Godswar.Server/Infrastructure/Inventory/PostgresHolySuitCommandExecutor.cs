using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Items;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolySuitCommandExecutor :
    IHolySuitCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly GameplayItemContent _itemContent;

    public PostgresHolySuitCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        GameplayItemContent itemContent)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        _ownershipGuard = new PostgresPlayerOwnershipGuard(_dataSource);
        _itemContent = itemContent ??
            throw new ArgumentNullException(nameof(itemContent));
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _commandTimeoutSeconds = Math.Max(
            1,
            (int)Math.Ceiling(options.CommandTimeout.TotalSeconds));
        _maximumOutboxAttempts =
            checked((short)options.MaximumDeliveryAttempts);
    }

    public async Task<HolySuitExecutionResult> ExecuteAsync(
        CommandEnvelope<HolySuitCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        CommandFamily? family = envelope.Family;
        var outcome = "provider_unavailable";
        try
        {
            var validation = HolySuitCommandEnvelope.Validate(envelope);
            if (validation == CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return HolySuitExecutionResult.RequestHashConflict();
            }

            if (validation != CommandEnvelopeValidation.Valid ||
                !HasCanonicalExpectedItemStates(envelope.Command) ||
                !_itemContent.HolySuit.IsAvailable)
            {
                outcome = "invalid_intent";
                return HolySuitExecutionResult.InvalidIntent();
            }

            family = HolySuitCommandEnvelope.Family(
                envelope.Command.Operation);
            var context = new HolySuitCommandContext(
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
            if (family.HasValue && IsHolySuitFamily(family.Value))
            {
                PostgresCommandMetrics.RecordInbox(
                    HolySuitPersistenceCodec.CommandFamilyCode(family.Value),
                    outcome,
                    Stopwatch.GetElapsedTime(started));
            }
        }
    }

    private async Task<HolySuitExecutionResult> ExecuteTransactionAsync(
        HolySuitCommandContext context,
        CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(context.OperationId);
        var requestHash = DecodeDigest(context.RequestHash);
        var principalKey = context.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey = HolySuitPersistenceCodec.AggregateKey(
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
            return HolySuitExecutionResult.PreconditionFailed();
        }
        ownership.RequireCurrent();

        var character = await LockCharacterAsync(
            connection,
            transaction,
            context.Subject,
            cancellationToken);
        if (character is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return HolySuitExecutionResult.PreconditionFailed();
        }

        var existing = await ReadInboxAsync(
            connection,
            transaction,
            context.Family,
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
                return HolySuitExecutionResult.RequestHashConflict();
            }

            var replay = ValidateStoredResult(existing, context.Family);
            await RecordDuplicateAsync(
                connection,
                transaction,
                existing.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return HolySuitExecutionResult.Duplicate(replay);
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
            return HolySuitExecutionResult.PreconditionFailed();
        }

        var bag = await LockKitBagAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            cancellationToken);
        var daily = context.Command.Operation ==
            HolySuitCommandOperation.StoreExperience
            ? await LockDailyUsageAsync(
                connection,
                transaction,
                context.Subject.AccountId,
                character.Value.RealmId,
                cancellationToken)
            : DailyUsage.None;
        var battlePass = context.Command.Operation ==
            HolySuitCommandOperation.StoreExperience &&
            await HasActiveBattlePassAsync(
                connection,
                transaction,
                context.Subject.AccountId,
                cancellationToken);
        var plan = CreatePlan(
            context.Command,
            character.Value,
            bag,
            daily,
            battlePass,
            _itemContent.HolySuit,
            _itemContent.Templates);
        if (!plan.Committed)
        {
            var rejected = await PersistTerminalResultAsync(
                connection,
                transaction,
                context,
                character.Value,
                daily,
                battlePass,
                plan.Status,
                principalKey,
                aggregateKey,
                operationId,
                requestHash,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return HolySuitExecutionResult.TerminalRejected(rejected);
        }

        return await PersistCommittedResultAsync(
            connection,
            transaction,
            context,
            character.Value,
            daily,
            battlePass,
            plan,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
    }

    private static bool HasCanonicalExpectedItemStates(
        HolySuitCommand command)
    {
        if (command.Operation == HolySuitCommandOperation.TransformExperience)
        {
            return command.ExpectedPrimaryCompactItemState == "[]" &&
                command.ExpectedSecondaryCompactItemState == "[]";
        }

        return IsCanonical(command.ExpectedPrimaryCompactItemState) &&
            (command.Operation == HolySuitCommandOperation.StoreExperience
                ? command.ExpectedSecondaryCompactItemState == "[]"
                : IsCanonical(command.ExpectedSecondaryCompactItemState));
    }

    private static bool IsCanonical(string value)
    {
        var item = CompactItemEntry.Parse(value);
        return !item.IsEmpty && string.Equals(
            item.ToCompactString(),
            value,
            StringComparison.Ordinal);
    }

    private static string OutcomeCode(
        HolySuitExecutionDisposition disposition) =>
        disposition switch
        {
            HolySuitExecutionDisposition.Committed => "committed",
            HolySuitExecutionDisposition.Duplicate => "duplicate",
            HolySuitExecutionDisposition.TerminalRejected =>
                "terminal_rejected",
            HolySuitExecutionDisposition.ReplayNotFound => "replay_not_found",
            HolySuitExecutionDisposition.RequestHashConflict =>
                "request_hash_conflict",
            HolySuitExecutionDisposition.InvalidIntent => "invalid_intent",
            HolySuitExecutionDisposition.PreconditionFailed =>
                "precondition_failed",
            _ => throw new ArgumentOutOfRangeException(nameof(disposition))
        };

    private static bool IsHolySuitFamily(CommandFamily family) =>
        family is CommandFamily.HolySuitStoreExperience or
            CommandFamily.HolySuitTransferExperience or
            CommandFamily.HolySuitConsumeWare or
            CommandFamily.HolySuitTransformExperience;

    private static byte[] DecodeDigest(string value)
    {
        var bytes = Convert.FromHexString(value);
        if (bytes.Length != CommandEnvelopeContract.DigestBytes)
        {
            throw new InvalidDataException(
                "The Holy Suit command digest has an invalid size.");
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

    private readonly record struct HolySuitCommandContext(
        CommandSubject Subject,
        PlayerOwnershipFence Ownership,
        string OperationId,
        string RequestHash,
        HolySuitCommand Command)
    {
        public CommandFamily Family =>
            HolySuitCommandEnvelope.Family(Command.Operation);
    }

    private readonly record struct LockedCharacter(
        int Level,
        long Experience,
        long ProgressionRevision,
        long InventoryRevision,
        RealmId RealmId);

    private readonly record struct DailyUsage(
        DateOnly UsageDay,
        long StoredExperience)
    {
        public static DailyUsage None => new(default, 0);
    }

    private sealed record StoredInbox(
        long InboxId,
        byte[] RequestHash,
        short ResultContractVersion,
        string ResultCode,
        string ResultPayload,
        byte[] ResultHash,
        long AuditId);

    private sealed record LockedInventoryItem(
        long ItemInstanceId,
        short Slot,
        CompactItemEntry Item,
        string BeforeState);

    private sealed record LockedKitBag(
        IReadOnlyDictionary<short, LockedInventoryItem> Items,
        IReadOnlyList<short> EmptySlots);

    private sealed record PlannedMutation(
        HolySuitReceiptItemRole Role,
        short Slot,
        LockedInventoryItem? Existing,
        CompactItemEntry Before,
        CompactItemEntry After);

    private sealed record HolySuitPlan(
        HolySuitCommandResultStatus Status,
        IReadOnlyList<PlannedMutation> Mutations,
        long CharacterExperienceAfter,
        long DailyStoredExperienceAfter,
        int PrismsCreated,
        int PrismsConsumed,
        long StoredExperience)
    {
        public bool Committed => HolySuitNativeResults.IsCommitted(Status);
    }

    private sealed record AppliedMutation(
        HolySuitReceiptItemRole Role,
        short Slot,
        uint ItemId,
        long ItemInstanceId,
        string BeforeCompactState,
        string AfterCompactState,
        string MutationKind,
        string? BeforeState,
        string? AfterState);
}
