using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Items;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private static async Task AssertV4UpgradeAsync(
        NpgsqlDataSource dataSource,
        string originalRevision)
    {
        var original = await PostgresItemTemplateCatalogLoader.LoadAsync(
            dataSource);
        var v4Definitions = original.All
            .Where(static value => value.Id is < 9010 or > 9025 ||
                                   value.Id is > 9016 and < 9020)
            .ToArray();
        var v4Revision = ItemTemplateContentRevisionHasher.Compute(
            v4Definitions,
            original.Attributes,
            original.EquipmentRanks,
            original.HolySuitEffects,
            original.Materials.ForgingMaterials,
            original.Materials.GearEnhancementMaterials,
            original.Materials.AttributeDusts,
            original.Materials.GearMentorRecipes);

        try
        {
            await using (var connection =
                         await dataSource.OpenConnectionAsync())
            await using (var transaction =
                         await connection.BeginTransactionAsync())
            {
                await using var create = new NpgsqlCommand("""
                    INSERT INTO item_template_content_revisions (
                        revision, entry_count, source, manifest_version,
                        attribute_count, equipment_rank_count,
                        holy_suit_effect_count, material_policy_count,
                        material_recipe_count)
                    SELECT @v4Revision, @entryCount,
                           'historical-v4-upgrade-test', 4,
                           attribute_count, equipment_rank_count,
                           holy_suit_effect_count, material_policy_count,
                           material_recipe_count
                    FROM item_template_content_revisions
                    WHERE revision = @originalRevision
                    ON CONFLICT (revision) DO NOTHING
                    RETURNING 1;
                    """, connection, transaction);
                create.Parameters.AddWithValue("v4Revision", v4Revision);
                create.Parameters.AddWithValue(
                    "originalRevision", originalRevision);
                create.Parameters.AddWithValue(
                    "entryCount", v4Definitions.Length);
                var created = await create.ExecuteScalarAsync() is not null;
                if (created)
                {
                    await CopyV4ContentAsync(
                        connection,
                        transaction,
                        originalRevision,
                        v4Revision);
                }

                await using var publish = new NpgsqlCommand("""
                    UPDATE item_template_content_publication
                    SET revision = @v4Revision, published_at = now()
                    WHERE family = 'items';
                    """, connection, transaction);
                publish.Parameters.AddWithValue("v4Revision", v4Revision);
                Check.Equal(
                    1,
                    await publish.ExecuteNonQueryAsync(),
                    "historical v4 fixture becomes the official pointer");
                await transaction.CommitAsync();
            }

            var unrelatedBefore =
                await ReadMutableTemplateFingerprintAsync(dataSource, 1000);
            await DeleteMutableHolySuitTemplatesAsync(dataSource);
            Check.Equal(
                0,
                await CountMutableHolySuitTemplatesAsync(dataSource),
                "v4 fixture reproduces missing Holy Suit FK identities");
            var before = await ReadCompleteRevisionFingerprintAsync(
                dataSource,
                v4Revision);
            var upgraded = await PostgresItemTemplateBaselinePublisher
                .EnsurePublishedAsync(dataSource);
            Check.Equal(
                originalRevision,
                upgraded.Revision,
                "startup advances a valid v4 pointer to canonical v6");
            var loaded = await PostgresItemTemplateCatalogLoader.LoadAsync(
                dataSource);
            Check.True(
                loaded.Revision.ManifestVersion == 7 &&
                loaded.HolySuit.ItemTemplates.Count == 13 &&
                loaded.HolySuit.Upgrades.Count == 70,
                "v4-to-v7 upgrade publishes and pins Holy Suit and elemental content");
            await AssertMutableHolySuitTemplatesMatchPublishedAsync(
                dataSource,
                originalRevision,
                "v4-to-v6 startup repairs missing mutable Holy Suit identities");
            Check.Equal(
                unrelatedBefore,
                await ReadMutableTemplateFingerprintAsync(dataSource, 1000),
                "v4-to-v6 compatibility repair leaves unrelated rows untouched");
            Check.Equal(
                before,
                await ReadCompleteRevisionFingerprintAsync(
                    dataSource,
                    v4Revision),
                "v4-to-v6 upgrade leaves sealed v4 content immutable");
            var repeated = await PostgresItemTemplateBaselinePublisher
                .EnsurePublishedAsync(dataSource);
            Check.True(
                repeated.Revision == originalRevision && !repeated.Created,
                "v4-to-v6 publication upgrade is idempotent");
        }
        finally
        {
            await using var restore = dataSource.CreateCommand("""
                UPDATE item_template_content_publication
                SET revision = @revision, published_at = now()
                WHERE family = 'items';
                """);
            restore.Parameters.AddWithValue("revision", originalRevision);
            await restore.ExecuteNonQueryAsync();
        }
    }

    private static async Task CopyV4ContentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sourceRevision,
        string targetRevision)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO item_template_content_definitions
            SELECT @targetRevision, id, kind, name_key, display_name,
                   equipment_slot, class_ids, min_level, max_level,
                   hand, skill_flag, texture, icon, stats
            FROM item_template_content_definitions
            WHERE revision = @sourceRevision
              AND NOT (id BETWEEN 9010 AND 9016 OR id BETWEEN 9020 AND 9025)
            ORDER BY id;

            INSERT INTO item_attribute_content_definitions
            SELECT @targetRevision, id, name_key, stat_type, distribution,
                   percent, max_level, level_values, stats
            FROM item_attribute_content_definitions
            WHERE revision = @sourceRevision ORDER BY id;

            INSERT INTO equipment_rank_content_definitions
            SELECT @targetRevision, rank_kind, rank_level, required_score,
                   aura_effect, source
            FROM equipment_rank_content_definitions
            WHERE revision = @sourceRevision ORDER BY rank_kind, rank_level;

            INSERT INTO holy_suit_effect_content_definitions
            SELECT @targetRevision, effect_key, stat_type, unlock_points,
                   effect_value, source
            FROM holy_suit_effect_content_definitions
            WHERE revision = @sourceRevision ORDER BY effect_key;

            INSERT INTO item_material_content_definitions (
                revision, item_id, policy_kind, stack_cap, random_value,
                distribution, granted_bound, material, material_level,
                is_piece, attribute_name, attribute_ids, can_enhance,
                source_attribute_level, target_attribute_level,
                target_item_id, recipe_quantity, recipe_kind,
                source_quantity, target_quantity)
            SELECT @targetRevision, item_id, policy_kind, stack_cap,
                   random_value, distribution, granted_bound, material,
                   material_level, is_piece, attribute_name, attribute_ids,
                   can_enhance, source_attribute_level,
                   target_attribute_level, target_item_id, recipe_quantity,
                   recipe_kind, source_quantity, target_quantity
            FROM item_material_content_definitions
            WHERE revision = @sourceRevision ORDER BY item_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("sourceRevision", sourceRevision);
        command.Parameters.AddWithValue("targetRevision", targetRevision);
        await command.ExecuteNonQueryAsync();
    }
}
