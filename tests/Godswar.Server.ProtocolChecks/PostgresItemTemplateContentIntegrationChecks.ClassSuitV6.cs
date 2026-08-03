using Godswar.Server.Application.Items;
using Godswar.Server.Infrastructure.Items;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private static readonly int[] ClassSuitV6ItemIds =
        [3931, 3962, 14069, 14073];

    private static async Task AssertClassSuitV6PublicationAsync(
        NpgsqlDataSource dataSource,
        string revision)
    {
        var loaded = await PostgresItemTemplateCatalogLoader.LoadAsync(
            dataSource);
        Check.True(
            loaded.Revision.ManifestVersion == 7 &&
            loaded.Revision.Sha256 == revision,
            "runtime pins the immutable Class Suit and elemental manifest-v7 release");
        Check.True(
            loaded.TryGet(3931, out var tierOne) &&
            tierOne.DisplayName == "Promotional Insignia I" &&
            tierOne.Icon == "792,288" &&
            loaded.TryGet(3962, out var tierTwo) &&
            tierTwo.DisplayName == "Promotional Insignia II" &&
            tierTwo.Icon == "756,288" &&
            loaded.TryGet(14069, out var tierThree) &&
            tierThree.DisplayName == "Promotional Insignia III" &&
            tierThree.Icon == "720,288" &&
            loaded.TryGet(14073, out var tierFour) &&
            tierFour.DisplayName == "Promotional Insignia IV" &&
            tierFour.Icon == "828,288",
            "manifest-v6 pins all four reviewed Promotional Insignias");

        await using var command = dataSource.CreateCommand("""
            SELECT count(*)::integer,
                   count(*) FILTER (
                       WHERE definition.kind = 'consume item'
                         AND definition.texture =
                             './Localization/en_us/UI/Texture/Icon.gwo'
                         AND definition.stats->>'ID' =
                             definition.id::text
                         AND definition.stats->>'Type' = 'consume item'
                         AND definition.stats->>'Overlap' = '99'
                         AND definition.stats->>'Random' = '0'
                         AND definition.stats->>'Distribution' = '0,0'
                   )::integer,
                   count(mutable.id)::integer,
                   count(*) FILTER (
                       WHERE mutable.id IS NOT NULL
                         AND ROW(
                             mutable.kind, mutable.name_key,
                             mutable.display_name, mutable.equipment_slot,
                             mutable.class_ids, mutable.min_level,
                             mutable.max_level, mutable.hand,
                             mutable.skill_flag, mutable.texture,
                             mutable.icon, mutable.stats
                         ) IS NOT DISTINCT FROM ROW(
                             definition.kind, definition.name_key,
                             definition.display_name,
                             definition.equipment_slot,
                             definition.class_ids, definition.min_level,
                             definition.max_level, definition.hand,
                             definition.skill_flag, definition.texture,
                             definition.icon, definition.stats
                         )
                   )::integer
            FROM item_template_content_definitions definition
            LEFT JOIN item_templates mutable ON mutable.id = definition.id
            WHERE definition.revision = @revision
              AND definition.id = ANY(@itemIds);
            """);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.Add(new NpgsqlParameter(
            "itemIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = ClassSuitV6ItemIds
        });
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt32(0) == ClassSuitV6ItemIds.Length &&
            reader.GetInt32(1) == ClassSuitV6ItemIds.Length &&
            reader.GetInt32(2) == ClassSuitV6ItemIds.Length &&
            reader.GetInt32(3) == ClassSuitV6ItemIds.Length,
            "official and mutable Class Suit identities match manifest v7");
    }

    private static async Task AssertV5ToV6ClassSuitUpgradeAsync(
        NpgsqlDataSource dataSource,
        string originalV6Revision)
    {
        var original = await PostgresItemTemplateCatalogLoader.LoadAsync(
            dataSource);
        var v5Definitions = original.All
            .Where(definition => !ClassSuitV6ItemIds.Contains(
                checked((int)definition.Id)))
            .ToArray();
        var v5Revision = ItemTemplateContentRevisionHasher.Compute(
            v5Definitions,
            original.Attributes,
            original.EquipmentRanks,
            original.HolySuitEffects,
            original.Materials.ForgingMaterials,
            original.Materials.GearEnhancementMaterials,
            original.Materials.AttributeDusts,
            original.Materials.GearMentorRecipes,
            original.HolySuit.Tiers,
            original.HolySuit.Upgrades,
            original.HolySuit.Consumables,
            original.HolySuit.OperationPolicy ??
                throw new InvalidOperationException(
                    "The v6 fixture is missing Holy Suit policy."));

        try
        {
            await CreateAndPublishV5FixtureAsync(
                dataSource,
                originalV6Revision,
                v5Revision,
                v5Definitions.Length);
            var before = await ReadCompleteRevisionFingerprintAsync(
                dataSource,
                v5Revision);

            var upgraded = await PostgresItemTemplateBaselinePublisher
                .EnsurePublishedAsync(dataSource);
            Check.True(
                upgraded.Revision == originalV6Revision &&
                !upgraded.Created,
                "startup advances a sealed manifest-v5 pointer to retained v6");
            await AssertClassSuitV6PublicationAsync(
                dataSource,
                originalV6Revision);
            Check.Equal(
                before,
                await ReadCompleteRevisionFingerprintAsync(
                    dataSource,
                    v5Revision),
                "v5-to-v6 upgrade leaves the sealed v5 release immutable");
            await AssertRetainedV5ShapeAsync(dataSource, v5Revision);

            var repeated = await PostgresItemTemplateBaselinePublisher
                .EnsurePublishedAsync(dataSource);
            Check.True(
                repeated.Revision == originalV6Revision &&
                !repeated.Created,
                "v5-to-v6 Class Suit publication upgrade is idempotent");
        }
        finally
        {
            await using var restore = dataSource.CreateCommand("""
                UPDATE item_template_content_publication
                SET revision = @revision, published_at = now()
                WHERE family = 'items';
                """);
            restore.Parameters.AddWithValue(
                "revision",
                originalV6Revision);
            await restore.ExecuteNonQueryAsync();
        }
    }

    private static async Task CreateAndPublishV5FixtureAsync(
        NpgsqlDataSource dataSource,
        string sourceRevision,
        string targetRevision,
        int entryCount)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using (var create = new NpgsqlCommand("""
            INSERT INTO item_template_content_revisions (
                revision, entry_count, source, manifest_version,
                attribute_count, equipment_rank_count,
                holy_suit_effect_count, material_policy_count,
                material_recipe_count, holy_suit_tier_count,
                holy_suit_upgrade_count, holy_suit_consumable_count,
                holy_suit_policy_count)
            SELECT @targetRevision, @entryCount,
                   'historical-v5-class-suit-upgrade-test', 5,
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
            create.Parameters.AddWithValue("sourceRevision", sourceRevision);
            create.Parameters.AddWithValue("entryCount", entryCount);
            if (await create.ExecuteScalarAsync() is not null)
            {
                await CopyV5ContentAsync(
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
                "sealed manifest-v5 fixture becomes the official pointer");
        }
        await transaction.CommitAsync();
    }

    private static async Task CopyV5ContentAsync(
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
              AND id <> ALL(@classSuitItemIds)
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

            INSERT INTO holy_suit_tier_content_definitions
            SELECT @targetRevision, suit_type, display_name, max_level,
                   ware_item_id, source
            FROM holy_suit_tier_content_definitions
            WHERE revision = @sourceRevision ORDER BY suit_type;

            INSERT INTO holy_suit_consumable_content_definitions
            SELECT @targetRevision, item_id, role, suit_type,
                   experience_capacity, stack_cap, granted_bound, source
            FROM holy_suit_consumable_content_definitions
            WHERE revision = @sourceRevision ORDER BY item_id;

            INSERT INTO holy_suit_upgrade_content_definitions
            SELECT @targetRevision, current_suit_type, current_level,
                   target_suit_type, target_level,
                   required_item_experience, ware_item_id, ware_quantity,
                   required_prisms, source
            FROM holy_suit_upgrade_content_definitions
            WHERE revision = @sourceRevision
            ORDER BY current_suit_type, current_level;

            INSERT INTO holy_suit_operation_policy_content_definitions (
                revision, policy_key, minimum_player_level,
                minimum_gear_level, daily_experience_per_player_level,
                per_operation_experience_maximum,
                gear_experience_capacity, experience_prism_cost,
                realm_day_time_zone, daily_quota_bypass_entitlement,
                source, daily_experience_per_player)
            SELECT @targetRevision, policy_key, minimum_player_level,
                   minimum_gear_level, daily_experience_per_player_level,
                   per_operation_experience_maximum,
                   gear_experience_capacity, experience_prism_cost,
                   realm_day_time_zone, daily_quota_bypass_entitlement,
                   source, daily_experience_per_player
            FROM holy_suit_operation_policy_content_definitions
            WHERE revision = @sourceRevision ORDER BY policy_key;
            """, connection, transaction);
        command.Parameters.AddWithValue("sourceRevision", sourceRevision);
        command.Parameters.AddWithValue("targetRevision", targetRevision);
        command.Parameters.Add(new NpgsqlParameter(
            "classSuitItemIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = ClassSuitV6ItemIds
        });
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertRetainedV5ShapeAsync(
        NpgsqlDataSource dataSource,
        string revision)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT release.manifest_version,
                   release.sealed_at IS NOT NULL,
                   count(definition.id)::integer
            FROM item_template_content_revisions release
            LEFT JOIN item_template_content_definitions definition
              ON definition.revision = release.revision
             AND definition.id = ANY(@itemIds)
            WHERE release.revision = @revision
            GROUP BY release.manifest_version, release.sealed_at;
            """);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.Add(new NpgsqlParameter(
            "itemIds", NpgsqlDbType.Array | NpgsqlDbType.Integer)
        {
            Value = ClassSuitV6ItemIds
        });
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync() &&
            reader.GetInt16(0) == 5 &&
            reader.GetBoolean(1) &&
            reader.GetInt32(2) == 0,
            "retained sealed v5 remains a valid Class-Suit-free rollback target");
    }
}
