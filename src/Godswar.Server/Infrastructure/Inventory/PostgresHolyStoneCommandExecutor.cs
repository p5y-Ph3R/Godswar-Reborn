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

internal sealed partial class PostgresHolyStoneCommandExecutor :
    IHolyStoneCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly IPostgresHolyStoneCommandProbe? _probe;

    public PostgresHolyStoneCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        IPostgresHolyStoneCommandProbe? probe = null)
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

    public async Task<HolyStoneExecutionResult> ExecuteAsync(
        CommandEnvelope<HolyStoneCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        var operation = envelope.Command.Operation;
        var commandFamilyCode = Enum.IsDefined(operation)
            ? HolyStonePersistenceCodec.CommandFamilyCode(operation)
            : "holy_stone_invalid";
        try
        {
            var validation = HolyStoneCommandEnvelope.Validate(envelope);
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return HolyStoneExecutionResult.RequestHashConflict();
            }
            if (validation != CommandEnvelopeValidation.Valid ||
                !HasCanonicalExpectedStates(envelope.Command))
            {
                outcome = "invalid_intent";
                return HolyStoneExecutionResult.InvalidIntent();
            }

            var context = new HolyStoneCommandContext(
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
                commandFamilyCode,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    public async Task<HolyStoneExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        HolyStoneCommandOperation operation,
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        var commandFamilyCode = Enum.IsDefined(operation)
            ? HolyStonePersistenceCodec.CommandFamilyCode(operation)
            : "holy_stone_invalid";
        try
        {
            if (subject.AccountId <= 0 ||
                subject.CharacterId <= 0 ||
                !Enum.IsDefined(operation) ||
                clientOperationId == Guid.Empty)
            {
                outcome = "invalid_intent";
                return HolyStoneExecutionResult.InvalidIntent();
            }

            var operationId = DecodeDigest(
                HolyStoneCommandEnvelope.CreateOperationId(
                    subject,
                    operation,
                    clientOperationId));
            var principalKey = subject.AccountId.ToString(
                CultureInfo.InvariantCulture);
            var aggregateKey = HolyStonePersistenceCodec.AggregateKey(
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
                return HolyStoneExecutionResult.PreconditionFailed();
            }
            ownershipResult.RequireCurrent();

            if (await LockCharacterAsync(
                    connection,
                    transaction,
                    subject,
                    cancellationToken) is null)
            {
                outcome = "precondition_failed";
                return HolyStoneExecutionResult.PreconditionFailed();
            }

            var stored = await ReadInboxAsync(
                connection,
                transaction,
                operation,
                principalKey,
                aggregateKey,
                operationId,
                cancellationToken);
            if (stored is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                outcome = "replay_not_found";
                return HolyStoneExecutionResult.ReplayNotFound();
            }

            var receipt = ValidateStoredResult(
                stored,
                subject,
                operation);
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
            return HolyStoneExecutionResult.Duplicate(receipt);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            PostgresCommandMetrics.RecordInbox(
                commandFamilyCode,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<HolyStoneExecutionResult>
        ExecuteTransactionAsync(
            HolyStoneCommandContext context,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(context.OperationId);
        var requestHash = DecodeDigest(context.RequestHash);
        var principalKey = context.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey = HolyStonePersistenceCodec.AggregateKey(
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
            return HolyStoneExecutionResult.PreconditionFailed();
        }
        ownership.RequireCurrent();

        var character = await LockCharacterAsync(
            connection,
            transaction,
            context.Subject,
            cancellationToken);
        if (character is null)
        {
            return HolyStoneExecutionResult.PreconditionFailed();
        }

        var existing = await ReadInboxAsync(
            connection,
            transaction,
            context.Command.Operation,
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
                return HolyStoneExecutionResult.RequestHashConflict();
            }

            var receipt = ValidateStoredResult(
                existing,
                context.Subject,
                context.Command.Operation);
            await RecordDuplicateAsync(
                connection,
                transaction,
                existing.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return HolyStoneExecutionResult.Duplicate(receipt);
        }

        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                context.Subject.AccountId,
                context.Subject.CharacterId,
                _commandTimeoutSeconds,
                cancellationToken))
        {
            return HolyStoneExecutionResult.PreconditionFailed();
        }

        var locked = await LockCommandItemsAsync(
            connection,
            transaction,
            context,
            cancellationToken);
        var plan = await CreatePlanAsync(
            connection,
            transaction,
            context,
            character,
            locked,
            cancellationToken);
        if (!plan.IsSuccess)
        {
            var receipt = await PersistTerminalResultAsync(
                connection,
                transaction,
                context,
                character,
                locked,
                plan,
                principalKey,
                aggregateKey,
                operationId,
                requestHash,
                cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return HolyStoneExecutionResult.TerminalRejected(receipt);
        }

        return await PersistSuccessAsync(
            connection,
            transaction,
            context,
            character,
            locked,
            plan,
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
            PostgresHolyStoneCommandStage.BeforeCommit,
            -1,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ReachAsync(
            PostgresHolyStoneCommandStage.AfterCommit,
            -1,
            cancellationToken);
    }

    private async ValueTask ReachAsync(
        PostgresHolyStoneCommandStage stage,
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

    private static bool HasCanonicalExpectedStates(
        HolyStoneCommand command) =>
        IsCanonicalCompactState(
            command.ExpectedTargetCompactItemState) &&
        IsCanonicalCompactState(
            command.ExpectedStoneCompactItemState);

    private static bool IsCanonicalCompactState(string value)
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

    private static byte[] DecodeDigest(string value)
    {
        var bytes = Convert.FromHexString(value);
        if (bytes.Length != CommandEnvelopeContract.DigestBytes)
        {
            throw new InvalidDataException(
                "The Holy Stone command digest has an invalid size.");
        }
        return bytes;
    }

    private static string OutcomeCode(
        HolyStoneExecutionDisposition disposition) =>
        disposition switch
        {
            HolyStoneExecutionDisposition.Committed => "committed",
            HolyStoneExecutionDisposition.Duplicate => "duplicate",
            HolyStoneExecutionDisposition.TerminalRejected =>
                "terminal_rejected",
            HolyStoneExecutionDisposition.ReplayNotFound =>
                "replay_not_found",
            HolyStoneExecutionDisposition.RequestHashConflict =>
                "request_hash_conflict",
            HolyStoneExecutionDisposition.InvalidIntent =>
                "invalid_intent",
            HolyStoneExecutionDisposition.PreconditionFailed =>
                "precondition_failed",
            _ => throw new ArgumentOutOfRangeException(nameof(disposition))
        };

    private NpgsqlCommand CreateCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _commandTimeoutSeconds
        };

    private sealed record HolyStoneCommandContext(
        CommandSubject Subject,
        PlayerOwnershipFence Ownership,
        string OperationId,
        string RequestHash,
        HolyStoneCommand Command);

    private sealed record LockedCharacter(
        int Gold,
        long WalletRevision,
        long InventoryRevision);

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
        short Slot,
        CompactItemEntry Item,
        string BeforeState);

    private sealed record LockedCommandItems(
        LockedItem? Target,
        LockedItem? Stone,
        IReadOnlyDictionary<short, LockedItem> KitBag);

    private sealed record HolyStonePlan(
        HolyStoneCommandResultStatus Status,
        int SocketIndex,
        CompactItemEntry TargetAfter,
        CompactItemEntry StoneAfter,
        int OutputKitBagSlot,
        CompactItemEntry OutputItem,
        short? RemovedEffectId,
        short? RemovedLevel,
        int GoldSpent)
    {
        public bool IsSuccess =>
            HolyStoneNativeResults.IsSuccess(Status);
    }

    private sealed record InventoryMutation(
        long ItemInstanceId,
        string MutationKind,
        string? BeforeState,
        string? AfterState);
}
