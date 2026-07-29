using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresGearEnhancementCommandExecutor :
    IGearEnhancementCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly IPostgresGearEnhancementCommandProbe? _probe;

    public PostgresGearEnhancementCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        IPostgresGearEnhancementCommandProbe? probe = null)
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

    public async Task<GearEnhancementExecutionResult> ExecuteAsync(
        CommandEnvelope<GearEnhancementCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        CommandFamily? family = envelope.Family;
        var outcome = "provider_unavailable";
        try
        {
            var validation =
                GearEnhancementCommandEnvelope.Validate(envelope);
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return GearEnhancementExecutionResult
                    .RequestHashConflict();
            }

            if (validation != CommandEnvelopeValidation.Valid ||
                !HasCanonicalExpectedItemStates(envelope.Command))
            {
                outcome = "invalid_intent";
                return GearEnhancementExecutionResult.InvalidIntent();
            }

            family = GearEnhancementCommandEnvelope.Family(
                envelope.Command.Operation);
            var context = new GearEnhancementCommandContext(
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
            if (family.HasValue &&
                IsGearEnhancementFamily(family.Value))
            {
                PostgresCommandMetrics.RecordInbox(
                    GearEnhancementPersistenceCodec.CommandFamilyCode(
                        family.Value),
                    outcome,
                    Stopwatch.GetElapsedTime(started));
            }
        }
    }

    public async Task<GearEnhancementExecutionResult> TryReplayAsync(
        CommandSubject subject,
        GearEnhancementCommandOperation operation,
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        CommandFamily? family = Enum.IsDefined(operation)
            ? GearEnhancementCommandEnvelope.Family(operation)
            : null;
        var outcome = "provider_unavailable";
        try
        {
            if (subject.AccountId <= 0 ||
                subject.CharacterId <= 0 ||
                clientOperationId == Guid.Empty ||
                !Enum.IsDefined(operation))
            {
                outcome = "invalid_intent";
                return GearEnhancementExecutionResult.InvalidIntent();
            }

            var operationId = DecodeDigest(
                GearEnhancementCommandEnvelope.CreateOperationId(
                    subject,
                    operation,
                    clientOperationId));
            var principalKey = subject.AccountId.ToString(
                CultureInfo.InvariantCulture);
            var aggregateKey =
                GearEnhancementPersistenceCodec.AggregateKey(
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
                return GearEnhancementExecutionResult
                    .PreconditionFailed();
            }

            var stored = await ReadInboxAsync(
                connection,
                transaction,
                family!.Value,
                principalKey,
                aggregateKey,
                operationId,
                cancellationToken);
            if (stored is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                outcome = "replay_not_found";
                return GearEnhancementExecutionResult.ReplayNotFound();
            }

            var receipt = ValidateStoredResult(stored, family.Value);
            await RecordDuplicateAsync(
                connection,
                transaction,
                stored.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            outcome = "duplicate";
            return GearEnhancementExecutionResult.Duplicate(receipt);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            if (family.HasValue &&
                IsGearEnhancementFamily(family.Value))
            {
                PostgresCommandMetrics.RecordInbox(
                    GearEnhancementPersistenceCodec.CommandFamilyCode(
                        family.Value),
                    outcome,
                    Stopwatch.GetElapsedTime(started));
            }
        }
    }

    private async Task<GearEnhancementExecutionResult>
        ExecuteTransactionAsync(
            GearEnhancementCommandContext context,
            CancellationToken cancellationToken)
    {
        var family = context.Family;
        var operationId = DecodeDigest(context.OperationId);
        var requestHash = DecodeDigest(context.RequestHash);
        var principalKey = context.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey =
            GearEnhancementPersistenceCodec.AggregateKey(
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
            return GearEnhancementExecutionResult.PreconditionFailed();
        }

        var existing = await ReadInboxAsync(
            connection,
            transaction,
            family,
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
                return GearEnhancementExecutionResult
                    .RequestHashConflict();
            }

            var receipt = ValidateStoredResult(existing, family);
            await RecordDuplicateAsync(
                connection,
                transaction,
                existing.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return GearEnhancementExecutionResult.Duplicate(receipt);
        }

        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                context.Subject.AccountId,
                context.Subject.CharacterId,
                _commandTimeoutSeconds,
                cancellationToken))
        {
            return GearEnhancementExecutionResult.PreconditionFailed();
        }

        var lockedBag = await LockKitBagAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            cancellationToken);
        var plan = GearEnhancementPlanner.Create(
            lockedBag.CompactProjection,
            CreatePlannerRequest(context.Command));
        var status = MapStatus(plan.Status);
        if (!plan.Committed)
        {
            var receipt = await PersistTerminalResultAsync(
                connection,
                transaction,
                context,
                character.Value.InventoryRevision,
                status,
                principalKey,
                aggregateKey,
                operationId,
                requestHash,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return GearEnhancementExecutionResult
                .TerminalRejected(receipt);
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
        PostgresGearEnhancementCommandStage stage,
        CancellationToken cancellationToken)
    {
        if (_probe is not null)
        {
            await _probe.ReachedAsync(stage, cancellationToken);
        }
    }

    private static GearEnhancementRequest CreatePlannerRequest(
        GearEnhancementCommand command) =>
        new(
            MapOperation(command.Operation),
            CreateSelection(command.Gear),
            CreateSelection(command.AttributeStone),
            CreateSelection(command.Catalyst));

    private static GearEnhancementSlotSelection CreateSelection(
        GearEnhancementCommandSelection selection) =>
        new(
            selection.KitBagSlot,
            CompactItemEntry.Parse(
                selection.ExpectedCompactItemState));

    private static GearEnhancementOperation MapOperation(
        GearEnhancementCommandOperation operation) =>
        operation switch
        {
            GearEnhancementCommandOperation.Enhance =>
                GearEnhancementOperation.Enhance,
            GearEnhancementCommandOperation.Add =>
                GearEnhancementOperation.Add,
            GearEnhancementCommandOperation.Delete =>
                GearEnhancementOperation.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static bool HasCanonicalExpectedItemStates(
        GearEnhancementCommand command) =>
        GearEnhancementCommandEnvelope.OrderedSelections(command).All(
            static selection =>
            {
                var expected = CompactItemEntry.Parse(
                    selection.ExpectedCompactItemState);
                return !expected.IsEmpty &&
                    string.Equals(
                        expected.ToCompactString(),
                        selection.ExpectedCompactItemState,
                        StringComparison.Ordinal);
            });

    private static GearEnhancementCommandResultStatus MapStatus(
        GearEnhancementStatus status) =>
        status switch
        {
            GearEnhancementStatus.Succeeded =>
                GearEnhancementCommandResultStatus.Succeeded,
            GearEnhancementStatus.SelectionMissing =>
                GearEnhancementCommandResultStatus.SelectionMissing,
            GearEnhancementStatus.InvalidKitBagSlot or
                GearEnhancementStatus.DuplicateKitBagSlot =>
                GearEnhancementCommandResultStatus.InvalidSelection,
            GearEnhancementStatus.StaleSelection =>
                GearEnhancementCommandResultStatus.StaleSelection,
            GearEnhancementStatus.InvalidEquipment =>
                GearEnhancementCommandResultStatus.InvalidEquipment,
            GearEnhancementStatus.UnsupportedEquipment =>
                GearEnhancementCommandResultStatus.UnsupportedEquipment,
            GearEnhancementStatus.InvalidAttributeState =>
                GearEnhancementCommandResultStatus.InvalidAttributeState,
            GearEnhancementStatus.InvalidAttributeStone =>
                GearEnhancementCommandResultStatus.InvalidAttributeStone,
            GearEnhancementStatus.InvalidCatalyst =>
                GearEnhancementCommandResultStatus.InvalidCatalyst,
            GearEnhancementStatus.InsufficientMaterial =>
                GearEnhancementCommandResultStatus.InsufficientMaterial,
            GearEnhancementStatus.AttributeNotAllowed =>
                GearEnhancementCommandResultStatus.AttributeNotAllowed,
            GearEnhancementStatus.AttributeAlreadyPresent =>
                GearEnhancementCommandResultStatus
                    .AttributeAlreadyPresent,
            GearEnhancementStatus.AttributeSlotsFull =>
                GearEnhancementCommandResultStatus.AttributeSlotsFull,
            GearEnhancementStatus.AttributeMissing =>
                GearEnhancementCommandResultStatus.AttributeMissing,
            GearEnhancementStatus.AttributeAmbiguous =>
                GearEnhancementCommandResultStatus.AttributeAmbiguous,
            GearEnhancementStatus.AttributeNotEnhanceable =>
                GearEnhancementCommandResultStatus
                    .AttributeNotEnhanceable,
            GearEnhancementStatus.AttributeLevelMismatch =>
                GearEnhancementCommandResultStatus
                    .AttributeLevelMismatch,
            GearEnhancementStatus.QuartzLevelMismatch =>
                GearEnhancementCommandResultStatus.QuartzLevelMismatch,
            GearEnhancementStatus.AttributeMaximumLevel =>
                GearEnhancementCommandResultStatus.AttributeMaximumLevel,
            GearEnhancementStatus.RequestMissing or
                GearEnhancementStatus.UnsupportedOperation =>
                throw new InvalidDataException(
                    $"Unexpected Gear Enhancement planner status {status}."),
            _ => throw new InvalidDataException(
                $"Unsupported Gear Enhancement planner status {status}.")
        };

    private static string OutcomeCode(
        GearEnhancementExecutionDisposition disposition) =>
        disposition switch
        {
            GearEnhancementExecutionDisposition.Committed =>
                "committed",
            GearEnhancementExecutionDisposition.Duplicate =>
                "duplicate",
            GearEnhancementExecutionDisposition.TerminalRejected =>
                "terminal_rejected",
            GearEnhancementExecutionDisposition.ReplayNotFound =>
                "replay_not_found",
            GearEnhancementExecutionDisposition.RequestHashConflict =>
                "request_hash_conflict",
            GearEnhancementExecutionDisposition.InvalidIntent =>
                "invalid_intent",
            GearEnhancementExecutionDisposition.PreconditionFailed =>
                "precondition_failed",
            _ => throw new InvalidOperationException(
                "Unknown Gear Enhancement command outcome.")
        };

    private static bool IsGearEnhancementFamily(CommandFamily family) =>
        family is CommandFamily.GearMentorEnhanceAttribute or
            CommandFamily.GearMentorAddAttribute or
            CommandFamily.GearMentorDeleteAttribute;

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

    private readonly record struct GearEnhancementCommandContext(
        CommandSubject Subject,
        string OperationId,
        string RequestHash,
        GearEnhancementCommand Command)
    {
        public CommandFamily Family =>
            GearEnhancementCommandEnvelope.Family(Command.Operation);
    }

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

    private sealed record LockedInventoryItem(
        long ItemInstanceId,
        short Slot,
        CompactItemEntry Item,
        string BeforeState);

    private sealed record LockedKitBag(
        string CompactProjection,
        IReadOnlyDictionary<short, LockedInventoryItem> Items);

    private sealed record InventoryMutation(
        GearEnhancementCommandItemRole Role,
        long ItemInstanceId,
        string MutationKind,
        string? BeforeState,
        string? AfterState);
}
