using Godswar.Server.Application.World;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static partial class PostgresGameplayContentPublisher
{
    private static async Task CopyChampionAuthoritySuccessorAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string predecessorRevision,
        string revision,
        GameplayContentCatalog content,
        CancellationToken cancellationToken)
    {
        var copies = new (string Description, int Count, string Sql)[]
        {
            ("maps", content.Maps.Count,
                """
                INSERT INTO gameplay_map_definitions (
                    revision, map_id, scene_key, display_name,
                    client_scene_id, map_mode
                )
                SELECT @revision, map_id, scene_key, display_name,
                       client_scene_id, map_mode
                FROM gameplay_map_definitions
                WHERE revision = @predecessor_revision
                ORDER BY map_id;
                """),
            ("address points", content.AddressPoints.Count,
                """
                INSERT INTO gameplay_map_address_points (
                    revision, map_id, group_index, point_index, group_name,
                    name, pos_x, pos_z, source
                )
                SELECT @revision, map_id, group_index, point_index,
                       group_name, name, pos_x, pos_z, source
                FROM gameplay_map_address_points
                WHERE revision = @predecessor_revision
                ORDER BY map_id, group_index, point_index;
                """),
            ("map links", content.Links.Count,
                """
                INSERT INTO gameplay_map_links (
                    revision, map_id, link_index, target_map_id, pos_x,
                    pos_z, source, confidence, activation, note
                )
                SELECT @revision, map_id, link_index, target_map_id, pos_x,
                       pos_z, source, confidence, activation, note
                FROM gameplay_map_links
                WHERE revision = @predecessor_revision
                ORDER BY map_id, link_index, target_map_id;
                """),
            ("monster templates", content.MonsterTemplates.Count,
                """
                INSERT INTO gameplay_monster_templates (
                    revision, source_key, source_kind, source_map_id,
                    scene_key, template_key, display_name, rank, is_boss,
                    is_elite, is_pet, attack_type, collision_range
                )
                SELECT @revision, source_key, source_kind, source_map_id,
                       scene_key, template_key, display_name, rank, is_boss,
                       is_elite, is_pet, attack_type, collision_range
                FROM gameplay_monster_templates
                WHERE revision = @predecessor_revision
                ORDER BY source_key, template_key;
                """),
            ("world bosses", content.WorldBosses.Count,
                """
                INSERT INTO gameplay_world_boss_definitions (
                    revision, map_id, scene_key, template_key, display_name,
                    bonus_basis_points, respawn_interval_seconds
                )
                SELECT @revision, map_id, scene_key, template_key,
                       display_name, bonus_basis_points,
                       respawn_interval_seconds
                FROM gameplay_world_boss_definitions
                WHERE revision = @predecessor_revision
                ORDER BY map_id;
                """),
            ("pending world bosses", content.PendingWorldBossAreas.Count,
                """
                INSERT INTO gameplay_pending_world_boss_areas (
                    revision, map_id, scene_key, reason
                )
                SELECT @revision, map_id, scene_key, reason
                FROM gameplay_pending_world_boss_areas
                WHERE revision = @predecessor_revision
                ORDER BY map_id;
                """),
            ("skills", content.SkillCombatDefinitions.Count,
                """
                INSERT INTO gameplay_skill_combat_definitions (
                    revision, skill_id, target, affect_obj, distance,
                    effect_range, property, mp, power1, power2,
                    cast_time_seconds, cooldown_seconds, display_name,
                    base_name, skill_level, class_ids, previous_skill_id,
                    min_level, max_level, description, stats
                )
                SELECT @revision, skill_id, target, affect_obj, distance,
                       effect_range, property, mp, power1, power2,
                       cast_time_seconds, cooldown_seconds, display_name,
                       base_name, skill_level, class_ids, previous_skill_id,
                       min_level, max_level, description, stats
                FROM gameplay_skill_combat_definitions
                WHERE revision = @predecessor_revision
                ORDER BY skill_id;
                """),
            ("classes", content.Classes.Count,
                """
                INSERT INTO gameplay_class_definitions (
                    revision, id, name, display_name, source
                )
                SELECT @revision, id, name, display_name, source
                FROM gameplay_class_definitions
                WHERE revision = @predecessor_revision
                ORDER BY id;
                """),
            ("talent effects", content.TalentEffects.Count,
                """
                INSERT INTO gameplay_talent_effect_definitions (
                    revision, id, key, display_name, percent
                )
                SELECT @revision, id, key, display_name, percent
                FROM gameplay_talent_effect_definitions
                WHERE revision = @predecessor_revision
                ORDER BY id;
                """),
            ("skill books", content.SkillBooks.Count,
                """
                INSERT INTO gameplay_skill_book_definitions (
                    revision, item_id, name_key, display_name, skill_id,
                    base_name, skill_level, class_ids, min_level, max_level,
                    previous_skill_id, stats
                )
                SELECT @revision, item_id, name_key, display_name, skill_id,
                       base_name, skill_level, class_ids, min_level,
                       max_level, previous_skill_id, stats
                FROM gameplay_skill_book_definitions
                WHERE revision = @predecessor_revision
                ORDER BY item_id;
                """)
        };

        foreach (var copy in copies)
        {
            await CopyChampionPredecessorRowsAsync(
                connection,
                transaction,
                predecessorRevision,
                revision,
                copy.Sql,
                copy.Count,
                copy.Description,
                cancellationToken);
        }

        await CopyCorrectedChampionTalentsAsync(
            connection,
            transaction,
            predecessorRevision,
            revision,
            content.Talents.Count,
            cancellationToken);
    }

    private static async Task CopyChampionPredecessorRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string predecessorRevision,
        string revision,
        string sql,
        int expectedCount,
        string description,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            revision);
        command.Parameters.AddWithValue(
            "predecessor_revision",
            NpgsqlDbType.Varchar,
            predecessorRevision);
        var copied = await command.ExecuteNonQueryAsync(cancellationToken);
        if (copied != expectedCount)
        {
            throw ChampionUpgradeUnavailable(
                $"Champion authority copied {copied} {description}; " +
                $"expected {expectedCount}.");
        }
    }

    private static async Task CopyCorrectedChampionTalentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string predecessorRevision,
        string revision,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            WITH correction(id, effect_value, effect_text) AS (
                SELECT *
                FROM unnest(
                    @talent_ids::integer[],
                    @effect_values::numeric[],
                    @effect_texts::text[])
            )
            INSERT INTO gameplay_talent_definitions (
                revision, id, class_id, tree_order, name, prefix_id,
                required_prefix_rank, required_total_rank, equip_request,
                effect_type, effect_id, effect_value, is_percent,
                icon_x, icon_y, icon_width, icon_height, stats
            )
            SELECT @revision, source.id, source.class_id, source.tree_order,
                   source.name, source.prefix_id,
                   source.required_prefix_rank, source.required_total_rank,
                   source.equip_request, source.effect_type, source.effect_id,
                   COALESCE(correction.effect_value, source.effect_value),
                   source.is_percent, source.icon_x, source.icon_y,
                   source.icon_width, source.icon_height,
                   CASE WHEN correction.id IS NULL THEN source.stats
                        ELSE jsonb_set(
                            source.stats,
                            ARRAY[source.effect_type],
                            to_jsonb((source.effect_id::text || ',' ||
                                correction.effect_text)::text),
                            false)
                   END
            FROM gameplay_talent_definitions source
            LEFT JOIN correction
              ON source.class_id = 1
             AND correction.id = source.id
            WHERE source.revision = @predecessor_revision
            ORDER BY source.id;
            """,
            connection,
            transaction);
        AddChampionCorrectionParameters(command);
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            revision);
        command.Parameters.AddWithValue(
            "predecessor_revision",
            NpgsqlDbType.Varchar,
            predecessorRevision);
        var copied = await command.ExecuteNonQueryAsync(cancellationToken);
        if (copied != expectedCount)
        {
            throw ChampionUpgradeUnavailable(
                $"Champion authority copied {copied} talents; " +
                $"expected {expectedCount}.");
        }
    }

}
