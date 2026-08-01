using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresCharacterItemProjectionIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL B20F authoritative loadout projection";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} ({ConnectionStringVariable} is not set)");
            return;
        }

        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var itemCatalog = await PostgresItemTemplateContentBootstrapper
            .LoadAsync(connectionString);

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            var characterId = await CreateCharacterAsync(
                connection,
                transaction);
            var empty = await ReadDirectAsync(
                connection,
                transaction,
                characterId,
                itemCatalog.Revision.Sha256);
            AssertEmptyProjection(empty);

            await SeedSparseItemsAsync(
                connection,
                transaction,
                characterId);
            var direct = await ReadDirectAsync(
                connection,
                transaction,
                characterId,
                itemCatalog.Revision.Sha256);
            var compatibility = await ReadCompatibilityAsync(
                connection,
                transaction,
                characterId);

            Check.Equal(
                compatibility.Equipment,
                direct.Equipment,
                "direct equipment projection is byte-identical to compatibility view");
            Check.Equal(
                compatibility.KitBag,
                direct.KitBag,
                "direct kit-bag projection is byte-identical to compatibility view");

            var equipment = EquipmentSlots.GetItem(
                direct.Equipment,
                profession: 0,
                slot: 23);
            Check.Equal(1000u, equipment.Id, "last equipment slot survives projection");
            Check.Equal((short)20, equipment.Quality, "quality uses published item-template cap");
            Check.Equal((short)25, equipment.Grade, "grade uses published item-template cap");
            Check.Equal((short)5, equipment.SocketCount, "projection preserves existing socket-count semantics");
            Check.Equal(
                4030u,
                KitBagSlots.GetItem(direct.KitBag, 95).Id,
                "last kit-bag slot survives projection");
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task<int> CreateCharacterAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        await using var account = new NpgsqlCommand(
            """
            INSERT INTO accounts (username)
            VALUES (@username)
            RETURNING id;
            """,
            connection,
            transaction);
        account.Parameters.AddWithValue("username", $"b20f_{token}");
        var accountId = Convert.ToInt32(
            await account.ExecuteScalarAsync());

        await using var character = new NpgsqlCommand(
            """
            INSERT INTO character_base (
                account_id,
                server_id,
                name
            )
            VALUES (@accountId, 1, @name)
            RETURNING id;
            """,
            connection,
            transaction);
        character.Parameters.AddWithValue("accountId", accountId);
        character.Parameters.AddWithValue("name", $"B20F{token}");
        return Convert.ToInt32(await character.ExecuteScalarAsync());
    }

    private static async Task SeedSparseItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE item_templates
            SET stats = jsonb_set(
                jsonb_set(stats, '{BaseFraction}', '"1,2"'::jsonb),
                '{AppFraction}',
                '"1,2,3"'::jsonb)
            WHERE id = 1000;

            INSERT INTO character_items (
                user_id, item_location, slot_index, prop_id,
                item_quality, item_grade, bound, stack, item_exp,
                holy_suit_code, holy_socket_count,
                holy_socket1_effect_id, holy_socket1_level)
            VALUES
                (@characterId, 0, 23, 1000,
                 20, 25, 1, 2, 5, 0, 5, 7, 1),
                (@characterId, 1, 95, 4030,
                 1, 1, 1, 3, 0, 0, 0, NULL, NULL);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<ItemProjection> ReadDirectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId,
        string itemContentRevision)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT
                COALESCE(equipment_projection.equip, ''),
                COALESCE(kitbag_projection.kitbag_1, '')
            FROM character_base cb
            {PostgresCharacterItemProjectionSql.FullJoinForCharacterAlias}
            WHERE cb.id = @characterId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        command.Parameters.AddWithValue(
            "itemContentRevision",
            itemContentRevision);
        return await ReadRequiredAsync(command, "direct projection");
    }

    private static async Task<ItemProjection> ReadCompatibilityAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int characterId)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT equip, kitbag_1
            FROM character_item_loadout
            WHERE user_id = @characterId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        return await ReadRequiredAsync(command, "compatibility projection");
    }

    private static async Task<ItemProjection> ReadRequiredAsync(
        NpgsqlCommand command,
        string source)
    {
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidOperationException(
                $"B20F {source} did not return the fixture character.");
        }

        return new ItemProjection(
            reader.GetString(0),
            reader.GetString(1));
    }

    private static void AssertEmptyProjection(ItemProjection projection)
    {
        Check.Equal(
            string.Concat(Enumerable.Repeat("[]#", 24)),
            projection.Equipment,
            "empty equipment projection has exactly 24 native slots");
        Check.Equal(
            string.Concat(Enumerable.Repeat("[]#", 96)),
            projection.KitBag,
            "empty kit-bag projection has exactly 96 native slots");
    }

    private sealed record ItemProjection(
        string Equipment,
        string KitBag);
}
