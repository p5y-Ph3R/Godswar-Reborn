using System.Globalization;
using Godswar.Server.Infrastructure.Inventory;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresDeveloperBagClearCommandIntegrationChecks
{
    private const int BagItemId = 4230;
    private const int EquipmentItemId = 1000;

    private static async Task<ClearFixture> CreateFixtureAsync(
        string connectionString,
        string scenario,
        IReadOnlyList<short> bagSlots)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        var shortScenario = scenario[..Math.Min(6, scenario.Length)];
        var username = $"b09_clear_{shortScenario}_{token}";
        var characterName = $"B9C{shortScenario}{token}";

        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        int accountId;
        await using (var account = new NpgsqlCommand(
            """
            INSERT INTO public.accounts (username, password)
            VALUES (@username, '')
            RETURNING id;
            """,
            connection,
            transaction))
        {
            account.Parameters.AddWithValue("username", username);
            accountId = Convert.ToInt32(
                await account.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The clear fixture account has no identity."));
        }

        int characterId;
        await using (var character = new NpgsqlCommand(
            """
            INSERT INTO public.character_base (
                account_id,
                name,
                camp,
                profession,
                fighter_job_lv,
                "Money",
                "Stone",
                inventory_revision
            )
            VALUES (
                @accountId,
                @name,
                1,
                0,
                80,
                1000,
                100,
                0
            )
            RETURNING id;
            """,
            connection,
            transaction))
        {
            character.Parameters.AddWithValue("accountId", accountId);
            character.Parameters.AddWithValue("name", characterName);
            characterId = Convert.ToInt32(
                await character.ExecuteScalarAsync() ??
                throw new InvalidDataException(
                    "The clear fixture character has no identity."));
        }

        await InsertFixtureItemsAsync(
            connection,
            transaction,
            characterId,
            bagSlots);
        await InsertEconomyBaselineAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await PlayerOwnershipTestFences.InstallAsync(
            connection,
            transaction,
            accountId,
            characterId);
        await transaction.CommitAsync();

        return new ClearFixture(
            accountId,
            characterId,
            username,
            characterName);
    }

    private static async Task InsertFixtureItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        IReadOnlyList<short> bagSlots)
    {
        await using (var equipment = new NpgsqlCommand(
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
                item_exp,
                holy_suit_code
            )
            VALUES (
                @characterId,
                0,
                10,
                @itemId,
                1,
                1,
                1,
                1,
                0,
                0
            );
            """,
            connection,
            transaction))
        {
            equipment.Parameters.AddWithValue(
                "characterId",
                characterId);
            equipment.Parameters.AddWithValue(
                "itemId",
                EquipmentItemId);
            Check.Equal(
                1,
                await equipment.ExecuteNonQueryAsync(),
                "clear fixture inserts one equipped item");
        }

        foreach (var slot in bagSlots)
        {
            await using var item = new NpgsqlCommand(
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
                    item_exp,
                    holy_suit_code
                )
                VALUES (
                    @characterId,
                    1,
                    @slotIndex,
                    @itemId,
                    1,
                    1,
                    0,
                    1,
                    0,
                    0
                );
                """,
                connection,
                transaction);
            item.Parameters.AddWithValue("characterId", characterId);
            item.Parameters.AddWithValue("slotIndex", slot);
            item.Parameters.AddWithValue("itemId", BagItemId);
            Check.Equal(
                1,
                await item.ExecuteNonQueryAsync(),
                "clear fixture inserts one bag item");
        }
    }

    private static async Task InsertEconomyBaselineAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int accountId,
        int characterId)
    {
        await using (var baseline = new NpgsqlCommand(
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
                'test_b09_clear'
            FROM public.character_base character_row
            LEFT JOIN public.character_items item_row
              ON item_row.user_id = character_row.id
            WHERE character_row.account_id = @accountId
              AND character_row.id = @characterId
            GROUP BY character_row.id;
            """,
            connection,
            transaction))
        {
            baseline.Parameters.AddWithValue("accountId", accountId);
            baseline.Parameters.AddWithValue(
                "characterId",
                characterId);
            Check.Equal(
                1,
                await baseline.ExecuteNonQueryAsync(),
                "clear fixture inserts one economy baseline");
        }

        await using var snapshots = new NpgsqlCommand(
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
            WHERE item_row.user_id = @characterId;
            """,
            connection,
            transaction);
        snapshots.Parameters.AddWithValue("accountId", accountId);
        snapshots.Parameters.AddWithValue(
            "characterId",
            characterId);
        _ = await snapshots.ExecuteNonQueryAsync();
    }

    private static async Task<long> InsertLateBagItemAsync(
        string connectionString,
        int characterId)
    {
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
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
                item_exp,
                holy_suit_code
            )
            VALUES (
                @characterId,
                1,
                4,
                @itemId,
                1,
                1,
                0,
                1,
                0,
                0
            )
            RETURNING id;
            """,
            connection);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue("itemId", BagItemId);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync() ??
            throw new InvalidDataException(
                "The late bag item has no identity."));
    }

    private sealed record ClearFixture(
        int AccountId,
        int CharacterId,
        string Username,
        string CharacterName);
}
