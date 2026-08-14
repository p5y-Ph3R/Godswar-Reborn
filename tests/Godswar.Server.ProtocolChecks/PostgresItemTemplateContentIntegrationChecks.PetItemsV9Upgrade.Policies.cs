using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private static async Task CopyV9FixturePoliciesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sourceRevision,
        string targetRevision)
    {
        await using var command = new NpgsqlCommand("""
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
        await command.ExecuteNonQueryAsync();
    }
}
