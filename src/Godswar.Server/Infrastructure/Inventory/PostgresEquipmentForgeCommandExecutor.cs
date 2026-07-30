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

internal sealed partial class PostgresEquipmentForgeCommandExecutor :
    IEquipmentForgeCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly IPostgresEquipmentForgeCommandProbe? _probe;
    private readonly Func<int> _rollSource;

    public PostgresEquipmentForgeCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        IPostgresEquipmentForgeCommandProbe? probe = null,
        Func<int>? rollSource = null)
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
        _rollSource = rollSource ??
            (() => RandomNumberGenerator.GetInt32(100));
    }

    public async Task<EquipmentForgeExecutionResult> ExecuteAsync(
        CommandEnvelope<EquipmentForgeCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation =
                EquipmentForgeCommandEnvelope.Validate(envelope);
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return EquipmentForgeExecutionResult
                    .RequestHashConflict();
            }
            if (validation != CommandEnvelopeValidation.Valid ||
                !HasCanonicalExpectedItemStates(envelope.Command))
            {
                outcome = "invalid_intent";
                return EquipmentForgeExecutionResult.InvalidIntent();
            }

            var context = new EquipmentForgeCommandContext(
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
                EquipmentForgePersistenceCodec.CommandFamilyCode,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    public async Task<EquipmentForgeExecutionResult> TryReplayAsync(
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
                return EquipmentForgeExecutionResult.InvalidIntent();
            }

            var operationId = DecodeDigest(
                EquipmentForgeCommandEnvelope.CreateOperationId(
                    subject,
                    clientOperationId));
            var principalKey = subject.AccountId.ToString(
                CultureInfo.InvariantCulture);
            var aggregateKey =
                EquipmentForgePersistenceCodec.AggregateKey(
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
                return EquipmentForgeExecutionResult
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
                return EquipmentForgeExecutionResult
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
                return EquipmentForgeExecutionResult.ReplayNotFound();
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
            return EquipmentForgeExecutionResult.Duplicate(receipt);
        }
        catch (OperationCanceledException)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            PostgresCommandMetrics.RecordInbox(
                EquipmentForgePersistenceCodec.CommandFamilyCode,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<EquipmentForgeExecutionResult>
        ExecuteTransactionAsync(
            EquipmentForgeCommandContext context,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(context.OperationId);
        var requestHash = DecodeDigest(context.RequestHash);
        var principalKey = context.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey =
            EquipmentForgePersistenceCodec.AggregateKey(
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
            return EquipmentForgeExecutionResult.PreconditionFailed();
        }
        ownership.RequireCurrent();

        var character = await LockCharacterAsync(
            connection,
            transaction,
            context.Subject,
            cancellationToken);
        if (character is null)
        {
            return EquipmentForgeExecutionResult.PreconditionFailed();
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
                return EquipmentForgeExecutionResult
                    .RequestHashConflict();
            }

            var receipt = ValidateStoredResult(existing);
            await RecordDuplicateAsync(
                connection,
                transaction,
                existing.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return EquipmentForgeExecutionResult.Duplicate(receipt);
        }

        if (!await PostgresCharacterEconomyBaseline.EnsureAsync(
                connection,
                transaction,
                context.Subject.AccountId,
                context.Subject.CharacterId,
                _commandTimeoutSeconds,
                cancellationToken))
        {
            return EquipmentForgeExecutionResult.PreconditionFailed();
        }

        var lockedBag = await LockKitBagAsync(
            connection,
            transaction,
            context.Subject.CharacterId,
            cancellationToken);
        var request = CreatePlannerRequest(context.Command);
        if (!ForgePersistencePlanner.TryCreate(
                lockedBag.CompactProjection,
                character.Value.Silver,
                request,
                roll: 0,
                out _,
                out var rejection,
                out _))
        {
            var receipt = await PersistTerminalResultAsync(
                connection,
                transaction,
                context,
                character.Value,
                MapStatus(rejection),
                principalKey,
                aggregateKey,
                operationId,
                requestHash,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return EquipmentForgeExecutionResult
                .TerminalRejected(receipt);
        }

        var roll = _rollSource();
        if (roll is < 0 or > 99)
        {
            throw new InvalidDataException(
                "The equipment-forge roll source returned an invalid value.");
        }
        if (!ForgePersistencePlanner.TryCreate(
                lockedBag.CompactProjection,
                character.Value.Silver,
                request,
                roll,
                out var plan,
                out _,
                out _))
        {
            throw new InvalidDataException(
                "A validated equipment-forge request became invalid.");
        }

        return await PersistCommittedResultAsync(
            connection,
            transaction,
            context,
            character.Value,
            plan!,
            roll,
            lockedBag,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
    }

    private async ValueTask ReachAsync(
        PostgresEquipmentForgeCommandStage stage,
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

    private static ForgeTransactionRequest CreatePlannerRequest(
        EquipmentForgeCommand command)
    {
        var odds = command.OddsMaterials
            .Select(CreateSelection)
            .ToArray();
        return new ForgeTransactionRequest(
            CreateSelection(command.Equipment),
            CreateSelection(command.PrimaryMaterial),
            odds.FirstOrDefault(),
            odds.Skip(1).ToArray());
    }

    private static ForgeSlotSelection CreateSelection(
        EquipmentForgeCommandSelection selection) =>
        new(
            selection.KitBagSlot,
            CompactItemEntry.Parse(
                selection.ExpectedCompactItemState),
            selection.Quantity);

    private static bool HasCanonicalExpectedItemStates(
        EquipmentForgeCommand command) =>
        EquipmentForgeCommandEnvelope.OrderedSelections(command).All(
            static selection =>
            {
                var item = CompactItemEntry.Parse(
                    selection.ExpectedCompactItemState);
                return !item.IsEmpty &&
                    string.Equals(
                        item.ToCompactString(),
                        selection.ExpectedCompactItemState,
                        StringComparison.Ordinal);
            });

    private static EquipmentForgeCommandResultStatus MapStatus(
        ForgeTransactionStatus status) =>
        status switch
        {
            ForgeTransactionStatus.InvalidSelection =>
                EquipmentForgeCommandResultStatus.InvalidSelection,
            ForgeTransactionStatus.StaleSelection =>
                EquipmentForgeCommandResultStatus.StaleSelection,
            ForgeTransactionStatus.InvalidForge =>
                EquipmentForgeCommandResultStatus.InvalidForge,
            ForgeTransactionStatus.InsufficientMaterials =>
                EquipmentForgeCommandResultStatus.InsufficientMaterials,
            ForgeTransactionStatus.InsufficientSilver =>
                EquipmentForgeCommandResultStatus.InsufficientSilver,
            _ => throw new InvalidDataException(
                $"Unexpected equipment-forge rejection {status}.")
        };

    private static string OutcomeCode(
        EquipmentForgeExecutionDisposition disposition) =>
        disposition switch
        {
            EquipmentForgeExecutionDisposition.Committed => "committed",
            EquipmentForgeExecutionDisposition.Duplicate => "duplicate",
            EquipmentForgeExecutionDisposition.TerminalRejected =>
                "terminal_rejected",
            EquipmentForgeExecutionDisposition.ReplayNotFound =>
                "replay_not_found",
            EquipmentForgeExecutionDisposition.RequestHashConflict =>
                "request_hash_conflict",
            EquipmentForgeExecutionDisposition.InvalidIntent =>
                "invalid_intent",
            EquipmentForgeExecutionDisposition.PreconditionFailed =>
                "precondition_failed",
            _ => throw new InvalidOperationException(
                "Unknown equipment-forge command outcome.")
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

    private readonly record struct EquipmentForgeCommandContext(
        CommandSubject Subject,
        PlayerOwnershipFence Ownership,
        string OperationId,
        string RequestHash,
        EquipmentForgeCommand Command);

    private readonly record struct LockedCharacter(
        int Silver,
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

    private sealed record LockedInventoryItem(
        long ItemInstanceId,
        short Slot,
        CompactItemEntry Item,
        string BeforeState);

    private sealed record LockedKitBag(
        string CompactProjection,
        IReadOnlyDictionary<short, LockedInventoryItem> Items);

    private sealed record InventoryMutation(
        EquipmentForgeCommandItemRole Role,
        long ItemInstanceId,
        string MutationKind,
        string? BeforeState,
        string? AfterState);
}
