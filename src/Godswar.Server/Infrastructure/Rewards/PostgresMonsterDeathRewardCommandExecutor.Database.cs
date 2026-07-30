using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Rewards;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Rewards;

internal sealed partial class
    PostgresMonsterDeathRewardCommandExecutor
{
    private async Task AcquireDeathIdentityLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid deathEventId,
        CancellationToken cancellationToken)
    {
        Span<byte> bytes = stackalloc byte[16];
        deathEventId.TryWriteBytes(
            bytes,
            bigEndian: true,
            out _);
        var lockKey = BinaryPrimitives.ReadInt64BigEndian(bytes);
        await using var command = CreateCommand(
            "SELECT pg_advisory_xact_lock(@lockKey);",
            connection,
            transaction);
        command.Parameters.AddWithValue("lockKey", lockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<StoredSettlement?> ReadSettlementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid deathEventId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                command_inbox_id,
                account_id,
                character_id,
                request_hash
            FROM public.monster_death_reward_settlements
            WHERE death_event_id = @deathEventId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("deathEventId", deathEventId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredSettlement(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetFieldValue<byte[]>(3))
            : null;
    }

    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                fighter_job_lv,
                fighter_job_exp,
                "SkillExp",
                "SkillPoint",
                progression_reward_revision
            FROM public.character_base
            WHERE id = @characterId
              AND account_id = @accountId
              AND lifecycle_state = 'active'
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var character = new LockedCharacter(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt64(4));
        if (character.Level is < 1 or >
                PlayerExperienceCatalog.MaximumLevel ||
            character.Experience < 0 ||
            character.TalentExperience is < 0 or >= 100 ||
            character.TalentPoints < 0 ||
            character.Revision < 0)
        {
            throw new InvalidDataException(
                "The locked character progression is invalid.");
        }
        return character;
    }

    private async Task<StoredInbox?> ReadInboxByIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id,
                request_hash,
                result_contract_version,
                result_code,
                result_payload::text,
                result_hash,
                audit_id
            FROM public.command_inbox
            WHERE id = @inboxId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredInbox(
                reader.GetInt64(0),
                reader.GetFieldValue<byte[]>(1),
                reader.GetInt16(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<byte[]>(5),
                reader.GetInt64(6))
            : null;
    }

    private async Task<long> InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<MonsterDeathRewardCommand> envelope,
        LockedCharacter before,
        DerivedReward reward,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        CancellationToken cancellationToken)
    {
        var detailPayload = JsonSerializer.Serialize(new
        {
            deathEventId = envelope.Command.DeathEventId,
            runtimeInstanceId = envelope.Command.RuntimeInstanceId,
            mapId = envelope.Command.MapId,
            monsterObjectId = envelope.Command.MonsterObjectId,
            spawnGeneration = envelope.Command.SpawnGeneration,
            deathHealthRevision =
                envelope.Command.DeathHealthRevision,
            requestedExperience =
                envelope.Command.AwardedExperience,
            requestedTalentExperience =
                envelope.Command.AwardedTalentExperience,
            previousLevel = before.Level,
            currentLevel = reward.Fighter.Level,
            previousExperience = before.Experience,
            currentExperience = reward.Fighter.Experience,
            previousTalentExperience = before.TalentExperience,
            currentTalentExperience = reward.TalentExperience,
            previousTalentPoints = before.TalentPoints,
            currentTalentPoints = reward.TalentPoints,
            progressionRevision = reward.Revision
        });
        await using var command = CreateCommand(
            """
            INSERT INTO public.command_audit (
                principal_type,
                principal_key,
                aggregate_type,
                aggregate_key,
                command_family,
                operation_id,
                request_hash,
                outcome_code,
                detail_payload,
                retention_policy
            )
            VALUES (
                @principalType,
                @principalKey,
                @aggregateType,
                @aggregateKey,
                @commandFamily,
                @operationId,
                @requestHash,
                @outcomeCode,
                @detailPayload,
                @retentionPolicy
            )
            RETURNING id;
            """,
            connection,
            transaction);
        AddIdentityParameters(
            command,
            principalKey,
            aggregateKey,
            operationId);
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue(
            "outcomeCode",
            MonsterDeathRewardPersistenceCodec.ResultCode);
        command.Parameters.Add(
            "detailPayload",
            NpgsqlDbType.Jsonb).Value = detailPayload;
        command.Parameters.AddWithValue(
            "retentionPolicy",
            MonsterDeathRewardPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long auditId && auditId > 0
            ? auditId
            : throw new InvalidDataException(
                "The reward audit insert returned no identity.");
    }

    private async Task<long> InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string principalKey,
        string aggregateKey,
        byte[] operationId,
        byte[] requestHash,
        byte[] resultHash,
        long auditId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.command_inbox (
                principal_type,
                principal_key,
                aggregate_type,
                aggregate_key,
                command_family,
                operation_id,
                request_hash,
                result_contract_version,
                result_code,
                result_payload,
                result_hash,
                audit_id,
                retention_policy
            )
            VALUES (
                @principalType,
                @principalKey,
                @aggregateType,
                @aggregateKey,
                @commandFamily,
                @operationId,
                @requestHash,
                @resultContractVersion,
                @resultCode,
                @resultPayload,
                @resultHash,
                @auditId,
                @retentionPolicy
            )
            RETURNING id;
            """,
            connection,
            transaction);
        AddIdentityParameters(
            command,
            principalKey,
            aggregateKey,
            operationId);
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue(
            "resultContractVersion",
            MonsterDeathRewardPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "resultCode",
            MonsterDeathRewardPersistenceCodec.ResultCode);
        command.Parameters.Add(
            "resultPayload",
            NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(payload);
        command.Parameters.Add(
            "resultHash",
            NpgsqlDbType.Bytea).Value = resultHash;
        command.Parameters.AddWithValue("auditId", auditId);
        command.Parameters.AddWithValue(
            "retentionPolicy",
            MonsterDeathRewardPersistenceCodec.RetentionPolicy);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is long inboxId && inboxId > 0
            ? inboxId
            : throw new InvalidDataException(
                "The reward inbox insert returned no identity.");
    }

    private async Task<bool> UpdateProgressionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<MonsterDeathRewardCommand> envelope,
        LockedCharacter before,
        DerivedReward reward,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET fighter_job_lv = @level,
                fighter_job_exp = @experience,
                "SkillExp" = @talentExperience,
                "SkillPoint" = @talentPoints,
                progression_reward_revision = @newRevision
            WHERE id = @characterId
              AND account_id = @accountId
              AND lifecycle_state = 'active'
              AND progression_reward_revision = @expectedRevision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "level",
            reward.Fighter.Level);
        command.Parameters.AddWithValue(
            "experience",
            reward.Fighter.Experience);
        command.Parameters.AddWithValue(
            "talentExperience",
            reward.TalentExperience);
        command.Parameters.AddWithValue(
            "talentPoints",
            reward.TalentPoints);
        command.Parameters.AddWithValue(
            "newRevision",
            reward.Revision);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        command.Parameters.AddWithValue(
            "accountId",
            envelope.Subject.AccountId);
        command.Parameters.AddWithValue(
            "expectedRevision",
            before.Revision);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        string aggregateKey,
        long aggregateRevision,
        Guid eventId,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.outbox_events (
                event_id,
                command_inbox_id,
                consumer_key,
                aggregate_type,
                aggregate_key,
                aggregate_version,
                event_type,
                contract_version,
                ordering_policy,
                payload,
                max_attempts
            )
            VALUES (
                @eventId,
                @inboxId,
                @consumerKey,
                @aggregateType,
                @aggregateKey,
                @aggregateVersion,
                @eventType,
                @contractVersion,
                @orderingPolicy,
                @payload,
                @maxAttempts
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("eventId", eventId);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue(
            "consumerKey",
            MonsterDeathRewardPersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            MonsterDeathRewardPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "aggregateVersion",
            aggregateRevision);
        command.Parameters.AddWithValue(
            "eventType",
            MonsterDeathRewardPersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "contractVersion",
            MonsterDeathRewardPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            MonsterDeathRewardPersistenceCodec.OrderingPolicy);
        command.Parameters.Add(
            "payload",
            NpgsqlDbType.Jsonb).Value =
            Encoding.UTF8.GetString(payload);
        command.Parameters.AddWithValue(
            "maxAttempts",
            _maximumOutboxAttempts);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The reward outbox insert was not exact.");
        }
    }

    private async Task InsertSettlementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<MonsterDeathRewardCommand> envelope,
        byte[] requestHash,
        long progressionRevision,
        long inboxId,
        long auditId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.monster_death_reward_settlements (
                death_event_id,
                runtime_instance_id,
                map_id,
                monster_object_id,
                spawn_generation,
                death_health_revision,
                account_id,
                character_id,
                request_hash,
                progression_revision,
                command_inbox_id,
                audit_id,
                outbox_event_id
            )
            VALUES (
                @deathEventId,
                @runtimeInstanceId,
                @mapId,
                @monsterObjectId,
                @spawnGeneration,
                @deathHealthRevision,
                @accountId,
                @characterId,
                @requestHash,
                @progressionRevision,
                @inboxId,
                @auditId,
                @eventId
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "deathEventId",
            envelope.Command.DeathEventId);
        command.Parameters.AddWithValue(
            "runtimeInstanceId",
            envelope.Command.RuntimeInstanceId);
        command.Parameters.AddWithValue(
            "mapId",
            checked((short)envelope.Command.MapId));
        command.Parameters.AddWithValue(
            "monsterObjectId",
            checked((long)envelope.Command.MonsterObjectId));
        command.Parameters.AddWithValue(
            "spawnGeneration",
            checked((long)envelope.Command.SpawnGeneration));
        command.Parameters.AddWithValue(
            "deathHealthRevision",
            checked((long)envelope.Command.DeathHealthRevision));
        command.Parameters.AddWithValue(
            "accountId",
            envelope.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        command.Parameters.Add(
            "requestHash",
            NpgsqlDbType.Bytea).Value = requestHash;
        command.Parameters.AddWithValue(
            "progressionRevision",
            progressionRevision);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue("auditId", auditId);
        command.Parameters.AddWithValue("eventId", eventId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The global reward settlement insert was not exact.");
        }
    }

}
