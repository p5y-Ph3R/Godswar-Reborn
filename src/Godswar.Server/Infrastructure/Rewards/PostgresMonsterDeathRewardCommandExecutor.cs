using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Rewards;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.Rewards;

internal sealed partial class
    PostgresMonsterDeathRewardCommandExecutor :
    IMonsterDeathRewardCommandExecutor
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;
    private readonly short _maximumOutboxAttempts;
    private readonly IPostgresMonsterDeathRewardCommandProbe? _probe;

    public PostgresMonsterDeathRewardCommandExecutor(
        NpgsqlDataSource dataSource,
        PostgresOutboxDispatcherOptions options,
        IPostgresMonsterDeathRewardCommandProbe? probe = null)
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

    public async Task<MonsterDeathRewardExecutionResult> ExecuteAsync(
        CommandEnvelope<MonsterDeathRewardCommand> envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var started = Stopwatch.GetTimestamp();
        var outcome = "provider_unavailable";
        try
        {
            var validation =
                MonsterDeathRewardCommandEnvelope.Validate(envelope);
            if (validation ==
                CommandEnvelopeValidation.RequestHashConflict)
            {
                outcome = "request_hash_conflict";
                return MonsterDeathRewardExecutionResult
                    .RequestHashConflict();
            }
            if (validation != CommandEnvelopeValidation.Valid)
            {
                outcome = "invalid_intent";
                return MonsterDeathRewardExecutionResult
                    .InvalidIntent();
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
                MonsterDeathRewardPersistenceCodec.CommandFamily,
                outcome,
                Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<MonsterDeathRewardExecutionResult>
        ExecuteTransactionAsync(
            CommandEnvelope<MonsterDeathRewardCommand> envelope,
            CancellationToken cancellationToken)
    {
        var operationId = DecodeDigest(envelope.OperationId);
        var requestHash = DecodeDigest(envelope.RequestHash);
        var principalKey = envelope.Subject.AccountId.ToString(
            CultureInfo.InvariantCulture);
        var aggregateKey =
            MonsterDeathRewardPersistenceCodec.AggregateKey(
                envelope.Subject.CharacterId);

        await using var connection =
            await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await AcquireDeathIdentityLockAsync(
            connection,
            transaction,
            envelope.Command.DeathEventId,
            cancellationToken);
        await ReachAsync(
            PostgresMonsterDeathRewardCommandStage.DeathIdentityLocked,
            cancellationToken);

        var settlement = await ReadSettlementAsync(
            connection,
            transaction,
            envelope.Command.DeathEventId,
            cancellationToken);
        if (settlement is not null)
        {
            return await ReplayAsync(
                connection,
                transaction,
                envelope,
                settlement,
                requestHash,
                cancellationToken);
        }

        var character = await LockCharacterAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            envelope.Subject.CharacterId,
            cancellationToken);
        if (character is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return MonsterDeathRewardExecutionResult
                .PreconditionFailed();
        }

        if (!TryDeriveReward(
                character.Value,
                envelope.Command,
                out var reward))
        {
            await transaction.RollbackAsync(cancellationToken);
            return MonsterDeathRewardExecutionResult
                .PreconditionFailed();
        }

        var eventId = Guid.NewGuid();
        var auditId = await InsertAuditAsync(
            connection,
            transaction,
            envelope,
            character.Value,
            reward,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            cancellationToken);
        await ReachAsync(
            PostgresMonsterDeathRewardCommandStage.AuditInserted,
            cancellationToken);

        var receipt = CreateReceipt(
            envelope,
            character.Value,
            reward,
            auditId,
            eventId);
        var payload =
            MonsterDeathRewardPersistenceCodec.Encode(receipt);
        var inboxId = await InsertInboxAsync(
            connection,
            transaction,
            principalKey,
            aggregateKey,
            operationId,
            requestHash,
            MonsterDeathRewardPersistenceCodec.Hash(payload),
            auditId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresMonsterDeathRewardCommandStage.InboxInserted,
            cancellationToken);

        if (!await UpdateProgressionAsync(
                connection,
                transaction,
                envelope,
                character.Value,
                reward,
                cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return MonsterDeathRewardExecutionResult
                .RevisionConflict();
        }
        await ReachAsync(
            PostgresMonsterDeathRewardCommandStage.ProgressionUpdated,
            cancellationToken);

        await InsertOutboxAsync(
            connection,
            transaction,
            inboxId,
            aggregateKey,
            reward.Revision,
            eventId,
            payload,
            cancellationToken);
        await ReachAsync(
            PostgresMonsterDeathRewardCommandStage.OutboxInserted,
            cancellationToken);

        await InsertSettlementAsync(
            connection,
            transaction,
            envelope,
            requestHash,
            reward.Revision,
            inboxId,
            auditId,
            eventId,
            cancellationToken);
        await ReachAsync(
            PostgresMonsterDeathRewardCommandStage.SettlementInserted,
            cancellationToken);
        await ReachAsync(
            PostgresMonsterDeathRewardCommandStage.BeforeCommit,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await ReachAsync(
            PostgresMonsterDeathRewardCommandStage.AfterCommit,
            cancellationToken);
        return MonsterDeathRewardExecutionResult.Committed(receipt);
    }

    private async Task<MonsterDeathRewardExecutionResult> ReplayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<MonsterDeathRewardCommand> envelope,
        StoredSettlement settlement,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        var existing = await ReadInboxByIdAsync(
            connection,
            transaction,
            settlement.InboxId,
            cancellationToken);
        if (existing is null)
        {
            throw new InvalidDataException(
                "The monster reward settlement has no durable inbox.");
        }

        var sameOwner =
            settlement.AccountId == envelope.Subject.AccountId &&
            settlement.CharacterId == envelope.Subject.CharacterId;
        var sameRequest =
            CryptographicOperations.FixedTimeEquals(
                settlement.RequestHash,
                requestHash) &&
            CryptographicOperations.FixedTimeEquals(
                existing.RequestHash,
                requestHash);
        if (!sameOwner || !sameRequest)
        {
            await RecordRequestConflictAsync(
                connection,
                transaction,
                existing.InboxId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return MonsterDeathRewardExecutionResult
                .RequestHashConflict();
        }

        var receipt = ValidateStoredResult(
            existing,
            envelope.Command,
            envelope.Subject.CharacterId);
        var character = await LockCharacterAsync(
            connection,
            transaction,
            envelope.Subject.AccountId,
            envelope.Subject.CharacterId,
            cancellationToken);
        if (character is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return MonsterDeathRewardExecutionResult
                .PreconditionFailed();
        }
        if (character.Value.Revision < receipt.ProgressionRevision)
        {
            throw new InvalidDataException(
                "The current progression predates its durable reward.");
        }

        await RecordDuplicateAsync(
            connection,
            transaction,
            existing.InboxId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MonsterDeathRewardExecutionResult.Duplicate(
            receipt,
            character.Value.ToProjection());
    }

    private static bool TryDeriveReward(
        LockedCharacter character,
        MonsterDeathRewardCommand command,
        out DerivedReward reward)
    {
        reward = default;
        try
        {
            var fighter = PlayerExperienceCatalog.Apply(
                character.Level,
                character.Experience,
                command.AwardedExperience);
            var accumulatedTalentExperience = checked(
                (long)character.TalentExperience +
                command.AwardedTalentExperience);
            var pointsGained = accumulatedTalentExperience / 100;
            var currentPoints = checked(
                (long)character.TalentPoints + pointsGained);
            var revision = checked(character.Revision + 1);
            if (currentPoints > int.MaxValue)
            {
                return false;
            }

            reward = new DerivedReward(
                fighter,
                checked((int)(accumulatedTalentExperience % 100)),
                checked((int)pointsGained),
                checked((int)currentPoints),
                revision);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static MonsterDeathRewardExecutionReceipt CreateReceipt(
        CommandEnvelope<MonsterDeathRewardCommand> envelope,
        LockedCharacter before,
        DerivedReward reward,
        long auditId,
        Guid eventId) =>
        new(
            envelope.Command.DeathEventId,
            envelope.Command.RuntimeInstanceId,
            envelope.Command.MapId,
            envelope.Command.MonsterObjectId,
            envelope.Command.SpawnGeneration,
            envelope.Command.DeathHealthRevision,
            envelope.Subject.CharacterId,
            envelope.Command.AwardedExperience,
            envelope.Command.AwardedTalentExperience,
            reward.Fighter.ExperienceGained,
            before.Level,
            reward.Fighter.Level,
            before.Experience,
            reward.Fighter.Experience,
            PlayerExperienceCatalog.GetNextLevelExperience(
                reward.Fighter.Level),
            reward.Fighter.LevelUps
                .Select(static levelUp =>
                    new MonsterDeathRewardLevelUp(
                        levelUp.Level,
                        levelUp.CurrentExperience,
                        levelUp.NextLevelExperience))
                .ToArray(),
            envelope.Command.AwardedTalentExperience,
            before.TalentExperience,
            reward.TalentExperience,
            reward.TalentPointsGained,
            before.TalentPoints,
            reward.TalentPoints,
            reward.Revision,
            auditId.ToString(CultureInfo.InvariantCulture),
            eventId);

    private static MonsterDeathRewardExecutionReceipt
        ValidateStoredResult(
            StoredInbox stored,
            MonsterDeathRewardCommand command,
            int characterId)
    {
        if (stored.ResultContractVersion !=
                MonsterDeathRewardPersistenceCodec.ContractVersion ||
            !string.Equals(
                stored.ResultCode,
                MonsterDeathRewardPersistenceCodec.ResultCode,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The stored monster reward contract is unsupported.");
        }

        var receipt =
            MonsterDeathRewardPersistenceCodec.DecodeAndVerify(
                stored.ResultPayload,
                stored.ResultHash,
                stored.ResultCode,
                stored.AuditId);
        if (receipt.DeathEventId != command.DeathEventId ||
            receipt.RuntimeInstanceId != command.RuntimeInstanceId ||
            receipt.MapId != command.MapId ||
            receipt.MonsterObjectId != command.MonsterObjectId ||
            receipt.SpawnGeneration != command.SpawnGeneration ||
            receipt.DeathHealthRevision !=
                command.DeathHealthRevision ||
            receipt.CharacterId != characterId)
        {
            throw new InvalidDataException(
                "The stored monster reward identity is inconsistent.");
        }
        return receipt;
    }

    private static string OutcomeCode(
        MonsterDeathRewardExecutionDisposition disposition) =>
        disposition switch
        {
            MonsterDeathRewardExecutionDisposition.Committed =>
                "committed",
            MonsterDeathRewardExecutionDisposition.Duplicate =>
                "duplicate",
            MonsterDeathRewardExecutionDisposition
                .RequestHashConflict => "request_hash_conflict",
            MonsterDeathRewardExecutionDisposition.InvalidIntent =>
                "invalid_intent",
            MonsterDeathRewardExecutionDisposition
                .PreconditionFailed => "precondition_failed",
            MonsterDeathRewardExecutionDisposition.RevisionConflict =>
                "revision_conflict",
            _ => throw new InvalidOperationException(
                "Unknown monster reward outcome.")
        };

    private async ValueTask ReachAsync(
        PostgresMonsterDeathRewardCommandStage stage,
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
        int Level,
        int Experience,
        int TalentExperience,
        int TalentPoints,
        long Revision)
    {
        public MonsterDeathRewardProjection ToProjection() =>
            new(
                Level,
                Experience,
                PlayerExperienceCatalog.GetNextLevelExperience(Level),
                TalentExperience,
                TalentPoints,
                Revision);
    }

    private readonly record struct DerivedReward(
        PlayerExperienceProgression Fighter,
        int TalentExperience,
        int TalentPointsGained,
        int TalentPoints,
        long Revision);

    private sealed record StoredSettlement(
        long InboxId,
        int AccountId,
        int CharacterId,
        byte[] RequestHash);

    private sealed record StoredInbox(
        long InboxId,
        byte[] RequestHash,
        short ResultContractVersion,
        string ResultCode,
        string ResultPayload,
        byte[] ResultHash,
        long AuditId);
}
