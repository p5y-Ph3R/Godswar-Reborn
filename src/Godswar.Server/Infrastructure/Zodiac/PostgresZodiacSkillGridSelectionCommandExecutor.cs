using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Zodiac;

internal sealed partial class
    PostgresZodiacSkillGridSelectionCommandExecutor :
    IZodiacSkillGridSelectionCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresPlayerOwnershipGuard _ownershipGuard;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly IPostgresZodiacSkillGridSelectionCommandProbe? _probe;
    private readonly string? _gameplayContentRevision;

    public PostgresZodiacSkillGridSelectionCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        IPostgresZodiacSkillGridSelectionCommandProbe? probe = null)
        : this(dataSource, options, null, probe)
    {
    }

    public PostgresZodiacSkillGridSelectionCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        string gameplayContentRevision)
        : this(dataSource, options, gameplayContentRevision, null)
    {
    }

    internal PostgresZodiacSkillGridSelectionCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        string? gameplayContentRevision,
        IPostgresZodiacSkillGridSelectionCommandProbe? probe)
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
        _gameplayContentRevision =
            PostgresGameplayContentBinding.ValidateOptional(
                gameplayContentRevision);
    }

    public async Task<ZodiacSkillGridSelectionExecutionResult>
        ExecuteAsync(
            CommandEnvelope<ZodiacSkillGridSelectionCommand> envelope,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation =
                ZodiacSkillGridSelectionCommandEnvelope.Validate(envelope);
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return ZodiacSkillGridSelectionExecutionResult
                    .RequestHashConflict();
            }

            if (validation != CommandEnvelopeValidation.Valid)
            {
                outcome = "invalid_intent";
                return ZodiacSkillGridSelectionExecutionResult
                    .InvalidIntent();
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
            outcome = result.Disposition.ToString().ToLowerInvariant();
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
                ZodiacSkillGridSelectionPersistenceCodec.CommandFamily,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<ZodiacSkillGridSelectionExecutionResult>
        ExecuteTransactionAsync(
            CommandEnvelope<ZodiacSkillGridSelectionCommand> envelope,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(envelope.OperationId);
        var requestHash = DecodeDigest(envelope.RequestHash);
        var principalKey = envelope.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey =
            ZodiacSkillGridSelectionPersistenceCodec
                .CommandAggregateKey(envelope.Subject.CharacterId);
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
            return ZodiacSkillGridSelectionExecutionResult
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
            await transaction.RollbackAsync(cancellationToken);
            return ZodiacSkillGridSelectionExecutionResult
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
            return await ReplayAsync(
                connection,
                transaction,
                envelope,
                existing,
                requestHash,
                cancellationToken);
        }

        var row = await ReadRowAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            envelope.Command.GridIndex,
            cancellationToken);
        var currentRevision = await ReadCurrentRevisionAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            envelope.Command.GridIndex,
            cancellationToken);
        var learned = await IsSkillLearnedAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            character.Value.Profession,
            envelope.Command.GridIndex,
            envelope.Command.SelectedSkillKind,
            cancellationToken);
        var selection = DeriveSelection(
            character.Value.Profession,
            row,
            envelope.Command.GridIndex,
            envelope.Command.SelectedSkillKind,
            learned);
        return await PersistAsync(
            connection,
            transaction,
            envelope,
            selection,
            currentRevision,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
    }

    private async Task<ZodiacSkillGridSelectionExecutionResult>
        ReplayAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CommandEnvelope<ZodiacSkillGridSelectionCommand> envelope,
            StoredInbox existing,
            byte[] requestHash,
            CancellationToken cancellationToken)
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
            return ZodiacSkillGridSelectionExecutionResult
                .RequestHashConflict();
        }

        var receipt = ValidateStoredResult(
            existing,
            envelope.Subject.CharacterId,
            envelope.Command.GridIndex);
        var row = await ReadRowAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            envelope.Command.GridIndex,
            cancellationToken);
        var current = row[envelope.Command.GridIndex -
            ZodiacSkillGridSelectionCatalog.RowStart(
                envelope.Command.GridIndex)];
        var revision = await ReadCurrentRevisionAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            envelope.Command.GridIndex,
            cancellationToken);
        await RecordDuplicateAsync(
            connection,
            transaction,
            existing.InboxId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ZodiacSkillGridSelectionExecutionResult.Duplicate(
            receipt,
            current.Level,
            current.SelectedSkillKind,
            revision);
    }

    private async Task<ZodiacSkillGridSelectionExecutionResult>
        PersistAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CommandEnvelope<ZodiacSkillGridSelectionCommand> envelope,
            ZodiacSkillGridSelectionResult selection,
            long currentRevision,
            string principalKey,
            string aggregateKey,
            byte[] operationId,
            byte[] requestHash,
            CancellationToken cancellationToken)
    {
        var eventId = selection.Committed ? Guid.NewGuid() : (Guid?)null;
        var nextRevision = selection.Committed
            ? checked(currentRevision + 1)
            : (long?)null;
        var receiptStatus = ReceiptStatus(selection.Status);
        var resultCode =
            ZodiacSkillGridSelectionPersistenceCodec.ResultCode(
                receiptStatus);
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            envelope,
            selection,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            resultCode,
            cancellationToken);
        var receipt = new ZodiacSkillGridSelectionExecutionReceipt(
            envelope.Subject.CharacterId,
            receiptStatus,
            selection.GridIndex,
            selection.CurrentLevel,
            selection.PreviousSkillKind,
            selection.SelectedSkillKind,
            nextRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            eventId);
        var payload =
            ZodiacSkillGridSelectionPersistenceCodec.Encode(receipt);
        await ReachAsync(
            PostgresZodiacSkillGridSelectionCommandStage.AuditInserted,
            cancellationToken);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            resultCode,
            ZodiacSkillGridSelectionPersistenceCodec.Hash(payload),
            auditId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresZodiacSkillGridSelectionCommandStage.InboxInserted,
            cancellationToken);

        if (!selection.Committed)
        {
            await CommitAsync(transaction, cancellationToken);
            return ZodiacSkillGridSelectionExecutionResult
                .TerminalRejected(receipt);
        }

        await UpdateGridAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            selection,
            cancellationToken);
        await ReachAsync(
            PostgresZodiacSkillGridSelectionCommandStage.GridUpdated,
            cancellationToken);
        await InsertOutboxAsync(
            connection,
            transaction,
            inboxId,
            ZodiacSkillGridSelectionPersistenceCodec.EventAggregateKey(
                envelope.Subject.CharacterId,
                selection.GridIndex),
            nextRevision!.Value,
            eventId!.Value,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresZodiacSkillGridSelectionCommandStage.OutboxInserted,
            cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return ZodiacSkillGridSelectionExecutionResult.Committed(receipt);
    }

    private async Task CommitAsync(
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ReachAsync(
            PostgresZodiacSkillGridSelectionCommandStage.BeforeCommit,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ReachAsync(
            PostgresZodiacSkillGridSelectionCommandStage.AfterCommit,
            cancellationToken);
    }

    private async ValueTask ReachAsync(
        PostgresZodiacSkillGridSelectionCommandStage stage,
        CancellationToken cancellationToken)
    {
        if (_probe is not null)
        {
            await _probe.ReachedAsync(stage, cancellationToken);
        }
    }

    private NpgsqlCommand CreateCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _commandTimeoutSeconds
        };

    private static byte[] DecodeDigest(string value)
    {
        var bytes = Convert.FromHexString(value);
        return bytes.Length == CommandEnvelopeContract.DigestBytes
            ? bytes
            : throw new InvalidDataException(
                "The Zodiac selection digest has an invalid size.");
    }

    private static ZodiacSkillGridSelectionReceiptStatus ReceiptStatus(
        ZodiacSkillGridSelectionStatus status) =>
        status switch
        {
            ZodiacSkillGridSelectionStatus.Succeeded =>
                ZodiacSkillGridSelectionReceiptStatus.Succeeded,
            ZodiacSkillGridSelectionStatus.InactiveGrid =>
                ZodiacSkillGridSelectionReceiptStatus.InactiveGrid,
            ZodiacSkillGridSelectionStatus.SkillKindNotAllowedForGrid =>
                ZodiacSkillGridSelectionReceiptStatus
                    .SkillKindNotAllowedForGrid,
            ZodiacSkillGridSelectionStatus.SkillKindNotAllowedForClass =>
                ZodiacSkillGridSelectionReceiptStatus
                    .SkillKindNotAllowedForClass,
            ZodiacSkillGridSelectionStatus.SkillNotLearned =>
                ZodiacSkillGridSelectionReceiptStatus.SkillNotLearned,
            ZodiacSkillGridSelectionStatus.DuplicateSkillInRow =>
                ZodiacSkillGridSelectionReceiptStatus
                    .DuplicateSkillInRow,
            ZodiacSkillGridSelectionStatus.AlreadySelected =>
                ZodiacSkillGridSelectionReceiptStatus.AlreadySelected,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };

    private readonly record struct LockedCharacter(byte Profession);
    private readonly record struct StoredGrid(
        byte Level,
        int SelectedSkillKind);
    private sealed record StoredInbox(
        long InboxId,
        byte[] RequestHash,
        short ResultContractVersion,
        string ResultCode,
        string ResultPayload,
        byte[] ResultHash,
        long AuditId);
}
