using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Items;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private static async Task AssertV3UpgradeAsync(
        NpgsqlDataSource dataSource,
        string originalRevision)
    {
        var original = await PostgresItemTemplateCatalogLoader.LoadAsync(
            dataSource);
        var recipeSourceIds = original.Materials.GearMentorRecipes
            .Select(static recipe => recipe.SourceItemId)
            .ToHashSet();
        var decoy = original.Materials.ForgingMaterials.First(material =>
            !recipeSourceIds.Contains(material.ItemId));
        var decoyStackCap = decoy.StackCap == 1
            ? (short)2
            : checked((short)(decoy.StackCap - 1));
        var v3Forging = original.Materials.ForgingMaterials
            .Select(material => material.ItemId == decoy.ItemId
                ? material with { StackCap = decoyStackCap }
                : material)
            .ToArray();
        var v3Revision = ItemTemplateContentRevisionHasher.Compute(
            original.All,
            original.Attributes,
            original.EquipmentRanks,
            original.HolySuitEffects,
            v3Forging,
            original.Materials.GearEnhancementMaterials,
            original.Materials.AttributeDusts);

        try
        {
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var create = new NpgsqlCommand("""
                    INSERT INTO item_template_content_revisions (
                        revision, entry_count, source, manifest_version,
                        attribute_count, equipment_rank_count,
                        holy_suit_effect_count, material_policy_count,
                        material_recipe_count)
                    SELECT @v3Revision, entry_count,
                           'historical-v3-upgrade-test', 3,
                           attribute_count, equipment_rank_count,
                           holy_suit_effect_count, material_policy_count, 0
                    FROM item_template_content_revisions
                    WHERE revision = @originalRevision
                    ON CONFLICT (revision) DO NOTHING
                    RETURNING 1;
                    """, connection, transaction);
                create.Parameters.AddWithValue("v3Revision", v3Revision);
                create.Parameters.AddWithValue(
                    "originalRevision",
                    originalRevision);
                var created = await create.ExecuteScalarAsync() is not null;
                if (created)
                {
                    await CopyV2ContentAsync(
                        connection,
                        transaction,
                        originalRevision,
                        v3Revision);
                    await CopyV3MaterialContentAsync(
                        connection,
                        transaction,
                        originalRevision,
                        v3Revision,
                        decoy.ItemId,
                        decoyStackCap);
                }

                await using var publish = new NpgsqlCommand("""
                    UPDATE item_template_content_publication
                    SET revision = @v3Revision, published_at = now()
                    WHERE family = 'items';
                    """, connection, transaction);
                publish.Parameters.AddWithValue("v3Revision", v3Revision);
                Check.Equal(
                    1,
                    await publish.ExecuteNonQueryAsync(),
                    "historical v3 fixture becomes the official pointer");
                await transaction.CommitAsync();
            }

            await AssertV3FixtureShapeAsync(
                dataSource,
                v3Revision,
                decoy.ItemId,
                decoyStackCap);
            var before = await ReadCompleteRevisionFingerprintAsync(
                dataSource,
                v3Revision);

            var upgraded = await PostgresItemTemplateBaselinePublisher
                .EnsurePublishedAsync(dataSource);
            Check.True(
                !upgraded.Revision.Equals(v3Revision, StringComparison.Ordinal),
                "startup advances a valid v3 pointer to a new v5 release");
            var loaded = await PostgresItemTemplateCatalogLoader.LoadAsync(
                dataSource);
            Check.True(
                loaded.Revision.Sha256 == upgraded.Revision &&
                loaded.Revision.ManifestVersion == 5 &&
                loaded.Revision.MaterialRecipeCount > 0 &&
                loaded.Materials.GearMentorRecipes.Count ==
                    loaded.Revision.MaterialRecipeCount &&
                loaded.HolySuit.Upgrades.Count == 70,
                "runtime pins the Holy-Suit-complete official v5 publication");
            Check.True(
                loaded.Materials.TryResolveForging(
                    decoy.ItemId,
                    out var upgradedDecoy) &&
                upgradedDecoy.StackCap == decoyStackCap &&
                loaded.Materials.GearMentorRecipes.All(
                    recipe => recipe.SourceItemId != decoy.ItemId),
                "v3-to-v5 upgrade preserves the non-recipe material policy");

            var repeated = await PostgresItemTemplateBaselinePublisher
                .EnsurePublishedAsync(dataSource);
            Check.True(
                repeated.Revision == upgraded.Revision && !repeated.Created,
                "v3-to-v5 publication upgrade is idempotent");
            Check.Equal(
                before,
                await ReadCompleteRevisionFingerprintAsync(
                    dataSource,
                    v3Revision),
                "v3-to-v5 upgrade leaves the sealed v3 release immutable");
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

    private static async Task CopyV3MaterialContentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sourceRevision,
        string targetRevision,
        uint decoyItemId,
        short decoyStackCap)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO item_material_content_definitions (
                revision, item_id, policy_kind, stack_cap, random_value,
                distribution, granted_bound, material, material_level,
                is_piece, attribute_name, attribute_ids, can_enhance,
                source_attribute_level, target_attribute_level,
                target_item_id, recipe_quantity, recipe_kind,
                source_quantity, target_quantity)
            SELECT @targetRevision, item_id, policy_kind,
                   CASE WHEN item_id = @decoyItemId
                        THEN @decoyStackCap ELSE stack_cap END,
                   random_value, distribution, granted_bound, material,
                   material_level, is_piece, attribute_name, attribute_ids,
                   can_enhance, source_attribute_level,
                   target_attribute_level,
                   CASE WHEN recipe_kind IS NULL
                        THEN target_item_id ELSE NULL END,
                   recipe_quantity, NULL::varchar,
                   NULL::integer, NULL::integer
            FROM item_material_content_definitions
            WHERE revision = @sourceRevision
            ORDER BY item_id;
            """, connection, transaction);
        command.Parameters.AddWithValue("sourceRevision", sourceRevision);
        command.Parameters.AddWithValue("targetRevision", targetRevision);
        command.Parameters.AddWithValue(
            "decoyItemId",
            checked((int)decoyItemId));
        command.Parameters.AddWithValue("decoyStackCap", decoyStackCap);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertV3FixtureShapeAsync(
        NpgsqlDataSource dataSource,
        string revision,
        uint decoyItemId,
        short decoyStackCap)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT release.manifest_version,
                   release.material_policy_count,
                   release.material_recipe_count,
                   release.sealed_at IS NOT NULL,
                   (
                       SELECT count(*)::integer
                       FROM item_material_content_definitions definition
                       WHERE definition.revision = release.revision
                   ),
                   (
                       SELECT count(*)::integer
                       FROM item_material_content_definitions definition
                       WHERE definition.revision = release.revision
                         AND definition.recipe_kind IS NOT NULL
                   ),
                   EXISTS (
                       SELECT 1
                       FROM item_material_content_definitions definition
                       WHERE definition.revision = release.revision
                         AND definition.item_id = @decoyItemId
                         AND definition.stack_cap = @decoyStackCap
                         AND definition.recipe_kind IS NULL
                         AND definition.target_item_id IS NULL
                         AND definition.source_quantity IS NULL
                         AND definition.target_quantity IS NULL
                   )
            FROM item_template_content_revisions release
            WHERE release.revision = @revision;
            """);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue(
            "decoyItemId",
            checked((int)decoyItemId));
        command.Parameters.AddWithValue("decoyStackCap", decoyStackCap);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "sealed manifest-v3 fixture exists");
        Check.True(
            reader.GetInt16(0) == 3 &&
            reader.GetInt32(1) > 0 &&
            reader.GetInt32(2) == 0 &&
            reader.GetBoolean(3) &&
            reader.GetInt32(4) == reader.GetInt32(1) &&
            reader.GetInt32(5) == 0 &&
            reader.GetBoolean(6),
            "manifest-v3 fixture is sealed with material policies, the decoy, and zero recipes");
    }

    private static async Task<string> ReadCompleteRevisionFingerprintAsync(
        NpgsqlDataSource dataSource,
        string revision)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT md5(
                to_jsonb(release)::text ||
                COALESCE((
                    SELECT jsonb_agg(to_jsonb(definition)
                                     ORDER BY definition.id)::text
                    FROM item_template_content_definitions definition
                    WHERE definition.revision = release.revision
                ), '[]') ||
                COALESCE((
                    SELECT jsonb_agg(to_jsonb(definition)
                                     ORDER BY definition.id)::text
                    FROM item_attribute_content_definitions definition
                    WHERE definition.revision = release.revision
                ), '[]') ||
                COALESCE((
                    SELECT jsonb_agg(to_jsonb(definition)
                                     ORDER BY definition.rank_kind,
                                              definition.rank_level)::text
                    FROM equipment_rank_content_definitions definition
                    WHERE definition.revision = release.revision
                ), '[]') ||
                COALESCE((
                    SELECT jsonb_agg(to_jsonb(definition)
                                     ORDER BY definition.effect_key)::text
                    FROM holy_suit_effect_content_definitions definition
                    WHERE definition.revision = release.revision
                ), '[]') ||
                COALESCE((
                    SELECT jsonb_agg(to_jsonb(definition)
                                     ORDER BY definition.item_id)::text
                    FROM item_material_content_definitions definition
                    WHERE definition.revision = release.revision
                ), '[]'))
            FROM item_template_content_revisions release
            WHERE release.revision = @revision;
            """);
        command.Parameters.AddWithValue("revision", revision);
        return (string)(await command.ExecuteScalarAsync() ??
            throw new InvalidOperationException(
                $"Item revision {revision} is missing."));
    }
}
