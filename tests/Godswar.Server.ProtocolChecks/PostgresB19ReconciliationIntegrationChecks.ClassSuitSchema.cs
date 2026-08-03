using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresB19ReconciliationIntegrationChecks
{
    private static async Task ConvertInventoryBaselineToLegacyItemShapeAsync(
        NpgsqlDataSource dataSource,
        EconomyFixture fixture)
    {
        await ExecuteDisposableCorruptionAsync(
            dataSource,
            async (connection, transaction) =>
            {
                await using var command = new NpgsqlCommand(
                    """
                    UPDATE public.character_inventory_baseline_items
                    SET item_state =
                        item_state - 'class_attribute1' - 'class_attribute2'
                    WHERE character_id = @character_id
                      AND item_instance_id = @item_id;
                    """,
                    connection,
                    transaction);
                command.Parameters.AddWithValue(
                    "character_id",
                    fixture.CharacterId);
                command.Parameters.AddWithValue(
                    "item_id",
                    fixture.ItemId);
                Check.Equal(
                    1,
                    await command.ExecuteNonQueryAsync(),
                    "one baseline item is reshaped as pre-migration JSON");
            });
    }

    private static async Task AssertCrossVersionInventoryChainFixtureAsync(
        NpgsqlDataSource dataSource,
        EconomyFixture fixture)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                NOT (baseline.item_state ? 'class_attribute1')
                    AND NOT (baseline.item_state ? 'class_attribute2'),
                ledger.before_state ? 'class_attribute1'
                    AND ledger.before_state ? 'class_attribute2',
                public.canonical_character_item_state_v2(
                    ledger.before_state
                ) = public.canonical_character_item_state_v2(
                    baseline.item_state
                )
            FROM public.character_inventory_baseline_items baseline
            JOIN public.character_inventory_ledger ledger
              ON ledger.character_id = baseline.character_id
             AND ledger.item_instance_id = baseline.item_instance_id
             AND ledger.inventory_revision = 1
            WHERE baseline.character_id = @character_id
              AND baseline.item_instance_id = @item_id;
            """);
        command.Parameters.AddWithValue(
            "character_id",
            fixture.CharacterId);
        command.Parameters.AddWithValue("item_id", fixture.ItemId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "cross-version inventory-chain fixture exists");
        Check.True(
            reader.GetBoolean(0),
            "inventory baseline retains its pre-migration JSON shape");
        Check.True(
            reader.GetBoolean(1),
            "first ledger link retains the post-migration JSON shape");
        Check.True(
            reader.GetBoolean(2),
            "old baseline and new before-state are canonically equivalent");
    }

    private static async Task InsertInventoryLedgerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long inboxId,
        EconomyFixture fixture,
        long revision,
        int beforeIncrement,
        int afterIncrement)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.character_inventory_ledger (
                command_inbox_id,
                account_id,
                character_id,
                inventory_revision,
                entry_ordinal,
                item_instance_id,
                mutation_kind,
                state_contract_version,
                before_state,
                after_state,
                reason_code
            )
            SELECT
                @inbox_id,
                @account_id,
                @character_id,
                @revision,
                0,
                baseline.item_instance_id,
                'update',
                1,
                jsonb_set(
                    baseline.item_state,
                    '{item_exp}',
                    to_jsonb(@before_item_exp::integer),
                    false) || jsonb_build_object(
                        'class_attribute1', NULL,
                        'class_attribute2', NULL),
                jsonb_set(
                    baseline.item_state,
                    '{item_exp}',
                    to_jsonb(@after_item_exp::integer),
                    false) || jsonb_build_object(
                        'class_attribute1', NULL,
                        'class_attribute2', NULL),
                'b19.chain.test'
            FROM public.character_inventory_baseline_items baseline
            WHERE baseline.character_id = @character_id
              AND baseline.item_instance_id = @item_id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("inbox_id", inboxId);
        command.Parameters.AddWithValue("account_id", fixture.AccountId);
        command.Parameters.AddWithValue(
            "character_id",
            fixture.CharacterId);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("item_id", fixture.ItemId);
        command.Parameters.AddWithValue(
            "before_item_exp",
            fixture.ItemExperience + beforeIncrement);
        command.Parameters.AddWithValue(
            "after_item_exp",
            fixture.ItemExperience + afterIncrement);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"inventory ledger revision {revision} is seeded");
    }
}
