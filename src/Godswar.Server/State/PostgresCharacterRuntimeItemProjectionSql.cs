namespace Godswar.Server.State;

/// <summary>
/// Runtime character projections whose item-derived values are bound to the
/// content revision pinned by the server process. Compatibility views may
/// follow the official publication pointer; active runtime reads may not.
/// </summary>
internal static partial class PostgresCharacterRuntimeItemProjectionSql
{
    public static readonly string CalculatedStatsForCharacter =
        $$"""
        WITH
        {{PostgresMountGearPassiveProjectionSql.CommonTableExpressions}}
        equipment_stat_values AS (
            SELECT
                equipment.user_id,
                stat.stat_name,
                COALESCE(NULLIF(stat_values.values[
                    LEAST(
                        GREATEST(equipment.item_quality::integer, 1),
                        array_length(stat_values.values, 1))
                ], '')::numeric, 0::numeric) * stat.scale AS stat_value
            FROM character_items equipment
            JOIN item_template_content_revisions revision
              ON revision.revision = @itemContentRevision
             AND revision.sealed_at IS NOT NULL
            JOIN item_template_content_definitions template
              ON template.revision = revision.revision
             AND template.id = equipment.prop_id
            CROSS JOIN (
                VALUES
                    ('MaxHP', 'max_hp', 1::numeric),
                    ('MaxMP', 'max_mp', 1::numeric),
                    ('Attack', 'physical_attack', 1::numeric),
                    ('Defence', 'physical_defense', 1::numeric),
                    ('MagicAk', 'magic_attack', 1::numeric),
                    ('MagicRec', 'magic_defense', 1::numeric),
                    ('Hit', 'hit', 1::numeric),
                    ('Miss', 'dodge', 1::numeric),
                    ('FuryAddAk', 'critical', 1::numeric),
                    ('FuryAddRec', 'critical_resistance', 1::numeric),
                    ('InjureImbibe', 'damage_absorb', 1::numeric),
                    ('PhysicalDamage', 'physical_damage_bonus', 10000::numeric),
                    ('MagicDamage', 'magic_damage_bonus', 10000::numeric),
                    ('PhysicalDamageAbsorb', 'damage_absorb', 1::numeric),
                    ('MagicDamageAbsorb', 'damage_absorb', 1::numeric),
                    ('Cure', 'cure_bonus', 10000::numeric),
                    ('AcceptCure', 'be_cure_bonus', 10000::numeric),
                    ('HPRestore', 'hp_recovery', 1::numeric),
                    ('MPRestore', 'mp_recovery', 1::numeric),
                    ('IgnorePhyPer', 'ignore_physical_defense', 10000::numeric),
                    ('IgnoreMagPer', 'ignore_magic_defense', 10000::numeric),
                    ('PhyAppendDamageVal', 'physical_append_damage', 1::numeric),
                    ('MagAppendDamageVal', 'magic_append_damage', 1::numeric),
                    ('CriIncPer', 'critical_damage_percent', 10000::numeric),
                    ('CriIncVal', 'critical_damage_flat', 1::numeric)
            ) stat(source_key, stat_name, scale)
            JOIN LATERAL (
                SELECT string_to_array(
                    template.stats->>stat.source_key, ',') AS values
                WHERE template.stats ? stat.source_key
            ) stat_values ON true
            WHERE equipment.item_location = 0
        ),
        attribute_stat_values AS (
            SELECT
                equipment.user_id,
                stat.stat_name,
                CASE WHEN attribute_template.percent
                    THEN attribute_value.value * 10000
                    ELSE attribute_value.value
                END AS stat_value
            FROM character_items equipment
            CROSS JOIN LATERAL (
                VALUES
                    (equipment.attribute1),
                    (equipment.attribute2),
                    (equipment.attribute3),
                    (equipment.attribute4),
                    (equipment.attribute5),
                    (equipment.class_attribute1)
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
                    (8, 'physical_damage_bonus'), (9, 'magic_damage_bonus'),
                    (10, 'damage_absorb'), (13, 'max_hp'), (14, 'max_mp'),
                    (15, 'hp_recovery'), (16, 'mp_recovery'),
                    (17, 'be_cure_bonus'), (18, 'cure_bonus'),
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
                        GREATEST(equipment.item_grade::integer, 1),
                        array_length(
                            attribute_template.level_values,
                            1))
                ] AS value
            ) attribute_value
            WHERE equipment.item_location = 0
              AND attribute.attribute_id IS NOT NULL
              AND attribute.attribute_id >= 0
              AND attribute_value.value IS NOT NULL
        ),
        holy_stone_stat_values AS (
            SELECT
                equipment.user_id,
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
            CROSS JOIN LATERAL (
                VALUES
                    (equipment.holy_socket1_effect_id, equipment.holy_socket1_level, equipment.holy_socket1_value),
                    (equipment.holy_socket2_effect_id, equipment.holy_socket2_level, equipment.holy_socket2_value),
                    (equipment.holy_socket3_effect_id, equipment.holy_socket3_level, equipment.holy_socket3_value),
                    (equipment.holy_socket4_effect_id, equipment.holy_socket4_level, equipment.holy_socket4_value)
            ) socket(effect_id, effect_level, effectiveness_value)
            CROSS JOIN LATERAL (
                SELECT LEAST(
                    GREATEST(COALESCE(socket.effect_level, 1)::integer, 1),
                    10) AS safe_level
            ) socket_level
            WHERE equipment.item_location = 0
              AND socket.effect_id IS NOT NULL
              AND socket.effect_id > 0
        ),
        talent_stat_values AS (
            SELECT
                talent.user_id,
                stat.stat_name,
                talent_effective_rank(talent.rank) *
                    CASE WHEN template.is_percent
                        THEN template.effect_value * 10000
                        ELSE template.effect_value
                    END AS stat_value
            FROM character_talents talent
            JOIN character_base character
              ON character.id = talent.user_id
            JOIN gameplay_talent_definitions template
              ON template.id = talent.talent_id
             AND template.class_id = character.profession
             AND template.revision = COALESCE(
                 @gameplayContentRevision,
                 (
                     SELECT publication.revision
                     FROM gameplay_content_publication publication
                     WHERE publication.family = 'gameplay'
                 )
             )
            JOIN gameplay_talent_effect_definitions effect
              ON effect.revision = template.revision
             AND effect.id = template.effect_id
            JOIN (
                VALUES
                    ('MaxHP', 'max_hp'), ('MaxMP', 'max_mp'),
                    ('PhyAttack', 'physical_attack'),
                    ('PhyDefend', 'physical_defense'),
                    ('MagicAttack', 'magic_attack'),
                    ('MagicDefend', 'magic_defense'),
                    ('Hit', 'hit'), ('Miss', 'dodge'),
                    ('FrenzyHit', 'critical'),
                    ('FrenzyMiss', 'critical_resistance'),
                    ('DamageSorb', 'damage_absorb'),
                    ('PhyDamage', 'physical_damage_bonus'),
                    ('MagicDamage', 'magic_damage_bonus'),
                    ('Cure', 'cure_bonus'), ('Becure', 'be_cure_bonus'),
                    ('HPResume', 'hp_recovery'), ('MPResume', 'mp_recovery')
            ) stat(effect_key, stat_name)
              ON stat.effect_key = effect.key
            WHERE talent.rank > 0
        ),
        holy_suit_stat_values AS (
            SELECT
                character.id AS user_id,
                stat.stat_name,
                effect.effect_value AS stat_value
            FROM character_base character
            JOIN holy_suit_effect_content_definitions effect
              ON effect.revision = @itemContentRevision
             AND character.holy_suit_points >= effect.unlock_points
            JOIN (
                VALUES
                    ('MaxHPD', 'max_hp'), ('MaxMPD', 'max_mp'),
                    ('Attack', 'physical_attack'),
                    ('Defence', 'physical_defense'),
                    ('MagicAk', 'magic_attack'),
                    ('MagicRec', 'magic_defense'),
                    ('Hit', 'hit'), ('Miss', 'dodge'),
                    ('InjureImbibe', 'damage_absorb')
            ) stat(effect_key, stat_name)
              ON stat.effect_key = effect.effect_key
        ),
        all_stat_values AS (
            SELECT * FROM equipment_stat_values
            UNION ALL SELECT * FROM attribute_stat_values
            UNION ALL SELECT * FROM holy_stone_stat_values
            WHERE stat_name IS NOT NULL
            UNION ALL SELECT * FROM talent_stat_values
            UNION ALL SELECT * FROM holy_suit_stat_values
            UNION ALL SELECT * FROM mount_gear_spirit_stat_values
        ),
        stat_totals AS (
            SELECT
                user_id,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'max_hp'), 0) AS max_hp,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'max_mp'), 0) AS max_mp,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'physical_attack'), 0) AS physical_attack,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'physical_defense'), 0) AS physical_defense,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'magic_attack'), 0) AS magic_attack,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'magic_defense'), 0) AS magic_defense,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'hit'), 0) AS hit,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'dodge'), 0) AS dodge,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'critical'), 0) AS critical,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'critical_resistance'), 0) AS critical_resistance,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'damage_absorb'), 0) AS damage_absorb,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'physical_damage_bonus'), 0) AS physical_damage_bonus,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'magic_damage_bonus'), 0) AS magic_damage_bonus,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'cure_bonus'), 0) AS cure_bonus,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'be_cure_bonus'), 0) AS be_cure_bonus,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'hp_recovery'), 0) AS hp_recovery,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'mp_recovery'), 0) AS mp_recovery,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'ignore_physical_defense'), 0) AS ignore_physical_defense,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'ignore_magic_defense'), 0) AS ignore_magic_defense,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'physical_append_damage'), 0) AS physical_append_damage,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'magic_append_damage'), 0) AS magic_append_damage,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'critical_damage_percent'), 0) AS critical_damage_percent,
                COALESCE(SUM(stat_value) FILTER (WHERE stat_name = 'critical_damage_flat'), 0) AS critical_damage_flat
            FROM all_stat_values
            GROUP BY user_id
        )
        SELECT
            cb.id AS user_id,
            cb.account_id,
            cb.name,
            cb.profession,
            cb.fighter_job_lv AS level,
            GREATEST(1, ROUND(cb."MaxHP" + COALESCE(stats.max_hp, 0)))::integer AS max_hp,
            GREATEST(0, ROUND(cb."MaxMP" + COALESCE(stats.max_mp, 0)))::integer AS max_mp,
            LEAST(GREATEST(cb."curHP", 0), GREATEST(1, ROUND(cb."MaxHP" + COALESCE(stats.max_hp, 0)))::integer) AS current_hp,
            LEAST(GREATEST(cb."curMP", 0), GREATEST(0, ROUND(cb."MaxMP" + COALESCE(stats.max_mp, 0)))::integer) AS current_mp,
            ROUND(COALESCE(stats.physical_attack, 0))::integer AS physical_attack,
            ROUND(COALESCE(stats.physical_defense, 0))::integer AS physical_defense,
            ROUND(COALESCE(stats.magic_attack, 0))::integer AS magic_attack,
            ROUND(COALESCE(stats.magic_defense, 0))::integer AS magic_defense,
            ROUND(COALESCE(stats.hit, 0))::integer AS hit,
            ROUND(COALESCE(stats.dodge, 0))::integer AS dodge,
            ROUND(COALESCE(stats.critical, 0))::integer AS critical,
            ROUND(COALESCE(stats.critical_resistance, 0))::integer AS critical_resistance,
            ROUND(COALESCE(stats.damage_absorb, 0))::integer AS damage_absorb,
            ROUND(COALESCE(stats.physical_damage_bonus, 0))::integer AS physical_damage_bonus,
            ROUND(COALESCE(stats.magic_damage_bonus, 0))::integer AS magic_damage_bonus,
            ROUND(COALESCE(stats.cure_bonus, 0))::integer AS cure_bonus,
            ROUND(COALESCE(stats.be_cure_bonus, 0))::integer AS be_cure_bonus,
            ROUND(COALESCE(stats.hp_recovery, 0))::integer AS hp_recovery,
            ROUND(COALESCE(stats.mp_recovery, 0))::integer AS mp_recovery,
            ROUND(COALESCE(stats.ignore_physical_defense, 0))::integer AS ignore_physical_defense,
            ROUND(COALESCE(stats.ignore_magic_defense, 0))::integer AS ignore_magic_defense,
            ROUND(COALESCE(stats.physical_append_damage, 0))::integer AS physical_append_damage,
            ROUND(COALESCE(stats.magic_append_damage, 0))::integer AS magic_append_damage,
            ROUND(COALESCE(stats.critical_damage_percent, 0))::integer AS critical_damage_percent,
            ROUND(COALESCE(stats.critical_damage_flat, 0))::integer AS critical_damage_flat,
            item_rank_projection.weapon_score,
            item_rank_projection.weapon_rank,
            item_rank_projection.weapon_aura_effect,
            item_rank_projection.armor_score,
            item_rank_projection.armor_rank,
            item_rank_projection.armor_aura_effect,
            COALESCE((
                SELECT COUNT(*)::integer
                FROM character_skills skill
                WHERE skill.user_id = cb.id
            ), 0) AS learned_skill_count
        FROM character_base cb
        LEFT JOIN stat_totals stats ON stats.user_id = cb.id
        {{RankLateralJoinForCharacterAlias}}
        WHERE cb.account_id = @accountId
          AND cb.id = @characterId
          AND cb.lifecycle_state = 'active';

        """;
}
