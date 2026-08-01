using Godswar.Server.Application.World;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static partial class PostgresGameplayContentPublisher
{
    internal static async Task CopyDefinitionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        GameplayContentCatalog content,
        CancellationToken cancellationToken)
    {
        await CopyAsync(
            connection,
            transaction,
            revision,
            """
            INSERT INTO gameplay_map_definitions (
                revision, map_id, scene_key, display_name, client_scene_id
            )
            SELECT @revision, map_id, scene_key, display_name, client_scene_id
            FROM map_templates
            ORDER BY map_id;
            """,
            content.Maps.Count,
            "maps",
            cancellationToken);
        await CopyAsync(
            connection,
            transaction,
            revision,
            """
            INSERT INTO gameplay_map_address_points (
                revision, map_id, group_index, point_index, group_name, name,
                pos_x, pos_z, source
            )
            SELECT @revision, map_id, group_index, point_index, group_name,
                   name, pos_x, pos_z, source
            FROM map_address_points
            ORDER BY map_id, group_index, point_index;
            """,
            content.AddressPoints.Count,
            "address points",
            cancellationToken);
        await CopyAsync(
            connection,
            transaction,
            revision,
            """
            INSERT INTO gameplay_map_links (
                revision, map_id, link_index, target_map_id, pos_x, pos_z,
                source, confidence, activation, note
            )
            SELECT @revision, map_id, link_index, target_map_id, pos_x, pos_z,
                   source, confidence, activation, note
            FROM map_links
            ORDER BY map_id, link_index, target_map_id;
            """,
            content.Links.Count,
            "map links",
            cancellationToken);
        await CopyAsync(
            connection,
            transaction,
            revision,
            """
            INSERT INTO gameplay_monster_templates (
                revision, source_key, source_kind, source_map_id, scene_key,
                template_key, display_name, rank, is_boss, is_elite, is_pet,
                collision_range
            )
            SELECT @revision, source_key, source_kind, source_map_id, scene_key,
                   template_key, display_name, rank, is_boss, is_elite, is_pet,
                   collision_range
            FROM monster_templates
            ORDER BY source_key, template_key;
            """,
            content.MonsterTemplates.Count,
            "monster templates",
            cancellationToken);
        await CopyAsync(
            connection,
            transaction,
            revision,
            """
            INSERT INTO gameplay_world_boss_definitions (
                revision, map_id, scene_key, template_key, display_name,
                bonus_basis_points, respawn_interval_seconds
            )
            SELECT @revision, area.map_id, map.scene_key,
                   area.boss_template_key, area.boss_display_name,
                   area.bonus_basis_points, area.respawn_interval_seconds
            FROM world_boss_areas area
            JOIN map_templates map ON map.map_id = area.map_id
            WHERE area.enabled
            ORDER BY area.map_id;
            """,
            content.WorldBosses.Count,
            "world bosses",
            cancellationToken);
        await CopyAsync(
            connection,
            transaction,
            revision,
            """
            INSERT INTO gameplay_pending_world_boss_areas (
                revision, map_id, scene_key, reason
            )
            SELECT @revision, map_id, scene_key, reason
            FROM pending_world_boss_areas
            ORDER BY map_id;
            """,
            content.PendingWorldBossAreas.Count,
            "pending world-boss areas",
            cancellationToken);
        await CopyAsync(
            connection,
            transaction,
            revision,
            """
            INSERT INTO gameplay_skill_combat_definitions (
                revision, skill_id, target, affect_obj, distance,
                effect_range, property, mp, power1, power2,
                cast_time_seconds, cooldown_seconds, display_name, base_name,
                skill_level, class_ids, previous_skill_id, min_level,
                max_level, description, stats
            )
            SELECT @revision, skill_id, target, affect_obj, distance,
                   effect_range, property, mp, power1, power2,
                   intonate_time, cooling_time, display_name, base_name,
                   skill_level, class_ids, previous_skill_id, min_level,
                   max_level, description, stats
            FROM skill_templates
            ORDER BY skill_id;
            """,
            content.SkillCombatDefinitions.Count,
            "skill combat definitions",
            cancellationToken);
        await CopyProgressionDefinitionsAsync(
            connection,
            transaction,
            revision,
            content,
            cancellationToken);
    }

    private static async Task CopyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        string sql,
        int expectedCount,
        string description,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            revision);
        var copied = await command.ExecuteNonQueryAsync(cancellationToken);
        if (copied != expectedCount)
        {
            throw new InvalidDataException(
                $"Gameplay publication copied {copied} {description}; " +
                $"expected {expectedCount}.");
        }
    }
}
