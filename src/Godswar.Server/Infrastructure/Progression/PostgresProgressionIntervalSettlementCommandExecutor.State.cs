using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Progression;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Progression;

internal sealed partial class
    PostgresProgressionIntervalSettlementCommandExecutor
{
    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                zodiac_level,
                zodiac_energy,
                zodiac_energy_remainder_x100,
                zodiac_online_day,
                zodiac_online_duration_ticks,
                zodiac_last_online_at,
                zodiac_last_compensation_day
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId
              AND lifecycle_state = 'active'
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "accountId",
            subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LockedCharacter(
                checked((byte)reader.GetInt16(0)),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.IsDBNull(3)
                    ? null
                    : reader.GetFieldValue<DateOnly>(3),
                reader.GetInt64(4),
                reader.IsDBNull(5)
                    ? null
                    : AsUtc(reader.GetDateTime(5)),
                reader.IsDBNull(6)
                    ? null
                    : reader.GetFieldValue<DateOnly>(6))
            : null;
    }

    private async Task<ProgressionIntervalAuthorityState?> ReadAuthorityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                online_session_id,
                last_interval_sequence,
                last_interval_end,
                aggregate_revision
            FROM public.character_progression_interval_authority
            WHERE character_id = @characterId
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ProgressionIntervalAuthorityState(
                reader.GetGuid(0),
                reader.GetInt64(1),
                AsUtc(reader.GetDateTime(2)),
                reader.GetInt64(3))
            : null;
    }

    private async Task ApplyZodiacMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        ZodiacEnergyAccrualResult accrual,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET zodiac_energy = @energy,
                zodiac_energy_remainder_x100 = @energyRemainder,
                zodiac_online_day = @onlineDay,
                zodiac_online_duration_ticks = @onlineDurationTicks,
                zodiac_last_online_at = @lastOnlineAt,
                zodiac_last_compensation_day = @lastCompensationDay
            WHERE account_id = @accountId
              AND id = @characterId
              AND lifecycle_state = 'active';
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "accountId",
            subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        command.Parameters.AddWithValue(
            "energy",
            accrual.CurrentEnergy);
        command.Parameters.AddWithValue(
            "energyRemainder",
            accrual.CurrentEnergyRemainderX100);
        command.Parameters.Add(
            "onlineDay",
            NpgsqlDbType.Date).Value = accrual.OnlineDay;
        command.Parameters.AddWithValue(
            "onlineDurationTicks",
            accrual.OnlineDurationTicksToday);
        command.Parameters.Add(
            "lastOnlineAt",
            NpgsqlDbType.TimestampTz).Value =
            accrual.LastOnlineAt.UtcDateTime;
        command.Parameters.Add(
            "lastCompensationDay",
            NpgsqlDbType.Date).Value =
            accrual.LastCompensationDay.HasValue
                ? accrual.LastCompensationDay.Value
                : DBNull.Value;
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The progression Zodiac mutation was not exact.");
        }
    }

    private async Task<int> ConsumeBoostOnlineTimeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandSubject subject,
        ProgressionIntervalSettlementCommand interval,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_experience_modifiers modifier
            SET remaining_online_ticks = GREATEST(
                0,
                COALESCE(
                    modifier.remaining_online_ticks,
                    GREATEST(
                        0,
                        ROUND(EXTRACT(EPOCH FROM (
                            modifier.expires_at -
                            modifier.activated_at
                        )) * 10000000)::bigint
                    )
                ) - GREATEST(
                    0,
                    ROUND(EXTRACT(EPOCH FROM (
                        @onlineUntil -
                        GREATEST(
                            @onlineFrom,
                            modifier.activated_at
                        )
                    )) * 10000000)::bigint
                )
            )
            FROM public.character_base character
            WHERE modifier.character_id = @characterId
              AND character.id = modifier.character_id
              AND character.account_id = @accountId
              AND character.lifecycle_state = 'active'
              AND (
                  modifier.remaining_online_ticks > 0
                  OR (
                      modifier.remaining_online_ticks IS NULL
                      AND modifier.expires_at IS NOT NULL
                      AND modifier.expires_at >
                          modifier.activated_at
                  )
              )
              AND modifier.activated_at < @onlineUntil;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "accountId",
            subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            subject.CharacterId);
        command.Parameters.Add(
            "onlineFrom",
            NpgsqlDbType.TimestampTz).Value =
            interval.OnlineFromUtc.UtcDateTime;
        command.Parameters.Add(
            "onlineUntil",
            NpgsqlDbType.TimestampTz).Value =
            interval.OnlineUntilUtc.UtcDateTime;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpsertAuthorityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        ProgressionIntervalSettlementCommand interval,
        long aggregateRevision,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_progression_interval_authority (
                character_id,
                online_session_id,
                last_interval_sequence,
                last_interval_end,
                aggregate_revision,
                updated_at
            )
            VALUES (
                @characterId,
                @onlineSessionId,
                @lastIntervalSequence,
                @lastIntervalEnd,
                @aggregateRevision,
                now()
            )
            ON CONFLICT (character_id) DO UPDATE
            SET online_session_id = EXCLUDED.online_session_id,
                last_interval_sequence =
                    EXCLUDED.last_interval_sequence,
                last_interval_end = EXCLUDED.last_interval_end,
                aggregate_revision = EXCLUDED.aggregate_revision,
                updated_at = now()
            RETURNING aggregate_revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "onlineSessionId",
            interval.OnlineSessionId);
        command.Parameters.AddWithValue(
            "lastIntervalSequence",
            interval.IntervalSequence);
        command.Parameters.Add(
            "lastIntervalEnd",
            NpgsqlDbType.TimestampTz).Value =
            interval.OnlineUntilUtc.UtcDateTime;
        command.Parameters.AddWithValue(
            "aggregateRevision",
            aggregateRevision);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is not long persistedRevision ||
            persistedRevision != aggregateRevision)
        {
            throw new InvalidDataException(
                "The progression interval authority did not advance exactly.");
        }
    }

    private static ProgressionIntervalProjection CreateProjection(
        ProgressionIntervalAuthorityState authority,
        LockedCharacter character)
    {
        var onlineDay = character.ZodiacOnlineDay ??
            throw new InvalidDataException(
                "The progression authority has no Zodiac calendar day.");
        return new ProgressionIntervalProjection(
            authority.OnlineSessionId,
            authority.LastIntervalSequence,
            authority.LastIntervalEndUtc,
            authority.AggregateRevision,
            character.ZodiacEnergy,
            character.ZodiacEnergyRemainderX100,
            onlineDay,
            character.ZodiacOnlineDurationTicksToday,
            character.ZodiacLastCompensationDay);
    }

    private NpgsqlCommand CreateCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = _commandTimeoutSeconds
        };

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private readonly record struct LockedCharacter(
        byte ZodiacLevel,
        int ZodiacEnergy,
        int ZodiacEnergyRemainderX100,
        DateOnly? ZodiacOnlineDay,
        long ZodiacOnlineDurationTicksToday,
        DateTimeOffset? ZodiacLastOnlineAt,
        DateOnly? ZodiacLastCompensationDay)
    {
        public GameCharacter ToDomainCharacter() => new()
        {
            ZodiacLevel = ZodiacLevel,
            ZodiacEnergy = ZodiacEnergy,
            ZodiacEnergyRemainderX100 =
                ZodiacEnergyRemainderX100,
            ZodiacOnlineDay = ZodiacOnlineDay,
            ZodiacOnlineDurationTicksToday =
                ZodiacOnlineDurationTicksToday,
            ZodiacLastOnlineAt = ZodiacLastOnlineAt,
            ZodiacLastCompensationDay =
                ZodiacLastCompensationDay
        };
    }

    private sealed record StoredInbox(
        long InboxId,
        byte[] RequestHash,
        short ResultContractVersion,
        string ResultCode,
        string ResultPayload,
        byte[] ResultHash,
        long AuditId);
}
