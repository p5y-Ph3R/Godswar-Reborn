using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Items;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private static async Task<ItemTemplateDefinition[]>
        BuildOfficialElementalTombstonesAsync(NpgsqlDataSource dataSource)
    {
        var currentIds = ElementalAttributeCatalog.Stones
            .Select(static stone => stone.ItemId)
            .ToHashSet();
        var seeds = PostgresItemTemplateBaselinePublisher
            .BuildOfficialV8ElementalMaterials()
            .Where(material => !currentIds.Contains(material.ItemId))
            .Select(static material => material.ToItemTemplateSeed())
            .OrderBy(static item => item.Id)
            .ToArray();
        Check.Equal(14, seeds.Length, "reviewed elemental tombstone count");

        await using var command = dataSource.CreateCommand("""
            SELECT input.item_id, input.stats::jsonb::text
            FROM unnest(@itemIds, @stats) AS input(item_id, stats)
            ORDER BY input.item_id;
            """);
        command.Parameters.Add(new NpgsqlParameter(
            "itemIds",
            NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = seeds.Select(static item => item.Id).ToArray()
        });
        command.Parameters.Add(new NpgsqlParameter(
            "stats",
            NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = seeds.Select(static item => item.StatsJson).ToArray()
        });
        var canonical = new Dictionary<int, string>(seeds.Length);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            canonical.Add(reader.GetInt32(0), reader.GetString(1));
        }
        return seeds.Select(seed => new ItemTemplateDefinition(
                checked((uint)seed.Id),
                seed.Kind,
                seed.NameKey,
                seed.DisplayName,
                seed.EquipmentSlot,
                seed.ClassIds,
                seed.MinLevel,
                seed.MaxLevel,
                seed.Hand,
                seed.SkillFlag,
                seed.Texture,
                seed.Icon,
                canonical[seed.Id]))
            .ToArray();
    }

    private static async Task CreateAndPublishV9FixtureAsync(
        NpgsqlDataSource dataSource,
        string sourceRevision,
        string targetRevision,
        string source,
        int entryCount,
        int[] excludedItemIds,
        ItemTemplateDefinition[] additions)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var create = new NpgsqlCommand("""
            INSERT INTO item_template_content_revisions (
                revision, entry_count, source, manifest_version,
                attribute_count, equipment_rank_count,
                holy_suit_effect_count, material_policy_count,
                material_recipe_count, holy_suit_tier_count,
                holy_suit_upgrade_count, holy_suit_consumable_count,
                holy_suit_policy_count)
            SELECT @targetRevision, @entryCount, @source, 9,
                   attribute_count, equipment_rank_count,
                   holy_suit_effect_count, material_policy_count,
                   material_recipe_count, holy_suit_tier_count,
                   holy_suit_upgrade_count, holy_suit_consumable_count,
                   holy_suit_policy_count
            FROM item_template_content_revisions
            WHERE revision = @sourceRevision
            ON CONFLICT (revision) DO NOTHING
            RETURNING 1;
            """, connection, transaction))
        {
            create.Parameters.AddWithValue("targetRevision", targetRevision);
            create.Parameters.AddWithValue("entryCount", entryCount);
            create.Parameters.AddWithValue("source", source);
            create.Parameters.AddWithValue("sourceRevision", sourceRevision);
            if (await create.ExecuteScalarAsync() is not null)
            {
                await CopyV9FixtureDefinitionsAsync(
                    connection,
                    transaction,
                    sourceRevision,
                    targetRevision,
                    excludedItemIds,
                    additions);
                await CopyV9FixturePoliciesAsync(
                    connection,
                    transaction,
                    sourceRevision,
                    targetRevision);
            }
        }

        await using (var publish = new NpgsqlCommand("""
            UPDATE item_template_content_publication
            SET revision = @targetRevision, published_at = now()
            WHERE family = 'items';
            """, connection, transaction))
        {
            publish.Parameters.AddWithValue("targetRevision", targetRevision);
            Check.Equal(
                1,
                await publish.ExecuteNonQueryAsync(),
                "manifest-v9 fixture becomes the official pointer");
        }
        await transaction.CommitAsync();
    }

    private static async Task CopyV9FixtureDefinitionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sourceRevision,
        string targetRevision,
        int[] excludedItemIds,
        ItemTemplateDefinition[] additions)
    {
        await using (var copy = new NpgsqlCommand("""
            INSERT INTO item_template_content_definitions
            SELECT @targetRevision, id, kind, name_key, display_name,
                   equipment_slot, class_ids, min_level, max_level,
                   hand, skill_flag, texture, icon, stats
            FROM item_template_content_definitions
            WHERE revision = @sourceRevision
              AND id <> ALL(@excludedItemIds)
            ORDER BY id;
            """, connection, transaction))
        {
            copy.Parameters.AddWithValue("targetRevision", targetRevision);
            copy.Parameters.AddWithValue("sourceRevision", sourceRevision);
            copy.Parameters.Add(new NpgsqlParameter(
                "excludedItemIds",
                NpgsqlDbType.Array | NpgsqlDbType.Integer)
            {
                Value = excludedItemIds
            });
            await copy.ExecuteNonQueryAsync();
        }
        if (additions.Length == 0)
        {
            return;
        }

        await using var add = new NpgsqlCommand("""
            INSERT INTO item_template_content_definitions (
                revision, id, kind, name_key, display_name,
                equipment_slot, class_ids, min_level, max_level,
                hand, skill_flag, texture, icon, stats)
            SELECT @targetRevision, input.id, input.kind, input.name_key,
                   input.display_name, 0, '{}'::smallint[], NULL, NULL,
                   NULL, NULL, input.texture, input.icon, input.stats::jsonb
            FROM unnest(
                @ids, @kinds, @nameKeys, @displayNames,
                @textures, @icons, @stats)
              AS input(id, kind, name_key, display_name,
                       texture, icon, stats)
            ORDER BY input.id;
            """, connection, transaction);
        add.Parameters.AddWithValue("targetRevision", targetRevision);
        AddArray(add, "ids", NpgsqlDbType.Integer,
            additions.Select(static item => checked((int)item.Id)).ToArray());
        AddArray(add, "kinds", NpgsqlDbType.Text,
            additions.Select(static item => item.Kind).ToArray());
        AddArray(add, "nameKeys", NpgsqlDbType.Text,
            additions.Select(static item => item.NameKey).ToArray());
        AddArray(add, "displayNames", NpgsqlDbType.Text,
            additions.Select(static item => item.DisplayName).ToArray());
        AddArray(add, "textures", NpgsqlDbType.Text,
            additions.Select(static item => item.Texture).ToArray());
        AddArray(add, "icons", NpgsqlDbType.Text,
            additions.Select(static item => item.Icon).ToArray());
        AddArray(add, "stats", NpgsqlDbType.Text,
            additions.Select(static item => item.StatsJson).ToArray());
        await add.ExecuteNonQueryAsync();
    }

    private static void AddArray<T>(
        NpgsqlCommand command,
        string name,
        NpgsqlDbType elementType,
        T[] values) =>
        command.Parameters.Add(new NpgsqlParameter(
            name,
            NpgsqlDbType.Array | elementType)
        {
            Value = values
        });

    private static async Task RestoreItemPublicationAsync(
        NpgsqlDataSource dataSource,
        string revision)
    {
        await using var restore = dataSource.CreateCommand("""
            UPDATE item_template_content_publication
            SET revision = @revision, published_at = now()
            WHERE family = 'items';
            """);
        restore.Parameters.AddWithValue("revision", revision);
        await restore.ExecuteNonQueryAsync();
    }
}
