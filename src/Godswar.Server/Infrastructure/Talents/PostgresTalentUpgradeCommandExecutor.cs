using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Talents;
using Godswar.Server.Infrastructure.Messaging;
using Npgsql;

namespace Godswar.Server.Infrastructure.Talents;

internal sealed partial class PostgresTalentUpgradeCommandExecutor :
    ITalentUpgradeCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly IPostgresTalentUpgradeCommandProbe? _probe;

    public PostgresTalentUpgradeCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        IPostgresTalentUpgradeCommandProbe? probe = null)
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

    public async Task<TalentUpgradeExecutionResult> ExecuteAsync(
        CommandEnvelope<TalentUpgradeCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation =
                TalentUpgradeCommandEnvelope.Validate(envelope);
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return TalentUpgradeExecutionResult
                    .RequestHashConflict();
            }

            if (validation != CommandEnvelopeValidation.Valid)
            {
                outcome = "invalid_intent";
                return TalentUpgradeExecutionResult.InvalidIntent();
            }

            var result = await ExecuteTransactionAsync(
                envelope,
                cancellationToken);
            outcome = result.Disposition switch
            {
                TalentUpgradeExecutionDisposition.Committed =>
                    "committed",
                TalentUpgradeExecutionDisposition.Duplicate =>
                    "duplicate",
                TalentUpgradeExecutionDisposition.RequestHashConflict =>
                    "request_hash_conflict",
                TalentUpgradeExecutionDisposition.InvalidIntent =>
                    "invalid_intent",
                TalentUpgradeExecutionDisposition.PreconditionFailed =>
                    "precondition_failed",
                _ => throw new InvalidOperationException(
                    "Unknown talent command outcome.")
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
                TalentUpgradePersistenceCodec.CommandFamily,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<TalentUpgradeExecutionResult>
        ExecuteTransactionAsync(
            CommandEnvelope<TalentUpgradeCommand> envelope,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(envelope.OperationId);
        var requestHash = DecodeDigest(envelope.RequestHash);
        var principalKey = envelope.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey =
            TalentUpgradePersistenceCodec.AggregateKey(
                envelope.Subject.CharacterId,
                envelope.Command.TalentId);

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);

        var character = await LockCharacterAsync(
            connection,
            transaction,
            envelope,
            cancellationToken);
        if (character is null)
        {
            return TalentUpgradeExecutionResult.PreconditionFailed();
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
                return TalentUpgradeExecutionResult
                    .RequestHashConflict();
            }

            var replayReceipt = ValidateStoredResult(existing);
            var currentTalent = await ReadTalentAsync(
                connection,
                transaction,
                envelope.Subject.CharacterId,
                envelope.Command.TalentId,
                cancellationToken);
            await RecordDuplicateAsync(
                connection,
                transaction,
                existing.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return TalentUpgradeExecutionResult.Duplicate(
                replayReceipt,
                currentTalent?.Rank ?? replayReceipt.Rank,
                character.Value.TalentPoints);
        }

        if (!await TalentBelongsToProfessionAsync(
                connection,
                transaction,
                envelope.Command.TalentId,
                character.Value.Profession,
                cancellationToken))
        {
            return TalentUpgradeExecutionResult.PreconditionFailed();
        }

        var talent = await ReadTalentAsync(
            connection,
            transaction,
            envelope.Subject.CharacterId,
            envelope.Command.TalentId,
            cancellationToken);
        var currentRank = talent?.Rank ?? 0;
        if (currentRank >= TalentProgression.RankCap ||
            currentRank != envelope.Command.ExpectedRank)
        {
            return TalentUpgradeExecutionResult.PreconditionFailed();
        }

        var requiredLevel =
            TalentProgression.CalculateRequiredPlayerLevel(currentRank);
        var cost =
            TalentProgression.CalculateUpgradeCost(currentRank);
        if (character.Value.Level < requiredLevel ||
            character.Value.TalentPoints < cost)
        {
            return TalentUpgradeExecutionResult.PreconditionFailed();
        }

        var newRank = checked(currentRank + 1);
        var remainingPoints =
            checked(character.Value.TalentPoints - cost);
        var aggregateRevision =
            checked((talent?.OutboxRevision ?? 0) + 1);
        var eventId = Guid.NewGuid();

        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
        var receipt = new TalentUpgradeExecutionReceipt(
            envelope.Subject.CharacterId,
            envelope.Command.TalentId,
            newRank,
            cost,
            remainingPoints,
            TalentProgression.CalculateDisplayValue(newRank),
            aggregateRevision,
            auditId.ToString(CultureInfo.InvariantCulture),
            eventId);
        var payload = TalentUpgradePersistenceCodec.Encode(receipt);
        var resultHash =
            TalentUpgradePersistenceCodec.Hash(payload);

        await ReachAsync(
            PostgresTalentUpgradeCommandStage.AuditInserted,
            cancellationToken);
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
        await ReachAsync(
            PostgresTalentUpgradeCommandStage.InboxInserted,
            cancellationToken);

        var persistedRevision = await ApplyMutationAsync(
            connection,
            transaction,
            envelope,
            newRank,
            remainingPoints,
            cancellationToken);
        if (persistedRevision != aggregateRevision)
        {
            throw new InvalidDataException(
                "The talent outbox revision changed unexpectedly.");
        }

        await ReachAsync(
            PostgresTalentUpgradeCommandStage.MutationApplied,
            cancellationToken);
        await InsertOutboxAsync(
            connection,
            transaction,
            inboxId,
            aggregateKey,
            aggregateRevision,
            eventId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresTalentUpgradeCommandStage.OutboxInserted,
            cancellationToken);
        await ReachAsync(
            PostgresTalentUpgradeCommandStage.BeforeCommit,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ReachAsync(
            PostgresTalentUpgradeCommandStage.AfterCommit,
            cancellationToken);
        return TalentUpgradeExecutionResult.Committed(receipt);
    }

    private async ValueTask ReachAsync(
        PostgresTalentUpgradeCommandStage stage,
        CancellationToken cancellationToken)
    {
        if (_probe is not null)
        {
            await _probe.ReachedAsync(stage, cancellationToken);
        }
    }

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
        int TalentPoints,
        int Level,
        int Profession);

    private readonly record struct StoredTalent(
        int Rank,
        long OutboxRevision);

    private sealed record StoredInbox(
        long InboxId,
        byte[] RequestHash,
        short ResultContractVersion,
        string ResultCode,
        string ResultPayload,
        byte[] ResultHash,
        long AuditId);
}
