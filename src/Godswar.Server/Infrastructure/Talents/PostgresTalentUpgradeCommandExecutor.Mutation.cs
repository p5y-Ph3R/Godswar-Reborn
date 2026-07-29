using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Talents;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Talents;

internal sealed partial class PostgresTalentUpgradeCommandExecutor
{
    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<TalentUpgradeCommand> envelope,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                cb."SkillPoint",
                cb.fighter_job_lv,
                cb.profession::integer
            FROM public.character_base cb
            WHERE cb.account_id = @accountId
              AND cb.id = @characterId
            FOR UPDATE OF cb;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "accountId",
            envelope.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LockedCharacter(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2))
            : null;
    }

    private async Task<bool> TalentBelongsToProfessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int talentId,
        int profession,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.talent_templates
                WHERE id = @talentId
                  AND class_id = @profession
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("talentId", talentId);
        command.Parameters.AddWithValue(
            "profession",
            checked((short)profession));
        return await command.ExecuteScalarAsync(cancellationToken)
            is true;
    }

    private async Task<StoredTalent?> ReadTalentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int talentId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT rank::integer, outbox_revision
            FROM public.character_talents
            WHERE user_id = @characterId
              AND talent_id = @talentId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("talentId", talentId);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredTalent(
                reader.GetInt32(0),
                reader.GetInt64(1))
            : null;
    }

    private async Task<long> ApplyMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<TalentUpgradeCommand> envelope,
        int newRank,
        int remainingPoints,
        CancellationToken cancellationToken)
    {
        await using (var command = CreateCommand(
            """
            INSERT INTO public.character_talents (
                user_id,
                talent_id,
                rank,
                outbox_revision,
                updated_at
            )
            VALUES (
                @characterId,
                @talentId,
                @rank,
                1,
                now()
            )
            ON CONFLICT (user_id, talent_id) DO UPDATE
            SET rank = EXCLUDED.rank,
                outbox_revision =
                    public.character_talents.outbox_revision + 1,
                updated_at = now()
            RETURNING outbox_revision;
            """,
            connection,
            transaction))
        {
            command.Parameters.AddWithValue(
                "characterId",
                envelope.Subject.CharacterId);
            command.Parameters.AddWithValue(
                "talentId",
                envelope.Command.TalentId);
            command.Parameters.AddWithValue(
                "rank",
                checked((short)newRank));
            var scalar =
                await command.ExecuteScalarAsync(cancellationToken);
            if (scalar is not long revision || revision <= 0)
            {
                throw new InvalidDataException(
                    "The talent mutation returned no outbox revision.");
            }

            await using var pointCommand = CreateCommand(
                """
                UPDATE public.character_base
                SET "SkillPoint" = @remainingPoints
                WHERE account_id = @accountId
                  AND id = @characterId;
                """,
                connection,
                transaction);
            pointCommand.Parameters.AddWithValue(
                "remainingPoints",
                remainingPoints);
            pointCommand.Parameters.AddWithValue(
                "accountId",
                envelope.Subject.AccountId);
            pointCommand.Parameters.AddWithValue(
                "characterId",
                envelope.Subject.CharacterId);
            if (await pointCommand.ExecuteNonQueryAsync(
                    cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The talent-point mutation was not exact.");
            }

            return revision;
        }
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
            TalentUpgradePersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            TalentUpgradePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "aggregateVersion",
            aggregateRevision);
        command.Parameters.AddWithValue(
            "eventType",
            TalentUpgradePersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "contractVersion",
            TalentUpgradePersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            TalentUpgradePersistenceCodec.OrderingPolicy);
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
                "The talent outbox insert was not exact.");
        }
    }
}
