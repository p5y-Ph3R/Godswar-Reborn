using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresDatabaseCleanupIntegrationChecks
{
    private const string ConnectionStringVariable = "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const string HpMigrationId = "20260723_005_starter_consumable_templates";
    private const string KitbagMigrationId = "20260723_006_archive_legacy_character_kitbag";
    private const string ForeignKeyMigrationId = "20260723_007_character_item_template_foreign_key";

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL database-cleanup integration ({ConnectionStringVariable} is not set)");
            return;
        }

        var inventoryBefore = await ReadInventoryFingerprintIfPresentAsync(connectionString);
        var sourceKitbagBefore = await ReadPublicKitbagSnapshotIfPresentAsync(connectionString);

        await using (var store = new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
        }

        Check.True(
            !await RelationExistsAsync(connectionString, "public.character_kitbag"),
            "obsolete public character_kitbag table is retired");
        Check.True(
            await RelationExistsAsync(connectionString, "legacy.character_kitbag_archive"),
            "legacy character kitbag archive remains recoverable");

        if (sourceKitbagBefore is not null)
        {
            Check.Equal(
                sourceKitbagBefore,
                await ReadLegacyKitbagSnapshotAsync(connectionString),
                "every legacy compact kitbag row is archived byte-for-byte before retirement");
        }

        await CheckStarterConsumablesAsync(connectionString);
        await CheckMigrationHistoryAsync(connectionString);
        await CheckInventoryTemplateForeignKeyAsync(connectionString);
        if (inventoryBefore is not null)
        {
            var inventoryAfter = await ReadInventoryFingerprintIfPresentAsync(connectionString)
                ?? throw new InvalidOperationException(
                    "Authoritative inventory disappeared during database cleanup.");
            Check.Equal(
                inventoryBefore,
                inventoryAfter,
                "database cleanup does not delete or rewrite authoritative inventory rows");
        }

        var archiveBeforeSecondRun = await ReadLegacyArchiveFingerprintAsync(connectionString);
        var historyBeforeSecondRun = await ReadCleanupHistorySnapshotAsync(connectionString);
        var inventoryBeforeSecondRun = await ReadInventoryFingerprintIfPresentAsync(connectionString)
            ?? throw new InvalidOperationException(
                "Authoritative inventory is missing before the idempotence check.");

        await using (var reopenedStore = new PostgresGameStore(connectionString))
        {
            await reopenedStore.EnsureSeedDataAsync();
        }

        Check.Equal(
            archiveBeforeSecondRun,
            await ReadLegacyArchiveFingerprintAsync(connectionString),
            "re-running initialization does not rewrite the legacy archive");
        Check.Equal(
            historyBeforeSecondRun,
            await ReadCleanupHistorySnapshotAsync(connectionString),
            "re-running initialization does not replay cleanup migrations");
        var inventoryAfterSecondRun = await ReadInventoryFingerprintIfPresentAsync(connectionString)
            ?? throw new InvalidOperationException(
                "Authoritative inventory disappeared during the idempotence check.");
        Check.Equal(
            inventoryBeforeSecondRun,
            inventoryAfterSecondRun,
            "re-running cleanup initialization preserves authoritative inventory");
    }

    private static async Task CheckStarterConsumablesAsync(string connectionString)
    {
        var expected = new Dictionary<int, ConsumableExpectation>
        {
            [4000] = new(
                "HPPotion_a",
                "Small Healing Potion",
                "252,972",
                "250",
                "3100",
                "10"),
            [4030] = new(
                "MPPotion_a",
                "Small Mana Potion",
                "432,972",
                "0",
                "3120",
                "11")
        };

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
                id,
                kind,
                name_key,
                display_name,
                equipment_slot,
                cardinality(class_ids),
                min_level,
                max_level,
                hand,
                skill_flag,
                texture,
                icon,
                stats ->> 'Type',
                stats ->> 'Texture',
                stats ->> 'Icon',
                stats ->> 'Random',
                stats ->> 'Distribution',
                stats ->> 'Money',
                stats ->> 'Overlap',
                stats ->> 'Use',
                stats ->> 'Skill',
                stats ->> 'ItemType'
            FROM public.item_templates
            WHERE id IN (4000, 4030)
            ORDER BY id;
            """, connection);

        var seen = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetInt32(0);
            var item = expected[id];
            Check.Equal("consume item", reader.GetString(1), $"{id} kind");
            Check.Equal(item.NameKey, reader.GetString(2), $"{id} name key");
            Check.Equal(item.DisplayName, reader.GetString(3), $"{id} display name");
            Check.Equal((short)-1, reader.GetInt16(4), $"{id} is never equipable");
            Check.Equal(0, reader.GetInt32(5), $"{id} has no class restriction");
            for (var column = 6; column <= 9; column++)
            {
                Check.True(reader.IsDBNull(column), $"{id} nullable equipment column {column} stays null");
            }

            const string texture = "./Localization/en_us/UI/Texture/Icon.gwo";
            Check.Equal(texture, reader.GetString(10), $"{id} texture");
            Check.Equal(item.Icon, reader.GetString(11), $"{id} icon");
            Check.Equal("consume item", reader.GetString(12), $"{id} stats type");
            Check.Equal(texture, reader.GetString(13), $"{id} stats texture");
            Check.Equal(item.Icon, reader.GetString(14), $"{id} stats icon");
            Check.Equal(item.Random, reader.GetString(15), $"{id} stats random");
            Check.Equal("50,200", reader.GetString(16), $"{id} stats distribution");
            Check.Equal("5", reader.GetString(17), $"{id} stats money");
            Check.Equal("99", reader.GetString(18), $"{id} stats overlap");
            Check.Equal("1", reader.GetString(19), $"{id} stats use");
            Check.Equal(item.Skill, reader.GetString(20), $"{id} stats skill");
            Check.Equal(item.ItemType, reader.GetString(21), $"{id} stats item type");
            seen++;
        }

        Check.Equal(expected.Count, seen, "both client-derived starter consumables are present");
    }

    private static async Task CheckMigrationHistoryAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT migration_id, count(*)
            FROM public.schema_migrations
            WHERE migration_id IN (
                '20260723_005_starter_consumable_templates',
                '20260723_006_archive_legacy_character_kitbag',
                '20260723_007_character_item_template_foreign_key'
            )
            GROUP BY migration_id
            ORDER BY migration_id;
            """, connection);

        var rows = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            Check.True(
                reader.GetString(0) is
                    HpMigrationId or
                    KitbagMigrationId or
                    ForeignKeyMigrationId,
                "cleanup migration history contains only registered IDs");
            Check.Equal(1L, reader.GetInt64(1), "cleanup migration is recorded exactly once");
            rows++;
        }

        Check.Equal(3, rows, "all cleanup migrations are recorded");
    }

    private static async Task CheckInventoryTemplateForeignKeyAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*)
            FROM pg_constraint constraint_definition
            JOIN pg_attribute source_column
              ON source_column.attrelid = constraint_definition.conrelid
             AND source_column.attname = 'prop_id'
             AND constraint_definition.conkey = ARRAY[source_column.attnum]::smallint[]
            JOIN pg_attribute target_column
              ON target_column.attrelid = constraint_definition.confrelid
             AND target_column.attname = 'id'
             AND constraint_definition.confkey = ARRAY[target_column.attnum]::smallint[]
            WHERE constraint_definition.conrelid = 'public.character_items'::regclass
              AND constraint_definition.conname = 'fk_character_items_prop_id_item_templates'
              AND constraint_definition.contype = 'f'
              AND constraint_definition.confrelid = 'public.item_templates'::regclass
              AND constraint_definition.confdeltype = 'r'
              AND constraint_definition.convalidated;
            """, connection);
        Check.Equal(
            1L,
            (long)(await command.ExecuteScalarAsync()
                   ?? throw new InvalidOperationException("Foreign-key check returned null.")),
            "authoritative inventory has one validated, restrictive item-template foreign key");
    }

    private static async Task<bool> RelationExistsAsync(
        string connectionString,
        string qualifiedName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass(@qualifiedName) IS NOT NULL;",
            connection);
        command.Parameters.AddWithValue("qualifiedName", qualifiedName);
        return (bool)(await command.ExecuteScalarAsync()
                      ?? throw new InvalidOperationException("Relation check returned null."));
    }

    private static async Task<string?> ReadInventoryFingerprintIfPresentAsync(
        string connectionString)
    {
        if (!await RelationExistsAsync(connectionString, "public.character_items"))
        {
            return null;
        }

        return await ReadScalarTextAsync(connectionString, """
            SELECT
                count(*)::text || ':' ||
                md5(COALESCE(
                    string_agg(to_jsonb(item_row)::text, '' ORDER BY item_row.id),
                    ''
                ))
            FROM public.character_items AS item_row;
            """);
    }

    private static async Task<string?> ReadPublicKitbagSnapshotIfPresentAsync(
        string connectionString)
    {
        if (!await RelationExistsAsync(connectionString, "public.character_kitbag"))
        {
            return null;
        }

        return await ReadKitbagSnapshotAsync(connectionString, legacy: false);
    }

    private static Task<string> ReadLegacyKitbagSnapshotAsync(string connectionString) =>
        ReadKitbagSnapshotAsync(connectionString, legacy: true);

    private static Task<string> ReadKitbagSnapshotAsync(
        string connectionString,
        bool legacy)
    {
        var relation = legacy
            ? "legacy.character_kitbag_archive"
            : "public.character_kitbag";
        return ReadScalarTextAsync(connectionString, $"""
            SELECT COALESCE(
                jsonb_agg(
                    jsonb_build_object(
                        'user_id', user_id,
                        'kitbag_1', kitbag_1,
                        'kitbag_2', kitbag_2,
                        'kitbag_3', kitbag_3,
                        'kitbag_4', kitbag_4,
                        'storage', storage,
                        'equip', equip
                    )
                    ORDER BY user_id
                ),
                '[]'::jsonb
            )::text
            FROM {relation};
            """);
    }

    private static Task<string> ReadLegacyArchiveFingerprintAsync(string connectionString) =>
        ReadScalarTextAsync(connectionString, """
            SELECT
                count(*)::text || ':' ||
                md5(COALESCE(
                    string_agg(to_jsonb(archive_row)::text, '' ORDER BY archive_row.user_id),
                    ''
                ))
            FROM legacy.character_kitbag_archive AS archive_row;
            """);

    private static Task<string> ReadCleanupHistorySnapshotAsync(string connectionString) =>
        ReadScalarTextAsync(connectionString, """
            SELECT COALESCE(
                string_agg(
                    migration_id || ':' || checksum || ':' || execution_ms::text,
                    ','
                    ORDER BY migration_id
                ),
                ''
            )
            FROM public.schema_migrations
            WHERE migration_id IN (
                '20260723_005_starter_consumable_templates',
                '20260723_006_archive_legacy_character_kitbag',
                '20260723_007_character_item_template_foreign_key'
            );
            """);

    private static async Task<string> ReadScalarTextAsync(
        string connectionString,
        string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync()
                        ?? throw new InvalidOperationException("Database snapshot returned null."));
    }

    private sealed record ConsumableExpectation(
        string NameKey,
        string DisplayName,
        string Icon,
        string Random,
        string Skill,
        string ItemType);
}
