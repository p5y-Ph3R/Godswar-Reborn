using System.Text.Json;
using Godswar.Server.Application.Items;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private static async Task AssertSocketSpellPublicationAsync(
        NpgsqlDataSource dataSource,
        PinnedItemTemplateCatalog catalog)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT definition.id, definition.kind, definition.name_key,
                   definition.display_name, definition.equipment_slot,
                   definition.class_ids, definition.min_level,
                   definition.max_level, definition.hand,
                   definition.skill_flag, definition.texture,
                   definition.icon, definition.stats::text,
                   mutable.kind, mutable.name_key, mutable.display_name,
                   mutable.texture, mutable.icon, mutable.stats::text
            FROM item_template_content_publication publication
            JOIN item_template_content_definitions definition
              ON definition.revision = publication.revision
            JOIN item_templates mutable ON mutable.id = definition.id
            WHERE publication.family = 'items'
              AND definition.id BETWEEN 4270 AND 4273
            ORDER BY definition.id;
            """, connection);
        var rowCount = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var itemId = reader.GetInt32(0);
            var ordinal = itemId - 4269;
            var expectedName = ordinal switch
            {
                1 => "Socket Spell I",
                2 => "Socket Spell II",
                3 => "Socket Spell III",
                4 => "Socket Spell IV",
                _ => throw new InvalidDataException(
                    $"Unexpected Socket Spell item {itemId}.")
            };
            Check.True(
                reader.GetString(1) == "consume item" &&
                reader.GetString(2) == $"Smithing{itemId}" &&
                reader.GetString(3) == expectedName &&
                reader.GetInt16(4) == 0 &&
                reader.GetFieldValue<short[]>(5).Length == 0 &&
                reader.IsDBNull(6) &&
                reader.IsDBNull(7) &&
                reader.IsDBNull(8) &&
                reader.IsDBNull(9) &&
                reader.GetString(10) ==
                    "./Localization/en_us/UI/Texture/Icon.gwo" &&
                reader.GetString(11) == "108,900",
                $"published Socket Spell {ordinal} keeps stock metadata");
            AssertSocketSpellStats(reader.GetString(12), itemId);
            Check.True(
                reader.GetString(13) == "consume item" &&
                reader.GetString(14) == $"Smithing{itemId}" &&
                reader.GetString(15) == expectedName &&
                reader.GetString(16) == reader.GetString(10) &&
                reader.GetString(17) == reader.GetString(11),
                $"mutable FK projection agrees for Socket Spell {ordinal}");
            AssertSocketSpellStats(reader.GetString(18), itemId);
            rowCount++;
        }

        Check.Equal(4, rowCount, "published Socket Spell database row count");

        var developerItems = new GameplayItemContent(catalog).DeveloperItems;
        for (var ordinal = 1; ordinal <= 4; ordinal++)
        {
            var itemId = checked((uint)(4269 + ordinal));
            Check.True(
                catalog.TryGet(itemId, out _) &&
                developerItems.TryResolveDeveloper(
                    $"socketspell{ordinal}",
                    out var grant) &&
                grant.ItemId == itemId &&
                grant.StackCap == 99 &&
                grant.GrantedBound == 0,
                $"runtime pins Socket Spell {ordinal} as an unbound grant");
        }
    }

    private static void AssertSocketSpellStats(string statsJson, int itemId)
    {
        using var document = JsonDocument.Parse(statsJson);
        var stats = document.RootElement;
        Check.True(
            stats.GetProperty("ID").GetString() == itemId.ToString() &&
            stats.GetProperty("Type").GetString() == "consume item" &&
            stats.GetProperty("Texture").GetString() ==
                "./Localization/en_us/UI/Texture/Icon.gwo" &&
            stats.GetProperty("Icon").GetString() == "108,900" &&
            stats.GetProperty("Random").GetString() == "0" &&
            stats.GetProperty("Distribution").GetString() == "0,0" &&
            stats.GetProperty("Money").GetString() == "0" &&
            stats.GetProperty("Overlap").GetString() == "99" &&
            !stats.TryGetProperty("BindType", out _),
            $"Socket Spell {itemId} database stats remain stock and unbound");
    }
}
