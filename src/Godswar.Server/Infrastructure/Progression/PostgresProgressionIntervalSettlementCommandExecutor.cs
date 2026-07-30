using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Progression;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Progression;

internal sealed partial class
    PostgresProgressionIntervalSettlementCommandExecutor :
    IProgressionIntervalSettlementCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly ZodiacEnergyPolicy _zodiacEnergyPolicy;

    public PostgresProgressionIntervalSettlementCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        ZodiacEnergyPolicy zodiacEnergyPolicy)
    {
        _dataSource = dataSource ??
            throw new ArgumentNullException(nameof(dataSource));
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        zodiacEnergyPolicy.Validate();
        _commandTimeoutSeconds = Math.Max(
            1,
            (int)Math.Ceiling(options.CommandTimeout.TotalSeconds));
        _maximumOutboxAttempts =
            checked((short)options.MaximumDeliveryAttempts);
        _zodiacEnergyPolicy = zodiacEnergyPolicy;
    }

    public async Task<ProgressionIntervalSettlementExecutionResult>
        ExecuteAsync(
            CommandEnvelope<ProgressionIntervalSettlementCommand> envelope,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation =
                ProgressionIntervalSettlementCommandEnvelope.Validate(
                    envelope);
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return ProgressionIntervalSettlementExecutionResult
                    .RequestHashConflict();
            }

            if (validation != CommandEnvelopeValidation.Valid)
            {
                outcome = "invalid_intent";
                return ProgressionIntervalSettlementExecutionResult
                    .InvalidIntent();
            }

            var result = await ExecuteTransactionAsync(
                envelope,
                cancellationToken);
            outcome = result.Disposition switch
            {
                ProgressionIntervalSettlementDisposition.Committed =>
                    "committed",
                ProgressionIntervalSettlementDisposition.Duplicate =>
                    "duplicate",
                ProgressionIntervalSettlementDisposition
                    .RequestHashConflict => "request_hash_conflict",
                ProgressionIntervalSettlementDisposition.InvalidIntent =>
                    "invalid_intent",
                ProgressionIntervalSettlementDisposition
                    .CharacterNotFound => "character_not_found",
                ProgressionIntervalSettlementDisposition
                    .IntervalConflict => "interval_conflict",
                _ => throw new InvalidOperationException(
                    "Unknown progression interval outcome.")
            };
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
                ProgressionIntervalSettlementPersistenceCodec.CommandFamily,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<ProgressionIntervalSettlementExecutionResult>
        ExecuteTransactionAsync(
            CommandEnvelope<ProgressionIntervalSettlementCommand> envelope,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(envelope.OperationId);
        var requestHash = DecodeDigest(envelope.RequestHash);
        var principalKey = envelope.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey =
            ProgressionIntervalSettlementPersistenceCodec.AggregateKey(
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
            await transaction.RollbackAsync(cancellationToken);
            return ProgressionIntervalSettlementExecutionResult
                .CharacterNotFound();
        }

        var authority = await ReadAuthorityAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            cancellationToken);
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
                character.Value,
                authority,
                existing,
                requestHash,
                cancellationToken);
        }

        var conflict = ProgressionIntervalSettlementPolicy.ValidateNext(
            envelope.Command,
            authority,
            character.Value.ZodiacLastOnlineAt);
        if (conflict != ProgressionIntervalConflict.None)
        {
            var conflictProjection = authority is null
                ? null
                : CreateProjection(
                    authority.Value,
                    character.Value);
            await transaction.RollbackAsync(cancellationToken);
            return ProgressionIntervalSettlementExecutionResult
                .IntervalRejected(conflict, conflictProjection);
        }

        var domainCharacter = character.Value.ToDomainCharacter();
        var accrual = ZodiacEnergyAccrual.Apply(
            domainCharacter,
            envelope.Command.OnlineFromUtc,
            envelope.Command.OnlineUntilUtc,
            _zodiacEnergyPolicy);
        var revision = checked(
            (authority?.AggregateRevision ?? 0) + 1);
        var eventId = Guid.NewGuid();
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            envelope.Command,
            cancellationToken);

        await ApplyZodiacMutationAsync(
            connection,
            transaction,
            envelope.Subject,
            accrual,
            cancellationToken);
        var updatedBoostCount = await ConsumeBoostOnlineTimeAsync(
            connection,
            transaction,
            envelope.Subject,
            envelope.Command,
            cancellationToken);
        await UpsertAuthorityAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            envelope.Command,
            revision,
            cancellationToken);

        var projection = new ProgressionIntervalProjection(
            envelope.Command.OnlineSessionId,
            envelope.Command.IntervalSequence,
            accrual.LastOnlineAt,
            revision,
            accrual.CurrentEnergy,
            accrual.CurrentEnergyRemainderX100,
            accrual.OnlineDay,
            accrual.OnlineDurationTicksToday,
            accrual.LastCompensationDay);
        var receipt = new ProgressionIntervalSettlementReceipt(
            envelope.Subject.CharacterId,
            envelope.Command.OnlineSessionId,
            envelope.Command.IntervalSequence,
            envelope.Command.OnlineFromUtc,
            envelope.Command.OnlineUntilUtc,
            accrual.GainedEnergyX100,
            accrual.CompensationApplied,
            updatedBoostCount,
            projection,
            auditId.ToString(CultureInfo.InvariantCulture),
            eventId);
        var payload =
            ProgressionIntervalSettlementPersistenceCodec.Encode(
                receipt);
        var resultHash =
            ProgressionIntervalSettlementPersistenceCodec.Hash(payload);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            resultHash,
            auditId,
            payload,
            cancellationToken);
        await InsertOutboxAsync(
            connection,
            transaction,
            inboxId,
            aggregateKey,
            revision,
            eventId,
            payload,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ProgressionIntervalSettlementExecutionResult.Committed(
            receipt);
    }

    private async Task<ProgressionIntervalSettlementExecutionResult>
        ReplayAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CommandEnvelope<ProgressionIntervalSettlementCommand> envelope,
            LockedCharacter character,
            ProgressionIntervalAuthorityState? authority,
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
            return ProgressionIntervalSettlementExecutionResult
                .RequestHashConflict();
        }

        var receipt = ValidateStoredResult(
            existing,
            envelope.Subject.CharacterId,
            envelope.Command);
        if (authority is null)
        {
            throw new InvalidDataException(
                "A durable progression interval has no authority row.");
        }

        var projection = CreateProjection(
            authority.Value,
            character);
        await RecordDuplicateAsync(
            connection,
            transaction,
            existing.InboxId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ProgressionIntervalSettlementExecutionResult.Duplicate(
            receipt,
            projection);
    }

    private static byte[] DecodeDigest(string value)
    {
        var bytes = Convert.FromHexString(value);
        if (bytes.Length != CommandEnvelopeContract.DigestBytes)
        {
            throw new InvalidDataException(
                "The progression command digest has an invalid size.");
        }

        return bytes;
    }
}
