using System.Text.Json;
using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Items;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private static async Task AssertHolyStoneMaterialPublicationAsync(
        NpgsqlDataSource dataSource,
        PinnedItemTemplateCatalog catalog)
    {
        var expected = HolyStoneMaterialItemContentBaseline.ItemTemplates
            .ToDictionary(static value => value.Id);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT definition.id, definition.kind, definition.name_key,
                   definition.display_name, definition.equipment_slot,
                   definition.class_ids, definition.min_level,
                   definition.max_level, definition.hand,
                   definition.skill_flag, definition.texture,
                   definition.icon, definition.stats::text,
                   mutable.kind, mutable.name_key, mutable.display_name,
                   mutable.equipment_slot, mutable.class_ids,
                   mutable.texture, mutable.icon, mutable.stats::text
            FROM item_template_content_publication publication
            JOIN item_template_content_definitions definition
              ON definition.revision = publication.revision
            JOIN item_templates mutable ON mutable.id = definition.id
            WHERE publication.family = 'items'
              AND definition.id = ANY(@itemIds)
            ORDER BY definition.id;
            """, connection);
        command.Parameters.AddWithValue("itemIds", expected.Keys.ToArray());
        var rowCount = 0;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var itemId = reader.GetInt32(0);
            Check.True(
                expected.TryGetValue(itemId, out var seed),
                $"published Holy Stone material {itemId} is reviewed");
            Check.True(
                reader.GetString(1) == seed.Kind &&
                reader.GetString(2) == seed.NameKey &&
                reader.GetString(3) == seed.DisplayName &&
                reader.GetInt16(4) == seed.EquipmentSlot &&
                reader.GetFieldValue<short[]>(5).Length == 0 &&
                reader.IsDBNull(6) &&
                reader.IsDBNull(7) &&
                reader.IsDBNull(8) &&
                reader.IsDBNull(9) &&
                reader.GetString(10) == seed.Texture &&
                reader.GetString(11) == seed.Icon,
                $"published Holy Stone material {itemId} keeps metadata");
            AssertHolyStoneMaterialStats(reader.GetString(12), seed);
            Check.True(
                reader.GetString(13) == seed.Kind &&
                reader.GetString(14) == seed.NameKey &&
                reader.GetString(15) == seed.DisplayName &&
                reader.GetInt16(16) == seed.EquipmentSlot &&
                reader.GetFieldValue<short[]>(17).Length == 0 &&
                reader.GetString(18) == seed.Texture &&
                reader.GetString(19) == seed.Icon,
                $"mutable Holy Stone material {itemId} agrees with content");
            AssertHolyStoneMaterialStats(reader.GetString(20), seed);
            Check.True(
                catalog.TryGet(checked((uint)itemId), out var pinned) &&
                pinned.NameKey == seed.NameKey &&
                pinned.DisplayName == seed.DisplayName,
                $"runtime catalog pins Holy Stone material {itemId}");
            rowCount++;
        }

        Check.Equal(
            expected.Count,
            rowCount,
            "published stock Holy Stone material database row count");
    }

    private static void AssertHolyStoneMaterialStats(
        string statsJson,
        Godswar.Server.State.ItemTemplateSeed seed)
    {
        using var expectedDocument = JsonDocument.Parse(seed.StatsJson);
        using var actualDocument = JsonDocument.Parse(statsJson);
        var expected = expectedDocument.RootElement;
        var actual = actualDocument.RootElement;
        foreach (var property in expected.EnumerateObject())
        {
            Check.True(
                actual.TryGetProperty(property.Name, out var actualValue) &&
                actualValue.GetString() == property.Value.GetString(),
                $"Holy Stone material {seed.Id} keeps {property.Name}");
        }

        Check.Equal(
            expected.EnumerateObject().Count(),
            actual.EnumerateObject().Count(),
            $"Holy Stone material {seed.Id} has no invented database stats");
    }
}
