namespace Godswar.Server.State;

/// <summary>
/// Adds only the server-authored Zephyr reinforcement deltas. Native mount and
/// mount-gear values remain owned by the ordinary item-stat projection.
/// </summary>
internal static class PostgresMountGearPassiveProjectionSql
{
    public const string CommonTableExpressions =
        """
        valid_mount_loadouts AS (
            SELECT
                mount.user_id,
                COALESCE(mount_template.min_level, 1) AS mount_level
            FROM character_items mount
            JOIN character_base owner
              ON owner.id = mount.user_id
            JOIN item_template_content_revisions revision
              ON revision.revision = @itemContentRevision
             AND revision.sealed_at IS NOT NULL
            JOIN item_template_content_definitions mount_template
              ON mount_template.revision = revision.revision
             AND mount_template.id = mount.prop_id
             AND mount_template.kind = 'mount'
             AND mount_template.equipment_slot = 20
            WHERE mount.item_location = 0
              AND mount.slot_index = 20
              AND mount.user_id = @characterId
              AND owner.fighter_job_lv >=
                  COALESCE(mount_template.min_level, 1)
              AND (
                  mount_template.max_level IS NULL
                  OR owner.fighter_job_lv <= mount_template.max_level
              )
              AND (
                  cardinality(mount_template.class_ids) = 0
                  OR owner.profession = ANY(mount_template.class_ids)
              )
        ),
        mount_gear_spirit_candidates AS (
            SELECT
                gear.id AS item_instance_id,
                gear.user_id,
                gear.slot_index,
                gear.prop_id,
                gear.item_quality,
                gear.item_grade,
                gear.attribute1,
                gear.attribute2,
                gear.attribute3,
                gear.attribute4,
                gear.attribute5,
                gear_template.kind,
                gear_template.stats,
                socket.effect_id,
                socket.effectiveness_value,
                row_number() OVER (
                    PARTITION BY gear.id, socket.effect_id
                    ORDER BY socket.effectiveness_value DESC,
                        socket.socket_index
                ) AS host_roll_rank
            FROM character_items gear
            JOIN character_base owner
              ON owner.id = gear.user_id
            JOIN valid_mount_loadouts mount
              ON mount.user_id = gear.user_id
            JOIN item_template_content_definitions gear_template
              ON gear_template.revision = @itemContentRevision
             AND gear_template.id = gear.prop_id
             AND gear_template.equipment_slot = gear.slot_index
            CROSS JOIN LATERAL (
                VALUES
                    (1, gear.holy_socket1_effect_id,
                        gear.holy_socket1_level,
                        gear.holy_socket1_value),
                    (2, gear.holy_socket2_effect_id,
                        gear.holy_socket2_level,
                        gear.holy_socket2_value)
            ) socket(
                socket_index,
                effect_id,
                effect_level,
                effectiveness_value)
            WHERE gear.item_location = 0
              AND gear.slot_index BETWEEN 15 AND 19
              AND (
                  gear.slot_index = 15 AND gear_template.kind = 'mounthead'
                  OR gear.slot_index = 16 AND gear_template.kind = 'mountarmor'
                  OR gear.slot_index = 17 AND gear_template.kind = 'mountsoul'
                  OR gear.slot_index = 18 AND gear_template.kind = 'mountornament'
                  OR gear.slot_index = 19 AND gear_template.kind = 'mountamulet'
              )
              AND owner.fighter_job_lv >=
                  COALESCE(gear_template.min_level, 1)
              AND (
                  gear_template.max_level IS NULL
                  OR owner.fighter_job_lv <= gear_template.max_level
              )
              AND mount.mount_level >=
                  COALESCE(gear_template.min_level, 1)
              AND (
                  cardinality(gear_template.class_ids) = 0
                  OR owner.profession = ANY(gear_template.class_ids)
              )
              AND gear.holy_socket_count BETWEEN 1 AND 2
              AND socket.socket_index <= gear.holy_socket_count
              AND socket.effect_id IN (21, 22)
              AND socket.effect_level BETWEEN 1 AND 10
              AND socket.effectiveness_value BETWEEN
                  CASE socket.effect_id
                      WHEN 21 THEN 15 * socket.effect_level
                      WHEN 22 THEN 10 * socket.effect_level
                  END
                  AND
                  CASE socket.effect_id
                      WHEN 21 THEN 30 * socket.effect_level
                      WHEN 22 THEN 20 * socket.effect_level
                  END
        ),
        mount_gear_spirit_selected_hosts AS (
            SELECT selected.*
            FROM (
                SELECT candidate.*,
                    row_number() OVER (
                        PARTITION BY candidate.user_id, candidate.effect_id
                        ORDER BY candidate.effectiveness_value DESC,
                            candidate.item_instance_id
                    ) AS loadout_roll_rank
                FROM mount_gear_spirit_candidates candidate
                WHERE candidate.host_roll_rank = 1
            ) selected
            WHERE selected.loadout_roll_rank <= 2
        ),
        mount_gear_attunement_stat_values AS (
            SELECT
                host.user_id,
                native.stat_name,
                COALESCE(NULLIF(native_values.values[
                    LEAST(
                        GREATEST(host.item_quality::integer, 1),
                        array_length(native_values.values, 1))
                ], '')::numeric, 0::numeric) *
                    host.effectiveness_value / 10000::numeric AS stat_value
            FROM mount_gear_spirit_selected_hosts host
            CROSS JOIN LATERAL (
                VALUES
                    ('mounthead', 'Hit', 'hit'),
                    ('mountarmor', 'MaxHP', 'max_hp'),
                    ('mountsoul', 'InjureImbibe', 'damage_absorb'),
                    ('mountornament', 'MaxHP', 'max_hp'),
                    ('mountamulet', 'Miss', 'dodge')
            ) native(kind, source_key, stat_name)
            JOIN LATERAL (
                SELECT string_to_array(
                    host.stats->>native.source_key, ',') AS values
                WHERE host.stats ? native.source_key
            ) native_values ON true
            WHERE host.effect_id = 21
              AND host.kind = native.kind
        ),
        mount_gear_tempering_stat_values AS (
            SELECT
                host.user_id,
                stat.stat_name,
                CASE WHEN attribute_template.percent
                    THEN attribute_value.value * 10000
                    ELSE attribute_value.value
                END * host.effectiveness_value /
                    10000::numeric AS stat_value
            FROM mount_gear_spirit_selected_hosts host
            CROSS JOIN LATERAL (
                VALUES
                    (host.attribute1),
                    (host.attribute2),
                    (host.attribute3),
                    (host.attribute4),
                    (host.attribute5)
            ) attribute(attribute_id)
            JOIN item_attribute_content_definitions attribute_template
              ON attribute_template.revision = @itemContentRevision
             AND attribute_template.id = attribute.attribute_id
            JOIN (
                VALUES
                    (0, 'physical_attack'), (1, 'physical_defense'),
                    (2, 'magic_attack'), (3, 'magic_defense'),
                    (4, 'hit'), (5, 'dodge'), (6, 'critical'),
                    (7, 'critical_resistance'),
                    (8, 'physical_damage_bonus'),
                    (9, 'magic_damage_bonus'),
                    (10, 'damage_absorb'), (13, 'max_hp'),
                    (14, 'max_mp'), (15, 'hp_recovery'),
                    (16, 'mp_recovery'), (17, 'be_cure_bonus'),
                    (18, 'cure_bonus'),
                    (19, 'ignore_physical_defense'),
                    (20, 'ignore_magic_defense'),
                    (23, 'physical_append_damage'),
                    (24, 'magic_append_damage'),
                    (25, 'critical_damage_percent'),
                    (26, 'critical_damage_flat')
            ) stat(stat_type, stat_name)
              ON stat.stat_type = attribute_template.stat_type
            CROSS JOIN LATERAL (
                SELECT attribute_template.level_values[
                    LEAST(
                        GREATEST(host.item_grade::integer, 1),
                        array_length(
                            attribute_template.level_values,
                            1))
                ] AS value
            ) attribute_value
            WHERE host.effect_id = 22
              AND attribute.attribute_id IS NOT NULL
              AND attribute.attribute_id >= 0
              AND attribute_value.value IS NOT NULL
        ),
        mount_gear_spirit_stat_values AS (
            SELECT * FROM mount_gear_attunement_stat_values
            UNION ALL
            SELECT * FROM mount_gear_tempering_stat_values
        ),
        """;
}
