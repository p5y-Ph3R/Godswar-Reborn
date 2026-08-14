using Npgsql;

namespace Godswar.Server.Infrastructure.Pets;

internal sealed partial class PostgresPetDurableCommandExecutor
{
    private async Task<CreatedUtilityItem> InsertUtilityItemAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bagSlot,
        int itemTemplateId,
        CancellationToken cancellationToken,
        bool isBound = false)
    {
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack
            )
            VALUES (
                @characterId, 1, @bagSlot, @itemTemplateId,
                1, 1, @bound, 1
            )
            RETURNING id, to_jsonb(character_items)::text;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("bagSlot", checked((short)bagSlot));
        command.Parameters.AddWithValue("itemTemplateId", itemTemplateId);
        command.Parameters.AddWithValue(
            "bound",
            checked((short)(isBound ? 1 : 0)));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The Pet Manager item was not created exactly once.");
        }
        return new(reader.GetInt64(0), reader.GetString(1));
    }

    private async Task<CreatedUtilityItem> ReplaceEmptySealWithPackedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        LockedUtilityItem emptySeal,
        bool isBound,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            UPDATE public.character_items
            SET prop_id = 10109,
                item_quality = 1,
                item_grade = 1,
                bound = @bound,
                stack = 1,
                updated_at = transaction_timestamp()
            WHERE id = @itemId
              AND user_id = @characterId
              AND item_location = 1
              AND slot_index = @bagSlot
              AND prop_id = 10108
              AND stack = 1
            RETURNING id, to_jsonb(character_items)::text;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("itemId", emptySeal.Item.ItemId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "bound",
            checked((short)(isBound ? 1 : 0)));
        command.Parameters.AddWithValue(
            "bagSlot",
            checked((short)emptySeal.BagSlot));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidDataException(
                "The empty Seal Jade was not packed exactly once.");
        }
        return new(reader.GetInt64(0), reader.GetString(1));
    }

    private async Task DeletePackedSealAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int bagSlot,
        long itemId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            DELETE FROM public.character_items
            WHERE id = @itemId
              AND user_id = @characterId
              AND item_location = 1
              AND slot_index = @bagSlot
              AND prop_id = 10109
              AND stack = 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("itemId", itemId);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("bagSlot", checked((short)bagSlot));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The packed Seal Jade was not consumed exactly once.");
        }
    }

    private sealed record CreatedUtilityItem(
        long ItemId,
        string AfterState);
}
