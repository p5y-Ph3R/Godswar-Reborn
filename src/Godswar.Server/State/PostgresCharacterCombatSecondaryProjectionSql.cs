namespace Godswar.Server.State;

/// <summary>
/// Splits legacy all-damage absorption into typed combat channels and adds
/// percentage-based vampiric/reflect attributes. The legacy source rows remain
/// in the ordinary projection for packet compatibility.
/// </summary>
internal static class PostgresCharacterCombatSecondaryProjectionSql
{
    public const string CommonTableExpressions =
        """
        equipment_typed_absorption_stat_values AS (
            SELECT
                equipment.user_id,
                stat.stat_name,
                COALESCE(NULLIF(stat_values.values[
                    LEAST(
                        GREATEST(equipment.item_quality::integer, 1),
                        array_length(stat_values.values, 1))
                ], '')::numeric, 0::numeric) AS stat_value
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
            CROSS JOIN (
                VALUES
                    ('InjureImbibe', 'physical_flat_absorption'),
                    ('InjureImbibe', 'magic_flat_absorption'),
                    ('PhysicalDamageAbsorb', 'physical_flat_absorption'),
                    ('MagicDamageAbsorb', 'magic_flat_absorption')
            ) stat(source_key, stat_name)
            JOIN LATERAL (
                SELECT string_to_array(
                    template.stats->>stat.source_key, ',') AS values
                WHERE template.stats ? stat.source_key
            ) stat_values ON true
            WHERE equipment.item_location = 0
              AND equipment.user_id = @characterId
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
        compatibility_flat_absorption_stat_values AS (
            SELECT
                source.user_id,
                typed.stat_name,
                source.stat_value
            FROM (
                SELECT * FROM attribute_stat_values
                WHERE stat_name = 'damage_absorb'
                UNION ALL
                SELECT * FROM talent_stat_values
                WHERE stat_name = 'damage_absorb'
                UNION ALL
                SELECT * FROM holy_suit_stat_values
                WHERE stat_name = 'damage_absorb'
                UNION ALL
                SELECT * FROM mount_gear_spirit_stat_values
                WHERE stat_name = 'damage_absorb'
                UNION ALL
                SELECT * FROM pet_owner_merge_stat_values
                WHERE stat_name = 'damage_absorb'
            ) source
            CROSS JOIN (
                VALUES
                    ('physical_flat_absorption'),
                    ('magic_flat_absorption')
            ) typed(stat_name)
        ),
        combat_secondary_stat_values AS (
            SELECT * FROM equipment_typed_absorption_stat_values
            UNION ALL
            SELECT * FROM compatibility_flat_absorption_stat_values
            UNION ALL
            SELECT * FROM holy_stone_combat_stat_values
        ),
        """;
}
