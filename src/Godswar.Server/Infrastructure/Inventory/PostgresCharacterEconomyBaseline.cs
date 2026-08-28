using Npgsql;

namespace Godswar.Server.Infrastructure.Inventory;

internal static class PostgresCharacterEconomyBaseline
{
    public static async Task<bool> EnsureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        if (await ExistsAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds,
                cancellationToken))
        {
            return true;
        }

        // Older binaries can create characters without an opening baseline.
        // Briefly fence legacy item DML while capturing that initial state.
        await using (var lockItems = CreateCommand(
            """
            LOCK TABLE public.character_items
            IN SHARE MODE;
            """,
            connection,
            transaction,
            commandTimeoutSeconds))
        {
            await lockItems.ExecuteNonQueryAsync(cancellationToken);
        }

        if (await ExistsAsync(
                connection,
                transaction,
                accountId,
                characterId,
                commandTimeoutSeconds,
                cancellationToken))
        {
            return true;
        }

        await using (var command = CreateCommand(
            """
            WITH inserted_baseline AS (
                INSERT INTO public.character_economy_baseline (
                    character_id,
                    account_id,
                    wallet_revision,
                    inventory_revision,
                    silver,
                    gold,
                    binding_gold,
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
                    character_row."BindingGold"::bigint,
                    (
                        SELECT count(*)::integer
                        FROM public.character_items item_row
                        WHERE item_row.user_id = character_row.id
                    ),
                    'runtime_cutover'
                FROM public.character_base character_row
                WHERE character_row.id = @characterId
                  AND character_row.account_id = @accountId
                  AND character_row.wallet_revision = 0
                  AND character_row.inventory_revision = 0
                ON CONFLICT (character_id) DO NOTHING
                RETURNING character_id, account_id
            )
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
                inserted_baseline.account_id,
                item_row.id,
                item_row.item_location,
                item_row.slot_index,
                item_row.prop_id,
                1,
                to_jsonb(item_row)
            FROM inserted_baseline
            JOIN public.character_items item_row
              ON item_row.user_id =
                 inserted_baseline.character_id
            ORDER BY item_row.id;
            """,
            connection,
            transaction,
            commandTimeoutSeconds))
        {
            command.Parameters.AddWithValue("characterId", characterId);
            command.Parameters.AddWithValue("accountId", accountId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return await ExistsAsync(
            connection,
            transaction,
            accountId,
            characterId,
            commandTimeoutSeconds,
            cancellationToken);
    }

    private static async Task<bool> ExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM public.character_economy_baseline
                WHERE character_id = @characterId
                  AND account_id = @accountId
                  AND wallet_revision = 0
                  AND inventory_revision = 0
            );
            """,
            connection,
            transaction,
            commandTimeoutSeconds);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("accountId", accountId);
        return await command.ExecuteScalarAsync(cancellationToken)
            is true;
    }

    private static NpgsqlCommand CreateCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds) =>
        new(sql, connection, transaction)
        {
            CommandTimeout = commandTimeoutSeconds
        };
}
