namespace Godswar.Server.State;

/// <summary>
/// Projects socketed Holy Spirit values once, retaining the legacy aggregate
/// channel while exposing the cooled-spirit combat effects with their actual
/// units.
/// </summary>
internal static class PostgresCharacterHolySpiritCombatProjectionSql
{
    public static readonly string CommonTableExpressions =
        $$"""
        holy_spirit_effect_authority(
            effect_id,
            affinity,
            grade_one_minimum,
            grade_one_maximum,
            grade_one_accepted_maximum
        ) AS (
            VALUES
                (1, 1, 32, 80, 80), (2, 1, 32, 80, 80),
                (3, 1, 20, 50, 50), (4, 1, 24, 60, 60),
                (5, 1, 16, 40, 40), (6, 1, 12, 30, 30),
                (7, 1, 24, 60, 60), (8, 1, 40, 100, 100),
                (11, 2, 16, 40, 40), (12, 2, 14, 35, 35),
                (14, 2, 40, 100, 100),
                (19, 2, 16, 40, 40), (20, 2, 16, 40, 40)
            UNION ALL
            SELECT adjustable.effect_id,
                   2,
                   adjustable.grade_one_minimum,
                   adjustable.grade_one_maximum,
                   adjustable.grade_one_accepted_maximum
            FROM (
                VALUES
                    (9, 22,
                        @cooledPhysicalReductionGradeOneMaximum,
                        80),
                    (10, 22,
                        @cooledMagicReductionGradeOneMaximum,
                        80),
                    (13, 28,
                        @cooledCriticalReductionGradeOneMaximum,
                        70)
            ) adjustable(
                effect_id,
                grade_one_minimum,
                grade_one_maximum,
                grade_one_accepted_maximum)
        ),
        holy_stone_socket_raw_values AS (
            SELECT
                equipment.user_id,
                socket.effect_id,
                effect.grade_one_maximum,
                effect.grade_one_accepted_maximum,
                socket_level.safe_level,
                COALESCE(
                    socket.effectiveness_value::numeric,
                    CASE
                    WHEN socket.effect_id IN (1,2,3,4) THEN
                        (ARRAY[110,170,240,320,410,500,650,850,1100,1400]::numeric[])[socket_level.safe_level]
                    WHEN socket.effect_id IN (5,6) THEN
                        (ARRAY[120,190,280,380,500,620,850,1200,1650,2200]::numeric[])[socket_level.safe_level]
                    WHEN socket.effect_id = 8 THEN
                        (ARRAY[150,240,340,460,590,720,950,1300,1800,2400]::numeric[])[socket_level.safe_level]
                    WHEN socket.effect_id IN (11,12,14,16,18,20) THEN
                        (ARRAY[60,90,130,170,210,250,350,500,700,950]::numeric[])[socket_level.safe_level]
                    ELSE
                        (ARRAY[80,120,170,230,300,370,500,700,950,1200]::numeric[])[socket_level.safe_level]
                    END) AS stat_value
            FROM character_items equipment
            JOIN character_base owner
              ON owner.id = equipment.user_id
            JOIN item_template_content_revisions revision
              ON revision.revision = @itemContentRevision
             AND revision.sealed_at IS NOT NULL
            JOIN item_template_content_definitions template
              ON template.revision = revision.revision
             AND template.id = equipment.prop_id
             AND (
                 template.equipment_slot = equipment.slot_index
                 OR template.kind = 'ring'
                    AND equipment.slot_index IN (8, 9)
             )
            CROSS JOIN LATERAL (
                VALUES
                    (0, equipment.holy_socket1_effect_id, equipment.holy_socket1_level, equipment.holy_socket1_value),
                    (1, equipment.holy_socket2_effect_id, equipment.holy_socket2_level, equipment.holy_socket2_value),
                    (2, equipment.holy_socket3_effect_id, equipment.holy_socket3_level, equipment.holy_socket3_value),
                    (3, equipment.holy_socket4_effect_id, equipment.holy_socket4_level, equipment.holy_socket4_value)
            ) socket(socket_index, effect_id, effect_level, effectiveness_value)
            JOIN holy_spirit_effect_authority effect
              ON effect.effect_id = socket.effect_id
             AND (
                 effect.affinity = 1
                 AND equipment.slot_index IN (0, 2, 8, 9, 10)
                 OR effect.affinity = 2
                 AND equipment.slot_index IN (1, 3, 4, 5, 6, 7, 11)
             )
            CROSS JOIN LATERAL (
                SELECT LEAST(
                    GREATEST(COALESCE(socket.effect_level, 1)::integer, 1),
                    10) AS safe_level
            ) socket_level
            WHERE equipment.item_location = 0
              AND equipment.user_id = @characterId
              AND socket.socket_index < equipment.holy_socket_count
              AND socket.effect_level BETWEEN 1 AND 10
              AND (
                  socket.effectiveness_value IS NULL
                  OR socket.effectiveness_value BETWEEN
                      effect.grade_one_minimum * socket.effect_level
                      AND effect.grade_one_accepted_maximum *
                          socket.effect_level
              )
              AND owner.fighter_job_lv >=
                  COALESCE(template.min_level, 1)
              AND (
                  template.max_level IS NULL
                  OR owner.fighter_job_lv <= template.max_level
              )
              AND (
                  cardinality(template.class_ids) = 0
                  OR owner.profession = ANY(template.class_ids)
              )
        ),
        holy_stone_socket_values AS (
            SELECT
                socket.user_id,
                socket.effect_id,
                CASE
                    WHEN socket.effect_id IN (9, 10, 13) THEN LEAST(
                        socket.stat_value,
                        socket.grade_one_maximum * socket.safe_level)
                    ELSE socket.stat_value
                END AS stat_value
            FROM holy_stone_socket_raw_values socket
        ),
        holy_stone_stat_values AS (
            SELECT
                socket.user_id,
                CASE
                    WHEN socket.effect_id IN (9,10,11,12,13,14,19,20)
                        THEN 'damage_absorb'
                    WHEN socket.effect_id = 3 THEN 'physical_damage_bonus'
                    WHEN socket.effect_id = 4 THEN 'magic_damage_bonus'
                    WHEN socket.effect_id IN (15,16) THEN 'hp_recovery'
                    WHEN socket.effect_id IN (17,18) THEN 'mp_recovery'
                    WHEN socket.effect_id = 1 THEN 'ignore_physical_defense'
                    WHEN socket.effect_id = 2 THEN 'ignore_magic_defense'
                    WHEN socket.effect_id = 5 THEN 'physical_append_damage'
                    WHEN socket.effect_id = 6 THEN 'magic_append_damage'
                    WHEN socket.effect_id = 7 THEN 'critical_damage_percent'
                    WHEN socket.effect_id = 8 THEN 'critical_damage_flat'
                END AS stat_name,
                socket.stat_value
            FROM holy_stone_socket_values socket
        ),
        holy_stone_combat_stat_values AS (
            SELECT
                socket.user_id,
                CASE socket.effect_id
                    WHEN 9 THEN 'physical_damage_reduction'
                    WHEN 10 THEN 'magic_damage_reduction'
                    WHEN 11 THEN 'physical_flat_absorption'
                    WHEN 12 THEN 'magic_flat_absorption'
                    WHEN 13 THEN 'critical_damage_reduction'
                    WHEN 14 THEN 'critical_damage_flat_reduction'
                    WHEN 19 THEN 'damage_rebound'
                    WHEN 20 THEN 'damage_rebound_flat'
                END AS stat_name,
                socket.stat_value
            FROM holy_stone_socket_values socket
            WHERE socket.effect_id IN (9,10,11,12,13,14,19,20)
        ),
        """;
}
