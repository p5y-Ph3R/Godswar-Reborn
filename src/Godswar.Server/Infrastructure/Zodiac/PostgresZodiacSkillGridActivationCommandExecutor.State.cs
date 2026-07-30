using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Zodiac;

internal sealed partial class
    PostgresZodiacSkillGridActivationCommandExecutor
{
    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<ZodiacSkillGridActivationCommand> envelope,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT cb."Stone", cb.wallet_revision
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
                reader.GetInt64(1))
            : null;
    }

    private async Task<StoredGrid> ReadGridAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int gridIndex,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT level, selected_skill_id
            FROM public.character_zodiac_skill_grids
            WHERE user_id = @characterId
              AND grid_index = @gridIndex;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "gridIndex",
            checked((short)gridIndex));

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new StoredGrid(
                checked((byte)reader.GetInt16(0)),
                reader.GetInt32(1))
            : new StoredGrid(
                ZodiacSkillGridActivationCommandEnvelope
                    .ExpectedInactiveLevel,
                ZodiacSkillGridActivationCommandEnvelope
                    .NoSelectedSkillId);
    }

    private async Task ApplyGridMutationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int gridIndex,
        byte level,
        int selectedSkillId,
        CancellationToken cancellationToken)
    {
        if (level !=
            ZodiacSkillGridActivationCommandEnvelope.ActivatedLevel)
        {
            throw new InvalidDataException(
                "A Zodiac activation must persist level one.");
        }

        await using var command = CreateCommand(
            """
            INSERT INTO public.character_zodiac_skill_grids (
                user_id,
                grid_index,
                level,
                selected_skill_id,
                updated_at
            )
            VALUES (
                @characterId,
                @gridIndex,
                @level,
                @selectedSkillId,
                now()
            )
            ON CONFLICT (user_id, grid_index) DO UPDATE
            SET level = EXCLUDED.level,
                selected_skill_id = EXCLUDED.selected_skill_id,
                updated_at = now()
            WHERE public.character_zodiac_skill_grids.level = 0
            RETURNING level;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "gridIndex",
            checked((short)gridIndex));
        command.Parameters.AddWithValue("level", checked((short)level));
        command.Parameters.AddWithValue(
            "selectedSkillId",
            selectedSkillId);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        if (scalar is not short persistedLevel ||
            persistedLevel != level)
        {
            throw new InvalidDataException(
                "The Zodiac grid activation was not exact.");
        }
    }

    private async Task UpdateGoldWalletAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<ZodiacSkillGridActivationCommand> envelope,
        LockedCharacter character,
        int balanceAfter,
        long nextRevision,
        CancellationToken cancellationToken)
    {
        if (balanceAfter < 0 ||
            balanceAfter >= character.Gold ||
            nextRevision != character.WalletRevision + 1)
        {
            throw new InvalidDataException(
                "The Zodiac Gold transition is invalid.");
        }

        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET "Stone" = @balanceAfter,
                wallet_revision = @nextRevision
            WHERE account_id = @accountId
              AND id = @characterId
              AND "Stone" = @balanceBefore
              AND wallet_revision = @expectedRevision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("balanceAfter", balanceAfter);
        command.Parameters.AddWithValue("nextRevision", nextRevision);
        command.Parameters.AddWithValue(
            "accountId",
            envelope.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        command.Parameters.AddWithValue(
            "balanceBefore",
            character.Gold);
        command.Parameters.AddWithValue(
            "expectedRevision",
            character.WalletRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Zodiac Gold wallet did not advance exactly once.");
        }
    }

    private async Task InsertGoldLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        CommandEnvelope<ZodiacSkillGridActivationCommand> envelope,
        LockedCharacter character,
        int balanceAfter,
        long walletRevision,
        CancellationToken cancellationToken)
    {
        var delta = checked(balanceAfter - character.Gold);
        if (delta >= 0 ||
            walletRevision != character.WalletRevision + 1)
        {
            throw new InvalidDataException(
                "A Zodiac Gold ledger requires one negative revision.");
        }

        await using var command = CreateCommand(
            """
            INSERT INTO public.character_currency_ledger (
                command_inbox_id,
                account_id,
                character_id,
                wallet_revision,
                currency_code,
                delta,
                balance_before,
                balance_after,
                reason_code
            )
            VALUES (
                @inboxId,
                @accountId,
                @characterId,
                @walletRevision,
                'gold',
                @delta,
                @balanceBefore,
                @balanceAfter,
                @reasonCode
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("inboxId", inboxId);
        command.Parameters.AddWithValue(
            "accountId",
            envelope.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        command.Parameters.AddWithValue(
            "walletRevision",
            walletRevision);
        command.Parameters.AddWithValue("delta", delta);
        command.Parameters.AddWithValue(
            "balanceBefore",
            character.Gold);
        command.Parameters.AddWithValue(
            "balanceAfter",
            balanceAfter);
        command.Parameters.AddWithValue(
            "reasonCode",
            ZodiacSkillGridActivationPersistenceCodec.LedgerReasonCode);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Zodiac Gold ledger append was not exact.");
        }
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        string aggregateKey,
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
            ZodiacSkillGridActivationPersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            ZodiacSkillGridActivationPersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "aggregateVersion",
            ZodiacSkillGridActivationPersistenceCodec.AggregateRevision);
        command.Parameters.AddWithValue(
            "eventType",
            ZodiacSkillGridActivationPersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "contractVersion",
            ZodiacSkillGridActivationPersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            ZodiacSkillGridActivationPersistenceCodec.OrderingPolicy);
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
                "The Zodiac activation outbox insert was not exact.");
        }
    }
}
