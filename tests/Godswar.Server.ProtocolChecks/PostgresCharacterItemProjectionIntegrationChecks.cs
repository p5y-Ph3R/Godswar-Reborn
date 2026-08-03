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
            await AssertHistoricalStateNormalizationAsync(
                connection,
                transaction);
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
            var compatibilityEquipment = EquipmentSlots.GetItem(
                compatibility.Equipment,
                profession: 0,
                slot: 23);
            Check.Equal(2133u, equipment.Id, "last equipment slot survives projection");
            Check.Equal((short)20, equipment.Quality, "quality uses published item-template cap");
            Check.Equal((short)25, equipment.Grade, "grade uses published item-template cap");
            Check.True(
                new int?[]
                {
                    equipment.Attribute1,
                    equipment.Attribute2,
                    equipment.Attribute3,
                    equipment.Attribute4,
                    equipment.Attribute5
                }.SequenceEqual(new int?[] { 10, 40, 60, 80, 130 }),
                "all five ordinary attribute IDs retain native compact positions");
            Check.True(
                new short?[]
                {
                    equipment.AttributeLevel1,
                    equipment.AttributeLevel2,
                    equipment.AttributeLevel3,
                    equipment.AttributeLevel4,
                    equipment.AttributeLevel5
                }.SequenceEqual(new short?[] { 3, 7, 11, 15, 19 }),
                "all five ordinary attribute levels remain paired");
            Check.Equal((short)6, equipment.SocketCount, "projection preserves existing socket-count semantics");
            Check.Equal(
                (short)10,
                equipment.Socket6Level ?? -1,
                "sixth socket level retains native compact field 30");
            Check.Equal(
                200,
                equipment.ClassAttribute1 ?? -1,
                "first Class Suit attribute is parsed after socket six");
            Check.Equal(
                480,
                equipment.ElementalAttribute1 ?? -1,
                "first elemental attribute follows the optional class attribute");
            Check.Equal(
                483,
                equipment.ElementalAttribute2 ?? -1,
                "second elemental attribute retains its dedicated position");
            Check.True(
                equipment.ClassAttribute2 is null,
                "deprecated second Class Suit attribute remains empty");
            Check.Equal(
                equipment,
                compatibilityEquipment,
                "direct and compatibility compact entries parse to identical item state");
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
            INSERT INTO character_items (
                user_id, item_location, slot_index, prop_id,
                attribute1, attribute2, attribute3, attribute4, attribute5,
                attribute_level1, attribute_level2, attribute_level3,
                attribute_level4, attribute_level5,
                item_quality, item_grade, bound, stack, item_exp,
                holy_suit_code,
                class_attribute1, class_attribute2,
                elemental_attribute1, elemental_attribute2,
                holy_socket_count,
                holy_socket1_effect_id, holy_socket1_level,
                holy_socket6_effect_id, holy_socket6_level)
            VALUES
                (@characterId, 0, 23, 2133,
                 10, 40, 60, 80, 130,
                 3, 7, 11, 15, 19,
                 20, 25, 1, 2, 5,
                 0, 200, NULL, 480, 483, 6, 7, 1, 20, 10),
                (@characterId, 1, 95, 4030,
                 NULL, NULL, NULL, NULL, NULL,
                 NULL, NULL, NULL, NULL, NULL,
                 1, 1, 1, 3, 0,
                 0, NULL, NULL, NULL, NULL,
                 0, NULL, NULL, NULL, NULL);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("characterId", characterId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertHistoricalStateNormalizationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH shapes AS (
                SELECT
                    jsonb_build_object(
                        'id', 1,
                        'attribute1', 10,
                        'attribute2', 200,
                        'attribute3', 40,
                        'attribute4', 210,
                        'attribute5', 60,
                        'attribute_level1', 3,
                        'attribute_level2', 1,
                        'attribute_level3', 7,
                        'attribute_level4', 1,
                        'attribute_level5', 11
                    ) AS historical_state,
                    jsonb_build_object(
                        'id', 1,
                        'attribute1', 10,
                        'attribute2', 40,
                        'attribute3', 60,
                        'attribute4', NULL,
                        'attribute5', NULL,
                        'attribute_level1', 3,
                        'attribute_level2', 7,
                        'attribute_level3', 11,
                        'attribute_level4', NULL,
                        'attribute_level5', NULL,
                        'class_attribute1', 200,
                        'class_attribute2', 210
                    ) AS current_state
            )
            SELECT
                public.canonical_character_item_state_v2(
                    historical_state
                ) =
                public.canonical_character_item_state_v2(
                    current_state
                )
            FROM shapes;
            """,
            connection,
            transaction);
        Check.True(
            Convert.ToBoolean(await command.ExecuteScalarAsync()),
            "schema-aware reconciliation normalizes historical Class Suit JSON");
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
