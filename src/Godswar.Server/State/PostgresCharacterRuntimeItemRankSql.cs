namespace Godswar.Server.State;

internal static partial class PostgresCharacterRuntimeItemProjectionSql
{
    public const string RankLateralJoinForCharacterAlias = """
        LEFT JOIN LATERAL (
            WITH equipment_scores AS (
                SELECT
                    template.kind,
                    CASE
                        WHEN base_values.values IS NULL THEN 0
                        ELSE COALESCE(NULLIF(base_values.values[
                            LEAST(
                                GREATEST(equipment.item_quality::integer, 1),
                                array_length(base_values.values, 1))
                        ], '')::integer, 0)
                    END +
                    CASE
                        WHEN grade_values.values IS NULL THEN 0
                        ELSE COALESCE(NULLIF(grade_values.values[
                            LEAST(
                                GREATEST(equipment.item_grade::integer, 1),
                                array_length(grade_values.values, 1))
                        ], '')::integer, 0) * (
                            (equipment.attribute1 IS NOT NULL AND equipment.attribute1 >= 0)::integer +
                            (equipment.attribute2 IS NOT NULL AND equipment.attribute2 >= 0)::integer +
                            (equipment.attribute3 IS NOT NULL AND equipment.attribute3 >= 0)::integer +
                            (equipment.attribute4 IS NOT NULL AND equipment.attribute4 >= 0)::integer +
                            (equipment.attribute5 IS NOT NULL AND equipment.attribute5 >= 0)::integer +
                            (equipment.class_attribute1 IS NOT NULL AND equipment.class_attribute1 >= 0)::integer +
                            (equipment.class_attribute2 IS NOT NULL AND equipment.class_attribute2 >= 0)::integer
                        )
                    END AS item_score
                FROM character_items equipment
                JOIN item_template_content_revisions revision
                  ON revision.revision = @itemContentRevision
                 AND revision.sealed_at IS NOT NULL
                JOIN item_template_content_definitions template
                  ON template.revision = revision.revision
                 AND template.id = equipment.prop_id
                LEFT JOIN LATERAL (
                    SELECT string_to_array(
                        template.stats->>'BaseFraction', ',') AS values
                    WHERE template.stats ? 'BaseFraction'
                ) base_values ON true
                LEFT JOIN LATERAL (
                    SELECT string_to_array(
                        template.stats->>'AppFraction', ',') AS values
                    WHERE template.stats ? 'AppFraction'
                ) grade_values ON true
                WHERE equipment.user_id = cb.id
                  AND equipment.item_location = 0
            ),
            totals AS (
                SELECT
                    COALESCE(SUM(item_score) FILTER (
                        WHERE kind = 'weapon'), 0)::integer AS weapon_score,
                    COALESCE(SUM(item_score) FILTER (
                        WHERE kind <> 'weapon'
                          AND kind NOT IN (
                              'mount', 'mounthead', 'mountarmor',
                              'mountsoul', 'mountornament', 'mountamulet'
                          )), 0)::integer AS armor_score
                FROM equipment_scores
            )
            SELECT
                totals.weapon_score,
                COALESCE(weapon_rank.rank_level, 0)::smallint AS weapon_rank,
                COALESCE(weapon_rank.aura_effect, 0) AS weapon_aura_effect,
                totals.armor_score,
                COALESCE(armor_rank.rank_level, 0)::smallint AS armor_rank,
                COALESCE(armor_rank.aura_effect, 0) AS armor_aura_effect
            FROM totals
            LEFT JOIN LATERAL (
                SELECT rule.rank_level, rule.aura_effect
                FROM equipment_rank_content_definitions rule
                WHERE rule.revision = @itemContentRevision
                  AND rule.rank_kind = 'weapon'
                  AND rule.required_score <= totals.weapon_score
                ORDER BY rule.rank_level DESC
                LIMIT 1
            ) weapon_rank ON true
            LEFT JOIN LATERAL (
                SELECT rule.rank_level, rule.aura_effect
                FROM equipment_rank_content_definitions rule
                WHERE rule.revision = @itemContentRevision
                  AND rule.rank_kind = 'armor'
                  AND rule.required_score <= totals.armor_score
                ORDER BY rule.rank_level DESC
                LIMIT 1
            ) armor_rank ON true
        ) item_rank_projection ON true
        """;
}
