namespace Godswar.Server.State;

/// <summary>
/// Resolves the equipped weapon's authored grade-indexed cadence and range.
/// The scalar row also supplies deterministic unarmed/content fallbacks.
/// </summary>
internal static class PostgresCharacterWeaponCombatProjectionSql
{
    public const string LateralJoinForCharacterAlias =
        """
        LEFT JOIN LATERAL (
            SELECT
                COALESCE(
                    weapon.basic_attack_interval_milliseconds,
                    1500)::integer AS basic_attack_interval_milliseconds,
                COALESCE(
                    weapon.basic_attack_range,
                    1.7::real)::real AS basic_attack_range
            FROM (VALUES (1)) singleton(value)
            LEFT JOIN LATERAL (
                SELECT
                    COALESCE(
                        ROUND(1000::numeric +
                            NULLIF(speed_values.values[
                                LEAST(
                                    GREATEST(equipment.item_grade::integer, 1),
                                    array_length(speed_values.values, 1))
                            ], '')::numeric * 1000::numeric)::integer,
                        1500) AS basic_attack_interval_milliseconds,
                    COALESCE(
                        NULLIF(range_values.values[
                            LEAST(
                                GREATEST(equipment.item_grade::integer, 1),
                                array_length(range_values.values, 1))
                        ], '')::real,
                        1.7::real) AS basic_attack_range
                FROM character_items equipment
                JOIN item_template_content_revisions revision
                  ON revision.revision = @itemContentRevision
                 AND revision.sealed_at IS NOT NULL
                JOIN item_template_content_definitions template
                  ON template.revision = revision.revision
                 AND template.id = equipment.prop_id
                 AND template.kind = 'weapon'
                 AND template.equipment_slot = 10
                LEFT JOIN LATERAL (
                    SELECT string_to_array(
                        template.stats->>'AttackSpeed', ',') AS values
                    WHERE template.stats ? 'AttackSpeed'
                ) speed_values ON true
                LEFT JOIN LATERAL (
                    SELECT string_to_array(
                        template.stats->>'AttackRadius', ',') AS values
                    WHERE template.stats ? 'AttackRadius'
                ) range_values ON true
                WHERE equipment.user_id = cb.id
                  AND equipment.item_location = 0
                  AND equipment.slot_index = 10
                  AND cb.fighter_job_lv >=
                      COALESCE(template.min_level, 1)
                  AND (
                      template.max_level IS NULL
                      OR cb.fighter_job_lv <= template.max_level
                  )
                  AND (
                      cardinality(template.class_ids) = 0
                      OR cb.profession = ANY(template.class_ids)
                  )
                ORDER BY equipment.slot_index
                LIMIT 1
            ) weapon ON true
        ) weapon_combat_projection ON true
        """;
}
