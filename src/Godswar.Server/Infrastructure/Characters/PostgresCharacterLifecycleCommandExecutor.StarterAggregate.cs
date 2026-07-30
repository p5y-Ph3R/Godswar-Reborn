using Npgsql;

namespace Godswar.Server.Infrastructure.Characters;

internal sealed partial class PostgresCharacterLifecycleCommandExecutor
{
    private async Task SeedStarterItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        byte profession,
        CancellationToken cancellationToken)
    {
        var weaponId = profession switch
        {
            0 => 1000,
            1 => 1400,
            2 => 1700,
            3 => 1800,
            _ => throw new ArgumentOutOfRangeException(
                nameof(profession))
        };
        var hasShield = profession is 0 or 2;
        await using var command = CreateCommand(
            """
            INSERT INTO public.character_items (
                user_id,
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                bound,
                stack,
                item_exp
            )
            SELECT
                @characterId,
                starter.item_location,
                starter.slot_index,
                starter.prop_id,
                starter.item_quality,
                starter.item_grade,
                starter.bound,
                starter.stack,
                0
            FROM (
                VALUES
                    (0::smallint, 3::smallint, 2100, 1::smallint,
                        1::smallint, 1::smallint, 1::smallint),
                    (0::smallint, 6::smallint, 2900, 1::smallint,
                        1::smallint, 1::smallint, 1::smallint),
                    (0::smallint, 10::smallint, @weaponId, 1::smallint,
                        1::smallint, 1::smallint, 1::smallint),
                    (0::smallint, 13::smallint, 8040, 1::smallint,
                        1::smallint, 1::smallint, 1::smallint),
                    (1::smallint, 0::smallint, 4000, 0::smallint,
                        10::smallint, 1::smallint, 1::smallint),
                    (1::smallint, 1::smallint, 4030, 0::smallint,
                        10::smallint, 1::smallint, 1::smallint)
            ) AS starter(
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                bound,
                stack
            );

            INSERT INTO public.character_items (
                user_id,
                item_location,
                slot_index,
                prop_id,
                item_quality,
                item_grade,
                bound,
                stack,
                item_exp
            )
            SELECT
                @characterId,
                0,
                11,
                2000,
                1,
                1,
                1,
                1,
                0
            WHERE @hasShield;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("weaponId", weaponId);
        command.Parameters.AddWithValue("hasShield", hasShield);
        var expected = hasShield ? 7 : 6;
        if (await command.ExecuteNonQueryAsync(cancellationToken) !=
            expected)
        {
            throw new InvalidDataException(
                "The starter inventory was not inserted exactly.");
        }
    }

    private async Task SeedCreationEconomyBaselineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        int accountId,
        CancellationToken cancellationToken)
    {
        await using var baseline = CreateCommand(
            """
            INSERT INTO public.character_economy_baseline (
                character_id,
                account_id,
                wallet_revision,
                inventory_revision,
                silver,
                gold,
                item_count,
                baseline_source
            )
            SELECT
                character_row.id,
                character_row.account_id,
                character_row.wallet_revision,
                character_row.inventory_revision,
                character_row."Money"::bigint,
                character_row."Stone"::bigint,
                count(item_row.id)::integer,
                'character_creation'
            FROM public.character_base character_row
            LEFT JOIN public.character_items item_row
              ON item_row.user_id = character_row.id
            WHERE character_row.id = @characterId
              AND character_row.account_id = @accountId
              AND character_row.wallet_revision = 0
              AND character_row.inventory_revision = 0
            GROUP BY
                character_row.id,
                character_row.account_id,
                character_row.wallet_revision,
                character_row.inventory_revision,
                character_row."Money",
                character_row."Stone"
            RETURNING item_count;
            """,
            connection,
            transaction);
        baseline.Parameters.AddWithValue("characterId", characterId);
        baseline.Parameters.AddWithValue("accountId", accountId);
        var itemCount = Convert.ToInt32(
            await baseline.ExecuteScalarAsync(cancellationToken) ??
            throw new InvalidDataException(
                "The creation economy baseline was not inserted."));

        await using var items = CreateCommand(
            """
            INSERT INTO public.character_inventory_baseline_items (
                character_id,
                account_id,
                item_instance_id,
                item_location,
                slot_index,
                prop_id,
                state_contract_version,
                item_state
            )
            SELECT
                item_row.user_id,
                @accountId,
                item_row.id,
                item_row.item_location,
                item_row.slot_index,
                item_row.prop_id,
                1,
                to_jsonb(item_row)
            FROM public.character_items item_row
            WHERE item_row.user_id = @characterId
            ORDER BY item_row.id;
            """,
            connection,
            transaction);
        items.Parameters.AddWithValue("characterId", characterId);
        items.Parameters.AddWithValue("accountId", accountId);
        if (await items.ExecuteNonQueryAsync(cancellationToken) != itemCount)
        {
            throw new InvalidDataException(
                "The starter inventory baseline was not exact.");
        }
    }
}
