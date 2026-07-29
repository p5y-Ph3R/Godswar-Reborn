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
    PostgresGearMentorMaterialConversionCommandExecutor :
    IGearMentorMaterialConversionCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly
        IPostgresGearMentorMaterialConversionCommandProbe? _probe;

    public PostgresGearMentorMaterialConversionCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        IPostgresGearMentorMaterialConversionCommandProbe? probe = null)
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

    public Task<GearMentorMaterialConversionExecutionResult>
        ExecuteAsync(
            CommandEnvelope<GearMentorTransformCrystalCommand> envelope,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var command = envelope.Command;
        return ExecuteAsync(
            CreateContext(
                envelope,
                CommandFamily.GearMentorTransformCrystal,
                GearMentorOperation.TransformCrystal,
                command.ClientOperationId,
                command.NpcId,
                command.SelectedKitBagSlot,
                command.ExpectedCompactItemState),
            GearMentorTransformCrystalCommandEnvelope.Validate(envelope),
            cancellationToken);
    }

    public Task<GearMentorMaterialConversionExecutionResult>
        ExecuteAsync(
            CommandEnvelope<GearMentorCombineGemPiecesCommand> envelope,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var command = envelope.Command;
        return ExecuteAsync(
            CreateContext(
                envelope,
                CommandFamily.GearMentorCombineGemPieces,
                GearMentorOperation.CombineGemPieces,
                command.ClientOperationId,
                command.NpcId,
                command.SelectedKitBagSlot,
                command.ExpectedCompactItemState),
            GearMentorCombineGemPiecesCommandEnvelope.Validate(envelope),
            cancellationToken);
    }

    public Task<GearMentorMaterialConversionExecutionResult>
        TryReplayTransformAsync(
            CommandSubject subject,
            Guid clientOperationId,
            CancellationToken cancellationToken = default) =>
        TryReplayAsync(
            subject,
            clientOperationId,
            CommandFamily.GearMentorTransformCrystal,
            GearMentorTransformCrystalCommandEnvelope.CreateOperationId,
            cancellationToken);

    public Task<GearMentorMaterialConversionExecutionResult>
        TryReplayCombineAsync(
            CommandSubject subject,
            Guid clientOperationId,
            CancellationToken cancellationToken = default) =>
        TryReplayAsync(
            subject,
            clientOperationId,
            CommandFamily.GearMentorCombineGemPieces,
            GearMentorCombineGemPiecesCommandEnvelope.CreateOperationId,
            cancellationToken);

    private async Task<GearMentorMaterialConversionExecutionResult>
        ExecuteAsync(
            MaterialCommandContext context,
            CommandEnvelopeValidation validation,
            CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return GearMentorMaterialConversionExecutionResult
                    .RequestHashConflict();
            }

            if (validation != CommandEnvelopeValidation.Valid ||
                !HasCanonicalExpectedItemState(context))
            {
                outcome = "invalid_intent";
                return GearMentorMaterialConversionExecutionResult
                    .InvalidIntent();
            }

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
                GearMentorMaterialConversionPersistenceCodec
                    .CommandFamilyCode(context.Family),
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<GearMentorMaterialConversionExecutionResult>
        TryReplayAsync(
            CommandSubject subject,
            Guid clientOperationId,
            CommandFamily family,
            Func<CommandSubject, Guid, string> operationIdFactory,
            CancellationToken cancellationToken)
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
                return GearMentorMaterialConversionExecutionResult
                    .InvalidIntent();
            }

            var operationId = DecodeDigest(
                operationIdFactory(subject, clientOperationId));
            var principalKey = subject.AccountId.ToString(
                CultureInfo.InvariantCulture);
            var aggregateKey =
                GearMentorMaterialConversionPersistenceCodec
                    .AggregateKey(subject.CharacterId);

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
                return GearMentorMaterialConversionExecutionResult
                    .PreconditionFailed();
            }

            var stored = await ReadInboxAsync(
                connection,
                transaction,
                family,
                principalKey,
                aggregateKey,
                operationId,
                cancellationToken);
            if (stored is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                outcome = "replay_not_found";
                return GearMentorMaterialConversionExecutionResult
                    .ReplayNotFound();
            }

            var receipt = ValidateStoredResult(stored, family);
            await RecordDuplicateAsync(
                connection,
                transaction,
                stored.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            outcome = "duplicate";
            return GearMentorMaterialConversionExecutionResult
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
                GearMentorMaterialConversionPersistenceCodec
                    .CommandFamilyCode(family),
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<GearMentorMaterialConversionExecutionResult>
        ExecuteTransactionAsync(
            MaterialCommandContext context,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(context.OperationId);
        var requestHash = DecodeDigest(context.RequestHash);
        var principalKey = context.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey =
            GearMentorMaterialConversionPersistenceCodec.AggregateKey(
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
            return GearMentorMaterialConversionExecutionResult
                .PreconditionFailed();
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
                return GearMentorMaterialConversionExecutionResult
                    .RequestHashConflict();
            }

            var receipt = ValidateStoredResult(
                existing,
                context.Family);
            await RecordDuplicateAsync(
                connection,
                transaction,
                existing.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return GearMentorMaterialConversionExecutionResult
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
            return GearMentorMaterialConversionExecutionResult
                .PreconditionFailed();
        }

        var lockedBag = await LockKitBagAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            cancellationToken);
        var request = CreatePlannerRequest(context);
        var plan = GearMentorPlanner.Create(
            lockedBag.CompactProjection,
            character.Value.PlayerLevel,
            request);
        var durableStatus = MapStatus(context.Family, plan.Status);
        if (!plan.Committed)
        {
            var rejected = await PersistTerminalResultAsync(
                connection,
                transaction,
                context,
                character.Value.InventoryRevision,
                durableStatus,
                principalKey,
                aggregateKey,
                operationId,
                requestHash,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return GearMentorMaterialConversionExecutionResult
                .TerminalRejected(rejected);
        }

        return await PersistCommittedResultAsync(
            connection,
            transaction,
            context,
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
        PostgresGearMentorMaterialConversionCommandStage stage,
        CancellationToken cancellationToken)
    {
        if (_probe is not null)
        {
            await _probe.ReachedAsync(stage, cancellationToken);
        }
    }

    private static GearMentorRequest CreatePlannerRequest(
        MaterialCommandContext context) =>
        new(
            context.Operation,
            [
                new GearMentorSlotSelection(
                    context.SelectedKitBagSlot,
                    CompactItemEntry.Parse(
                        context.ExpectedCompactItemState))
            ]);

    private static bool HasCanonicalExpectedItemState(
        MaterialCommandContext context)
    {
        var expected = CompactItemEntry.Parse(
            context.ExpectedCompactItemState);
        return !expected.IsEmpty &&
            string.Equals(
                expected.ToCompactString(),
                context.ExpectedCompactItemState,
                StringComparison.Ordinal);
    }

    private static GearMentorMaterialConversionResultStatus MapStatus(
        CommandFamily family,
        GearMentorStatus status) =>
        (family, status) switch
        {
            (_, GearMentorStatus.Succeeded) =>
                GearMentorMaterialConversionResultStatus.Succeeded,
            (
                CommandFamily.GearMentorTransformCrystal,
                GearMentorStatus.InvalidCrystal) =>
                GearMentorMaterialConversionResultStatus.InvalidCrystal,
            (
                CommandFamily.GearMentorCombineGemPieces,
                GearMentorStatus.InvalidGemPieces) =>
                GearMentorMaterialConversionResultStatus.InvalidGemPieces,
            (
                CommandFamily.GearMentorCombineGemPieces,
                GearMentorStatus.InsufficientGemPieces) =>
                GearMentorMaterialConversionResultStatus
                    .InsufficientGemPieces,
            (_, GearMentorStatus.InsufficientCapacity) =>
                GearMentorMaterialConversionResultStatus
                    .InsufficientCapacity,
            (_, GearMentorStatus.SelectionMissing) or
                (_, GearMentorStatus.StaleSelection) =>
                GearMentorMaterialConversionResultStatus.StaleSelection,
            (_, GearMentorStatus.InvalidKitBagSlot) =>
                GearMentorMaterialConversionResultStatus
                    .InvalidKitBagSlot,
            _ => throw new InvalidDataException(
                $"Unsupported {family} planner status {status}.")
        };

    private static MaterialCommandContext CreateContext<TCommand>(
        CommandEnvelope<TCommand> envelope,
        CommandFamily expectedFamily,
        GearMentorOperation operation,
        Guid clientOperationId,
        int npcId,
        int selectedKitBagSlot,
        string expectedCompactItemState) =>
        new(
            expectedFamily,
            operation,
            envelope.Subject,
            envelope.OperationId,
            envelope.RequestHash,
            clientOperationId,
            npcId,
            selectedKitBagSlot,
            expectedCompactItemState);

    private static string OutcomeCode(
        GearMentorMaterialConversionExecutionDisposition disposition) =>
        disposition switch
        {
            GearMentorMaterialConversionExecutionDisposition.Committed =>
                "committed",
            GearMentorMaterialConversionExecutionDisposition.Duplicate =>
                "duplicate",
            GearMentorMaterialConversionExecutionDisposition
                .TerminalRejected =>
                "terminal_rejected",
            GearMentorMaterialConversionExecutionDisposition
                .ReplayNotFound =>
                "replay_not_found",
            GearMentorMaterialConversionExecutionDisposition
                .RequestHashConflict =>
                "request_hash_conflict",
            GearMentorMaterialConversionExecutionDisposition
                .InvalidIntent =>
                "invalid_intent",
            GearMentorMaterialConversionExecutionDisposition
                .PreconditionFailed =>
                "precondition_failed",
            _ => throw new InvalidOperationException(
                "Unknown material-conversion command outcome.")
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

    private readonly record struct MaterialCommandContext(
        CommandFamily Family,
        GearMentorOperation Operation,
        CommandSubject Subject,
        string OperationId,
        string RequestHash,
        Guid ClientOperationId,
        int NpcId,
        int SelectedKitBagSlot,
        string ExpectedCompactItemState);

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
