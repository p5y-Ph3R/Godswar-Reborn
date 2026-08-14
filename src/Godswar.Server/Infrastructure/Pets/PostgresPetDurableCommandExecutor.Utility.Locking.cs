using Godswar.Server.Application.Pets;
using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<LockedUtilityPet?> LockSummonedUtilityPetAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, species_id, name, sex, level, experience,
                bound, is_carried, is_summoned,
                contributes_to_character, activity_state, revision,
                growth_revealed, has_soul_contract, soul_contract_stage,
                current_energy, maximum_energy
            FROM public.character_pets
            WHERE user_id = @characterId
              AND activity_state = 'owned'
              AND is_carried
              AND is_summoned
            ORDER BY id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var pet = new LockedUtilityPet(
            reader.GetInt64(0),
            reader.GetInt16(1),
            reader.GetString(2),
            checked((byte)reader.GetInt16(3)),
            reader.GetInt16(4),
            reader.GetInt64(5),
            reader.GetBoolean(6),
            reader.GetBoolean(7),
            reader.GetBoolean(8),
            reader.GetBoolean(9),
            reader.GetString(10),
            reader.GetInt64(11),
            reader.GetBoolean(12),
            reader.GetBoolean(13),
            checked((byte)reader.GetInt16(14)),
            reader.GetInt32(15),
            reader.GetInt32(16));
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "More than one summoned Pet Manager pet is authoritative.");
        }
        return pet;
    }

    private async Task<LockedUtilityItem?> LockFirstUtilityItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int itemTemplateId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT
                id, prop_id, item_quality, bound, stack,
                to_jsonb(character_items)::text, slot_index
            FROM public.character_items
            WHERE user_id = @characterId
              AND item_location = 1
              AND prop_id = @itemTemplateId
            ORDER BY slot_index, id
            LIMIT 1
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemTemplateId", itemTemplateId);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new LockedUtilityItem(
                new LockedBagItem(
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    reader.GetInt16(2),
                    reader.GetInt16(3) != 0,
                    reader.GetInt16(4),
                    reader.GetString(5)),
                reader.GetInt16(6))
            : null;
    }

    private async Task<bool> HoldsUtilityItemAnywhereAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int itemTemplateId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT id
            FROM public.character_items
            WHERE user_id = @characterId
              AND prop_id = @itemTemplateId
            ORDER BY id
            LIMIT 1
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemTemplateId", itemTemplateId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<int?> LockFirstEmptyUtilityBagSlotAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT candidate.slot_index::integer
            FROM generate_series(0, 95) candidate(slot_index)
            WHERE NOT EXISTS (
                SELECT 1
                FROM public.character_items item
                WHERE item.user_id = @characterId
                  AND item.item_location = 1
                  AND item.slot_index = candidate.slot_index
            )
            ORDER BY candidate.slot_index
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    private sealed record LockedUtilityItem(
        LockedBagItem Item,
        int BagSlot);
}
