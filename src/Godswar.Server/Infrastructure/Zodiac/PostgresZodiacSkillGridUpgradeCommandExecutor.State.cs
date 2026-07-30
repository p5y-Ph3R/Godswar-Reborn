using System.Text;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Zodiac;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Zodiac;

internal sealed partial class
    PostgresZodiacSkillGridUpgradeCommandExecutor
{
    private async Task<LockedCharacter?> LockCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<ZodiacSkillGridUpgradeCommand> envelope,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                zodiac_level,
                zodiac_energy,
                zodiac_energy_remainder_x100,
                "SkillPoint"
            FROM public.character_base
            WHERE account_id = @accountId
              AND id = @characterId
            FOR UPDATE;
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
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var zodiacLevel = reader.GetInt16(0);
        var energy = reader.GetInt32(1);
        var remainder = reader.GetInt32(2);
        var talentPoints = reader.GetInt32(3);
        if (zodiacLevel is < 1 or > 30 ||
            energy < 0 ||
            remainder is < 0 or > 99 ||
            talentPoints < 0)
        {
            throw new InvalidDataException(
                "The durable Zodiac resource state is invalid.");
        }

        return new LockedCharacter(
            checked((byte)zodiacLevel),
            energy,
            remainder,
            talentPoints);
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
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new StoredGrid(
                0,
                ZodiacSkillGridUpgradeCommandEnvelope.NoSelectedSkillId);
        }

        var level = reader.GetInt16(0);
        var selectedSkillId = reader.GetInt32(1);
        if (level is < 0 or >
                ZodiacSkillGridUpgradeCommandEnvelope.MaximumGridLevel ||
            selectedSkillId <
                ZodiacSkillGridUpgradeCommandEnvelope.NoSelectedSkillId)
        {
            throw new InvalidDataException(
                "The durable Zodiac grid state is invalid.");
        }

        return new StoredGrid(
            checked((byte)level),
            selectedSkillId);
    }

    private async Task UpdateResourcesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CommandEnvelope<ZodiacSkillGridUpgradeCommand> envelope,
        LockedCharacter character,
        ZodiacSkillGridUpgradeResult result,
        CancellationToken cancellationToken)
    {
        if (!result.Committed ||
            result.CurrentEnergy < 0 ||
            result.CurrentEnergyRemainderX100 is < 0 or > 99 ||
            result.CurrentTalentPoints < 0)
        {
            throw new InvalidDataException(
                "The Zodiac resource mutation is invalid.");
        }

        var beforeEnergyX100 =
            checked((long)character.Energy * 100L +
                character.EnergyRemainderX100);
        var afterEnergyX100 =
            checked((long)result.CurrentEnergy * 100L +
                result.CurrentEnergyRemainderX100);
        if (afterEnergyX100 !=
                beforeEnergyX100 -
                    checked((long)result.EnergyCost * 100L) ||
            result.CurrentTalentPoints !=
                character.TalentPoints - result.TalentPointCost)
        {
            throw new InvalidDataException(
                "The Zodiac resource delta is not exact.");
        }

        await using var command = CreateCommand(
            """
            UPDATE public.character_base
            SET zodiac_energy = @energyAfter,
                zodiac_energy_remainder_x100 = @remainderAfter,
                "SkillPoint" = @talentPointsAfter
            WHERE account_id = @accountId
              AND id = @characterId
              AND zodiac_energy = @energyBefore
              AND zodiac_energy_remainder_x100 = @remainderBefore
              AND "SkillPoint" = @talentPointsBefore;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "energyAfter",
            result.CurrentEnergy);
        command.Parameters.AddWithValue(
            "remainderAfter",
            result.CurrentEnergyRemainderX100);
        command.Parameters.AddWithValue(
            "talentPointsAfter",
            result.CurrentTalentPoints);
        command.Parameters.AddWithValue(
            "accountId",
            envelope.Subject.AccountId);
        command.Parameters.AddWithValue(
            "characterId",
            envelope.Subject.CharacterId);
        command.Parameters.AddWithValue(
            "energyBefore",
            character.Energy);
        command.Parameters.AddWithValue(
            "remainderBefore",
            character.EnergyRemainderX100);
        command.Parameters.AddWithValue(
            "talentPointsBefore",
            character.TalentPoints);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Zodiac resources did not mutate exactly once.");
        }
    }

    private async Task UpdateGridAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int gridIndex,
        ZodiacSkillGridUpgradeResult result,
        CancellationToken cancellationToken)
    {
        if (!result.Committed ||
            result.CurrentLevel != result.PreviousLevel + 1)
        {
            throw new InvalidDataException(
                "The Zodiac grid transition is invalid.");
        }

        await using var command = CreateCommand(
            """
            UPDATE public.character_zodiac_skill_grids
            SET level = @currentLevel,
                updated_at = now()
            WHERE user_id = @characterId
              AND grid_index = @gridIndex
              AND level = @previousLevel
              AND selected_skill_id = @selectedSkillId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "gridIndex",
            checked((short)gridIndex));
        command.Parameters.AddWithValue(
            "previousLevel",
            checked((short)result.PreviousLevel));
        command.Parameters.AddWithValue(
            "currentLevel",
            checked((short)result.CurrentLevel));
        command.Parameters.AddWithValue(
            "selectedSkillId",
            result.SelectedSkillId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The Zodiac grid did not advance exactly once.");
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
                @aggregateRevision,
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
            ZodiacSkillGridUpgradePersistenceCodec.ConsumerKey);
        command.Parameters.AddWithValue(
            "aggregateType",
            ZodiacSkillGridUpgradePersistenceCodec.AggregateType);
        command.Parameters.AddWithValue("aggregateKey", aggregateKey);
        command.Parameters.AddWithValue(
            "aggregateRevision",
            aggregateRevision);
        command.Parameters.AddWithValue(
            "eventType",
            ZodiacSkillGridUpgradePersistenceCodec.EventType);
        command.Parameters.AddWithValue(
            "contractVersion",
            ZodiacSkillGridUpgradePersistenceCodec.ContractVersion);
        command.Parameters.AddWithValue(
            "orderingPolicy",
            ZodiacSkillGridUpgradePersistenceCodec.OrderingPolicy);
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
                "The Zodiac upgrade outbox insert was not exact.");
        }
    }
}
