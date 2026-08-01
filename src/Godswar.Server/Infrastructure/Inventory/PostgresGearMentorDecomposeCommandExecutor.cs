using System.Collections.Immutable;
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

internal sealed partial class PostgresGearMentorDecomposeCommandExecutor :
    IGearMentorDecomposeGearCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly IPostgresGearMentorDecomposeCommandProbe? _probe;
    private readonly IGearMentorDecomposeRandomSource _randomSource;
    private readonly GameplayItemContent _itemContent;

    public PostgresGearMentorDecomposeCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        GameplayItemContent itemContent,
        IPostgresGearMentorDecomposeCommandProbe? probe = null,
        IGearMentorDecomposeRandomSource? randomSource = null)
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
        _randomSource = randomSource ??
            new CryptographicGearMentorDecomposeRandomSource();
    }

    public async Task<GearMentorDecomposeGearExecutionResult> ExecuteAsync(
        CommandEnvelope<GearMentorDecomposeGearCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation =
                GearMentorDecomposeGearCommandEnvelope.Validate(envelope);
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return GearMentorDecomposeGearExecutionResult
                    .RequestHashConflict();
            }

            if (validation != CommandEnvelopeValidation.Valid ||
                !HasCanonicalExpectedItemStates(envelope.Command))
            {
                outcome = "invalid_intent";
                return GearMentorDecomposeGearExecutionResult
                    .InvalidIntent();
            }

            var context = new DecomposeCommandContext(
                envelope.Subject,
                envelope.Ownership,
                envelope.OperationId,
                envelope.RequestHash,
                envelope.Command.ClientOperationId,
                envelope.Command.NpcId,
                envelope.Command.Selections);
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
                GearMentorDecomposePersistenceCodec.CommandFamilyCode,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    public async Task<GearMentorDecomposeGearExecutionResult>
        TryReplayAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
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
                return GearMentorDecomposeGearExecutionResult
                    .InvalidIntent();
            }

            var operationId = DecodeDigest(
                GearMentorDecomposeGearCommandEnvelope.CreateOperationId(
                    subject,
                    clientOperationId));
            var principalKey = subject.AccountId.ToString(
                CultureInfo.InvariantCulture);
            var aggregateKey =
                GearMentorDecomposePersistenceCodec.AggregateKey(
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
                return GearMentorDecomposeGearExecutionResult
                    .PreconditionFailed();
            }
            ownershipResult.RequireCurrent();

            if (await LockCharacterAsync(
                    connection,
                    transaction,
                    subject,
                    cancellationToken) is null)
            {
                outcome = "precondition_failed";
                return GearMentorDecomposeGearExecutionResult
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
                return GearMentorDecomposeGearExecutionResult
                    .ReplayNotFound();
            }

            var receipt = ValidateStoredResult(stored);
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
            return GearMentorDecomposeGearExecutionResult
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
                GearMentorDecomposePersistenceCodec.CommandFamilyCode,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<GearMentorDecomposeGearExecutionResult>
        ExecuteTransactionAsync(
            DecomposeCommandContext context,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(context.OperationId);
        var requestHash = DecodeDigest(context.RequestHash);
        var principalKey = context.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey =
            GearMentorDecomposePersistenceCodec.AggregateKey(
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
            return GearMentorDecomposeGearExecutionResult
                .PreconditionFailed();
        }
        ownership.RequireCurrent();

        var character = await LockCharacterAsync(
            connection,
            transaction,
            context.Subject,
            cancellationToken);
        if (character is null)
        {
            return GearMentorDecomposeGearExecutionResult
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
                return GearMentorDecomposeGearExecutionResult
                    .RequestHashConflict();
            }

            var receipt = ValidateStoredResult(existing);
            await RecordDuplicateAsync(
                connection,
                transaction,
                existing.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return GearMentorDecomposeGearExecutionResult
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
            return GearMentorDecomposeGearExecutionResult
                .PreconditionFailed();
        }

        var lockedBag = await LockKitBagAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            cancellationToken);
        var plan = GearMentorPlanner.Create(
            _itemContent.Templates,
            lockedBag.CompactProjection,
            character.Value.PlayerLevel,
            CreatePlannerRequest(context),
            _randomSource.NextIndex);
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
            return GearMentorDecomposeGearExecutionResult
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
        PostgresGearMentorDecomposeCommandStage stage,
        CancellationToken cancellationToken)
    {
        if (_probe is not null)
        {
            await _probe.ReachedAsync(stage, cancellationToken);
        }
    }

    private static GearMentorRequest CreatePlannerRequest(
        DecomposeCommandContext context) =>
        new(
            GearMentorOperation.Decompose,
            context.Selections
                .Select(static selection =>
                    new GearMentorSlotSelection(
                        selection.SelectedKitBagSlot,
                        CompactItemEntry.Parse(
                            selection.ExpectedCompactItemState)))
                .ToArray());

    private static bool HasCanonicalExpectedItemStates(
        GearMentorDecomposeGearCommand command) =>
        command.Selections.All(
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

    private static GearMentorDecomposeGearResultStatus MapStatus(
        GearMentorStatus status) =>
        status switch
        {
            GearMentorStatus.Succeeded =>
                GearMentorDecomposeGearResultStatus.Succeeded,
            GearMentorStatus.SelectionMissing =>
                GearMentorDecomposeGearResultStatus.SelectionMissing,
            GearMentorStatus.PlayerLevelTooLow =>
                GearMentorDecomposeGearResultStatus.PlayerLevelTooLow,
            GearMentorStatus.InvalidEquipment =>
                GearMentorDecomposeGearResultStatus.InvalidEquipment,
            GearMentorStatus.EquipmentLevelTooLow =>
                GearMentorDecomposeGearResultStatus
                    .EquipmentLevelTooLow,
            GearMentorStatus.InsufficientEquipmentQuality =>
                GearMentorDecomposeGearResultStatus
                    .InsufficientEquipmentQuality,
            GearMentorStatus.ClassSuit =>
                GearMentorDecomposeGearResultStatus.ClassSuit,
            GearMentorStatus.InsufficientCapacity =>
                GearMentorDecomposeGearResultStatus
                    .InsufficientCapacity,
            GearMentorStatus.StaleSelection =>
                GearMentorDecomposeGearResultStatus.StaleSelection,
            GearMentorStatus.InvalidKitBagSlot or
                GearMentorStatus.DuplicateKitBagSlot =>
                GearMentorDecomposeGearResultStatus.InvalidSelection,
            _ => throw new InvalidDataException(
                $"Unsupported Decompose planner status {status}.")
        };

    private static string OutcomeCode(
        GearMentorDecomposeGearExecutionDisposition disposition) =>
        disposition switch
        {
            GearMentorDecomposeGearExecutionDisposition.Committed =>
                "committed",
            GearMentorDecomposeGearExecutionDisposition.Duplicate =>
                "duplicate",
            GearMentorDecomposeGearExecutionDisposition
                .TerminalRejected =>
                "terminal_rejected",
            GearMentorDecomposeGearExecutionDisposition.ReplayNotFound =>
                "replay_not_found",
            GearMentorDecomposeGearExecutionDisposition
                .RequestHashConflict =>
                "request_hash_conflict",
            GearMentorDecomposeGearExecutionDisposition.InvalidIntent =>
                "invalid_intent",
            GearMentorDecomposeGearExecutionDisposition
                .PreconditionFailed =>
                "precondition_failed",
            _ => throw new InvalidOperationException(
                "Unknown Decompose command outcome.")
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

    private readonly record struct DecomposeCommandContext(
        CommandSubject Subject,
        PlayerOwnershipFence Ownership,
        string OperationId,
        string RequestHash,
        Guid ClientOperationId,
        int NpcId,
        ImmutableArray<GearMentorDecomposeSelection> Selections);

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
