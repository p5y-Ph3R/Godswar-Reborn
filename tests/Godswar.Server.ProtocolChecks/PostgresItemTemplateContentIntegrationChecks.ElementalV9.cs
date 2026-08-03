using Godswar.Server.Application.Items;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private static async Task AssertElementalV9PublicationAsync(
        NpgsqlDataSource dataSource,
        PinnedItemTemplateCatalog loaded,
        string revision)
    {
        var stones = ElementalAttributeCatalog.Stones
            .OrderBy(static value => value.ItemId)
            .ToArray();
        Check.Equal(7, stones.Length, "manifest-v9 canonical stone count");
        foreach (var stone in stones)
        {
            Check.True(
                loaded.TryGet(stone.ItemId, out var template) &&
                template.DisplayName == stone.DisplayName &&
                loaded.Materials.TryGetGearEnhancement(
                    stone.ItemId,
                    out var material) &&
                material.DisplayName == stone.DisplayName &&
                material.AllowedAttributeIds.SequenceEqual(
                    stone.AttributeIds),
                $"manifest-v9 pins {stone.DisplayName} with all three gear-context attributes");
        }

        var ids = stones
            .Select(static value => checked((int)value.ItemId))
            .ToArray();
        await using var command = dataSource.CreateCommand("""
            SELECT definition.id,
                   definition.display_name,
                   policy.attribute_ids,
                   definition.icon,
                   definition.stats->>'Icon'
            FROM item_template_content_definitions definition
            JOIN item_material_content_definitions policy
              ON policy.revision = definition.revision
             AND policy.item_id = definition.id
            WHERE definition.revision = @revision
              AND definition.id = ANY(@itemIds)
            ORDER BY definition.id;

            SELECT count(*)::integer
            FROM item_material_content_definitions
            WHERE revision = @revision
              AND item_id BETWEEN 16300 AND 16320
              AND item_id <> ALL(@itemIds);

            SELECT count(*)::integer
            FROM item_templates
            WHERE id = ANY(@itemIds);

            SELECT count(*)::integer
            FROM item_template_content_definitions
            WHERE revision = @revision
              AND id BETWEEN 16300 AND 16320
              AND id <> ALL(@itemIds);

            SELECT count(*)::integer
            FROM item_template_content_revisions release
            WHERE release.manifest_version = 8
              AND (
                    (
                        SELECT count(*)
                        FROM item_template_content_definitions definition
                        WHERE definition.revision = release.revision
                          AND definition.id BETWEEN 16300 AND 16320
                    ) <> 21
                 OR (
                        SELECT count(*)
                        FROM item_material_content_definitions policy
                        WHERE policy.revision = release.revision
                          AND policy.item_id BETWEEN 16300 AND 16320
                    ) <> 21
              );
            """);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.Add(new NpgsqlParameter(
            "itemIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = ids
        });
        await using var reader = await command.ExecuteReaderAsync();
        var index = 0;
        while (await reader.ReadAsync())
        {
            var stone = stones[index++];
            Check.True(
                reader.GetInt32(0) == checked((int)stone.ItemId) &&
                reader.GetString(1) == stone.DisplayName &&
                reader.GetFieldValue<int[]>(2).SequenceEqual(
                    stone.AttributeIds) &&
                reader.GetString(3) == reader.GetString(4),
                $"PostgreSQL v9 policy and template agree for {stone.DisplayName}");
        }
        Check.Equal(7, index, "published canonical elemental row count");

        Check.True(
            await reader.NextResultAsync() &&
            await reader.ReadAsync() &&
            reader.GetInt32(0) == 0,
            "manifest-v9 removes all fourteen retired material policies");
        Check.True(
            await reader.NextResultAsync() &&
            await reader.ReadAsync() &&
            reader.GetInt32(0) == 7,
            "mutable FK projection contains every canonical stone identity");
        Check.True(
            await reader.NextResultAsync() &&
            await reader.ReadAsync() &&
            reader.GetInt32(0) is 0 or 14,
            "fresh v9 has no retired definitions while v8 upgrades retain all fourteen tombstones");
        Check.True(
            await reader.NextResultAsync() &&
            await reader.ReadAsync() &&
            reader.GetInt32(0) == 0,
            "sealed manifest-v8 elemental definitions and policies remain immutable");
    }
}
