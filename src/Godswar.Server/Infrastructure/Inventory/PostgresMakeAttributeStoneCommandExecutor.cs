using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresMakeAttributeStoneCommandExecutor :
    IMakeAttributeStoneCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly IPostgresMakeAttributeStoneCommandProbe? _probe;

    public PostgresMakeAttributeStoneCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        IPostgresMakeAttributeStoneCommandProbe? probe = null)
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

    public async Task<MakeAttributeStoneExecutionResult> ExecuteAsync(
        CommandEnvelope<GearMentorMakeAttributeStoneCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation =
                GearMentorMakeAttributeStoneCommandEnvelope.Validate(
                    envelope);
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return MakeAttributeStoneExecutionResult
                    .RequestHashConflict();
            }

            if (validation != CommandEnvelopeValidation.Valid)
            {
                outcome = "invalid_intent";
                return MakeAttributeStoneExecutionResult.InvalidIntent();
            }

            if (!HasCanonicalExpectedItemState(envelope.Command))
            {
                outcome = "invalid_intent";
                return MakeAttributeStoneExecutionResult.InvalidIntent();
            }

            var result = await ExecuteTransactionAsync(
                envelope,
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
                MakeAttributeStonePersistenceCodec.CommandFamily,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    public async Task<MakeAttributeStoneExecutionResult> TryReplayAsync(
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
                return MakeAttributeStoneExecutionResult.InvalidIntent();
            }

            var operationId = DecodeDigest(
                GearMentorMakeAttributeStoneCommandEnvelope
                    .CreateOperationId(subject, clientOperationId));
            var principalKey = subject.AccountId.ToString(
                CultureInfo.InvariantCulture);
            var aggregateKey =
                MakeAttributeStonePersistenceCodec.AggregateKey(
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
                return MakeAttributeStoneExecutionResult
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
                return MakeAttributeStoneExecutionResult
                    .ReplayNotFound();
            }

            var receipt = ValidateStoredResult(stored);
            await RecordDuplicateAsync(
                connection,
                transaction,
                stored.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            outcome = "duplicate";
            return MakeAttributeStoneExecutionResult.Duplicate(receipt);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            PostgresCommandMetrics.RecordInbox(
                MakeAttributeStonePersistenceCodec.CommandFamily,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<MakeAttributeStoneExecutionResult>
        ExecuteTransactionAsync(
            CommandEnvelope<GearMentorMakeAttributeStoneCommand> envelope,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(envelope.OperationId);
        var requestHash = DecodeDigest(envelope.RequestHash);
        var principalKey = envelope.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey =
            MakeAttributeStonePersistenceCodec.AggregateKey(
                envelope.Subject.CharacterId);

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var character = await LockCharacterAsync(
            connection,
            transaction,
            envelope.Subject,
            cancellationToken);
        if (character is null)
        {
            return MakeAttributeStoneExecutionResult.PreconditionFailed();
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
                return MakeAttributeStoneExecutionResult
                    .RequestHashConflict();
            }

            var receipt = ValidateStoredResult(existing);
            await RecordDuplicateAsync(
                connection,
                transaction,
                existing.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return MakeAttributeStoneExecutionResult.Duplicate(receipt);
        }

        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                envelope.Subject.AccountId,
                envelope.Subject.CharacterId,
                _commandTimeoutSeconds,
                cancellationToken))
        {
            return MakeAttributeStoneExecutionResult.PreconditionFailed();
        }

        var lockedBag = await LockKitBagAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
        var request = CreatePlannerRequest(envelope.Command);
        var plan = GearMentorPlanner.Create(
            lockedBag.CompactProjection,
            character.Value.PlayerLevel,
            request);
        var durableStatus = MapStatus(plan.Status);
        if (!plan.Committed)
        {
            var rejected = await PersistTerminalResultAsync(
                connection,
                transaction,
                envelope,
                character.Value.InventoryRevision,
                durableStatus,
                principalKey,
                aggregateKey,
                operationId,
                requestHash,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return MakeAttributeStoneExecutionResult
                .TerminalRejected(rejected);
        }

        return await PersistCommittedResultAsync(
            connection,
            transaction,
            envelope,
            character.Value.InventoryRevision,
            plan,
            lockedBag,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
    }

    private async ValueTask ReachAsync(
        PostgresMakeAttributeStoneCommandStage stage,
        CancellationToken cancellationToken)
    {
        if (_probe is not null)
        {
            await _probe.ReachedAsync(stage, cancellationToken);
        }
    }

    private static GearMentorRequest CreatePlannerRequest(
        GearMentorMakeAttributeStoneCommand command)
    {
        var expected = CompactItemEntry.Parse(
            command.ExpectedCompactItemState);
        return new GearMentorRequest(
            GearMentorOperation.MakeAttributeStone,
            [
                new GearMentorSlotSelection(
                    command.SelectedKitBagSlot,
                    expected)
            ]);
    }

    private static bool HasCanonicalExpectedItemState(
        GearMentorMakeAttributeStoneCommand command)
    {
        var expected = CompactItemEntry.Parse(
            command.ExpectedCompactItemState);
        return !expected.IsEmpty &&
            string.Equals(
                expected.ToCompactString(),
                command.ExpectedCompactItemState,
                StringComparison.Ordinal);
    }

    private static MakeAttributeStoneResultStatus MapStatus(
        GearMentorStatus status) =>
        status switch
        {
            GearMentorStatus.Succeeded =>
                MakeAttributeStoneResultStatus.Succeeded,
            GearMentorStatus.InvalidDust =>
                MakeAttributeStoneResultStatus.InvalidDust,
            GearMentorStatus.InsufficientDust =>
                MakeAttributeStoneResultStatus.InsufficientDust,
            GearMentorStatus.InsufficientCapacity =>
                MakeAttributeStoneResultStatus.InsufficientCapacity,
            GearMentorStatus.SelectionMissing or
                GearMentorStatus.StaleSelection =>
                MakeAttributeStoneResultStatus.StaleSelection,
            GearMentorStatus.InvalidKitBagSlot =>
                MakeAttributeStoneResultStatus.InvalidKitBagSlot,
            _ => throw new InvalidDataException(
                $"Unsupported Make Attribute Stone planner status " +
                $"{status}.")
        };

    private static string OutcomeCode(
        MakeAttributeStoneExecutionDisposition disposition) =>
        disposition switch
        {
            MakeAttributeStoneExecutionDisposition.Committed =>
                "committed",
            MakeAttributeStoneExecutionDisposition.Duplicate =>
                "duplicate",
            MakeAttributeStoneExecutionDisposition.TerminalRejected =>
                "terminal_rejected",
            MakeAttributeStoneExecutionDisposition.ReplayNotFound =>
                "replay_not_found",
            MakeAttributeStoneExecutionDisposition
                .RequestHashConflict =>
                "request_hash_conflict",
            MakeAttributeStoneExecutionDisposition.InvalidIntent =>
                "invalid_intent",
            MakeAttributeStoneExecutionDisposition.PreconditionFailed =>
                "precondition_failed",
            _ => throw new InvalidOperationException(
                "Unknown Make Attribute Stone command outcome.")
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

    private readonly record struct LockedCharacter(
        int PlayerLevel,
        long InventoryRevision);

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
        string CompactProjection,
        IReadOnlyDictionary<short, LockedInventoryItem> Items);

    private sealed record InventoryMutation(
        long ItemInstanceId,
        string MutationKind,
        string? BeforeState,
        string? AfterState);
}
