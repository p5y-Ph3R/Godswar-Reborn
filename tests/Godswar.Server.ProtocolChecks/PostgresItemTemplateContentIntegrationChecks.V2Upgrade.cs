using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Items;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private static async Task AssertV2UpgradeAsync(
        NpgsqlDataSource dataSource,
        string originalRevision)
    {
        var original = await PostgresItemTemplateCatalogLoader.LoadAsync(dataSource);
        var v2Revision = ItemTemplateContentRevisionHasher.ComputeLegacyV2(
            original.All,
            original.Attributes,
            original.EquipmentRanks,
            original.HolySuitEffects);
        try
        {
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var create = new NpgsqlCommand("""
                    INSERT INTO item_template_content_revisions (
                        revision, entry_count, source, manifest_version,
                        attribute_count, equipment_rank_count,
                        holy_suit_effect_count, material_policy_count)
                    SELECT @v2Revision, entry_count,
                           'historical-v2-upgrade-test', 2,
                           attribute_count, equipment_rank_count,
                           holy_suit_effect_count, 0
                    FROM item_template_content_revisions
                    WHERE revision = @originalRevision
                    ON CONFLICT (revision) DO NOTHING
                    RETURNING 1;
                    """, connection, transaction);
                create.Parameters.AddWithValue("v2Revision", v2Revision);
                create.Parameters.AddWithValue("originalRevision", originalRevision);
                var created = await create.ExecuteScalarAsync() is not null;
                if (created)
                {
                    await CopyV2ContentAsync(
                        connection, transaction, originalRevision, v2Revision);
                }

                await using var publish = new NpgsqlCommand("""
                    UPDATE item_template_content_publication
                    SET revision = @v2Revision, published_at = now()
                    WHERE family = 'items';
                    """, connection, transaction);
                publish.Parameters.AddWithValue("v2Revision", v2Revision);
                Check.Equal(1, await publish.ExecuteNonQueryAsync(),
                    "historical v2 fixture becomes the official pointer");
                await transaction.CommitAsync();
            }

            var before = await ReadRevisionFingerprintAsync(dataSource, v2Revision);
            var upgraded = await PostgresItemTemplateBaselinePublisher
                .EnsurePublishedAsync(dataSource);
            Check.Equal(
                originalRevision,
                upgraded.Revision,
                "startup advances a valid v2 pointer to the complete v6 release");
            Check.Equal(
                before,
                await ReadRevisionFingerprintAsync(dataSource, v2Revision),
                "v2-to-v6 upgrade leaves the sealed v2 release immutable");
            var loaded = await PostgresItemTemplateCatalogLoader.LoadAsync(dataSource);
            Check.True(
                loaded.Revision.ManifestVersion == 9 &&
                loaded.Revision.MaterialPolicyCount > 0 &&
                loaded.Revision.MaterialRecipeCount > 0 &&
                loaded.Materials.DeveloperMaterials.Count ==
                    loaded.Revision.MaterialPolicyCount &&
                loaded.Materials.GearMentorRecipes.Count ==
                    loaded.Revision.MaterialRecipeCount &&
                loaded.HolySuit.Upgrades.Count == 70,
                "runtime pins the Holy-Suit-complete v6 publication after v2 upgrade");
            var repeated = await PostgresItemTemplateBaselinePublisher
                .EnsurePublishedAsync(dataSource);
            Check.True(
                repeated.Revision == originalRevision && !repeated.Created,
                "v2-to-v6 publication upgrade is idempotent");
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

    private static async Task CopyV2ContentAsync(
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
            WHERE revision = @sourceRevision ORDER BY id;

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
            """, connection, transaction);
        command.Parameters.AddWithValue("sourceRevision", sourceRevision);
        command.Parameters.AddWithValue("targetRevision", targetRevision);
        await command.ExecuteNonQueryAsync();
    }
}
