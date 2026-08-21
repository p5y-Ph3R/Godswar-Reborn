using Npgsql;

namespace Godswar.Server.Infrastructure.Database;

internal static partial class PostgresRelationalContentBaselineBootstrapper
{
    private static async Task ApplyGameplayPolicyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            DO $champion_talent_authority$
            DECLARE
                authority_count integer;
                tooltip_count integer;
                corrected_count integer;
            BEGIN
                WITH correction(id, effect_value, tooltip_value) AS (
                    VALUES
                        (50, 3::numeric, 7.8::numeric),
                        (51, 10::numeric, 26::numeric),
                        (52, 9::numeric, 23.4::numeric),
                        (53, 50::numeric, 130::numeric),
                        (54, 2::numeric, 5.2::numeric),
                        (55, 0.005::numeric, 0.013::numeric),
                        (56, 5::numeric, 13::numeric),
                        (57, 16::numeric, 41.6::numeric),
                        (58, 4::numeric, 10.4::numeric),
                        (59, 7::numeric, 18.2::numeric),
                        (60, 3::numeric, 7.8::numeric),
                        (61, 0.01::numeric, 0.026::numeric),
                        (62, 20::numeric, 52::numeric),
                        (63, 1.6::numeric, 4.16::numeric),
                        (64, 4::numeric, 10.4::numeric),
                        (65, 1.2::numeric, 3.12::numeric),
                        (66, 7::numeric, 18.2::numeric),
                        (67, 90::numeric, 234::numeric),
                        (68, 90::numeric, 234::numeric)
                )
                SELECT
                    count(*) FILTER (
                        WHERE talent.effect_value = correction.effect_value
                          AND talent.stats ->> talent.effect_type =
                              talent.effect_id::text || ',' ||
                              correction.effect_value::text),
                    count(*) FILTER (
                        WHERE talent.effect_value = correction.tooltip_value
                          AND talent.stats ->> talent.effect_type =
                              talent.effect_id::text || ',' ||
                              correction.tooltip_value::text)
                INTO authority_count, tooltip_count
                FROM correction
                LEFT JOIN talent_templates talent
                  ON talent.id = correction.id
                 AND talent.class_id = 1;

                IF authority_count = 19 AND tooltip_count = 0 THEN
                    NULL;
                ELSIF authority_count = 0 AND tooltip_count = 19 THEN
                    WITH correction(id, effect_value, tooltip_value) AS (
                        VALUES
                            (50, 3::numeric, 7.8::numeric),
                            (51, 10::numeric, 26::numeric),
                            (52, 9::numeric, 23.4::numeric),
                            (53, 50::numeric, 130::numeric),
                            (54, 2::numeric, 5.2::numeric),
                            (55, 0.005::numeric, 0.013::numeric),
                            (56, 5::numeric, 13::numeric),
                            (57, 16::numeric, 41.6::numeric),
                            (58, 4::numeric, 10.4::numeric),
                            (59, 7::numeric, 18.2::numeric),
                            (60, 3::numeric, 7.8::numeric),
                            (61, 0.01::numeric, 0.026::numeric),
                            (62, 20::numeric, 52::numeric),
                            (63, 1.6::numeric, 4.16::numeric),
                            (64, 4::numeric, 10.4::numeric),
                            (65, 1.2::numeric, 3.12::numeric),
                            (66, 7::numeric, 18.2::numeric),
                            (67, 90::numeric, 234::numeric),
                            (68, 90::numeric, 234::numeric)
                    )
                    UPDATE talent_templates talent
                    SET effect_value = correction.effect_value,
                        stats = jsonb_set(
                            talent.stats,
                            ARRAY[talent.effect_type],
                            to_jsonb((talent.effect_id::text || ',' ||
                                correction.effect_value::text)::text),
                            false)
                    FROM correction
                    WHERE talent.id = correction.id
                      AND talent.class_id = 1
                      AND talent.effect_value = correction.tooltip_value
                      AND talent.stats ->> talent.effect_type =
                          talent.effect_id::text || ',' ||
                          correction.tooltip_value::text;
                    GET DIAGNOSTICS corrected_count = ROW_COUNT;
                    IF corrected_count <> 19 THEN
                        RAISE EXCEPTION
                            'Champion talent authority corrected % rows; expected 19.',
                            corrected_count;
                    END IF;
                ELSE
                    RAISE EXCEPTION
                        'Champion talent authority expected 19 uniformly authoritative or tooltip rows; found % authoritative and % tooltip rows.',
                        authority_count,
                        tooltip_count;
                END IF;
            END;
            $champion_talent_authority$;

            UPDATE map_links
            SET confidence = 'captured-span-map',
                activation = 'automatic',
                note = 'Captured SpanMap boundary with a matching reciprocal.';

            -- The reviewed legacy SQL contains four duplicate portal rows
            -- whose link_index values differ while the actual portal identity
            -- (source map, target map and coordinates) is identical. Collapse
            -- those duplicates before publishing the immutable revision so a
            -- clean install and an upgraded install produce the same catalog.
            WITH ranked AS (
                SELECT ctid,
                       row_number() OVER (
                           PARTITION BY map_id, target_map_id, pos_x, pos_z
                           ORDER BY link_index
                       ) AS ordinal
                FROM map_links
            )
            DELETE FROM map_links AS links
            USING ranked
            WHERE links.ctid = ranked.ctid
              AND ranked.ordinal > 1;

            UPDATE map_links
            SET confidence = 'excluded-by-observed-topology',
                activation = 'disabled-by-world-topology',
                note = 'Disabled walking edge: observed world topology permits Mycenae access only through Olympia.'
            WHERE map_id = 6 AND target_map_id IN (9, 15);

            INSERT INTO map_links (
                map_id, link_index, target_map_id, pos_x, pos_z, source,
                confidence, activation, note
            )
            VALUES
                (6, 3, 7, -198, 0,
                 './Localization/en_us/Monster/Mycenae_All/Address.ini',
                 'reciprocal-address-point', 'automatic',
                 'Exact Olympia address point paired with its reciprocal map label.'),
                (7, 1, 6, 212, -104,
                 './Localization/en_us/Monster/Olympia_All/Address.ini',
                 'reciprocal-address-point', 'automatic',
                 'Exact Mycenae address point paired with its reciprocal map label.'),
                (7, 2, 20, -181, 226,
                 './Localization/en_us/Monster/Olympia_All/Address.ini',
                 'reciprocal-address-point', 'automatic',
                 'Exact Delphi Forest address point paired with its reciprocal map label.'),
                (20, 1, 7, 132, -224,
                 './Localization/en_us/Monster/Oracle_of_Delphi_All/Address.ini',
                 'reciprocal-address-point', 'automatic',
                 'Exact Olympia address point paired with its reciprocal map label.'),
                (20, 2, 10, -200, -4,
                 './Localization/en_us/Monster/Oracle_of_Delphi_All/Address.ini',
                 'reciprocal-address-point', 'automatic',
                 'Exact Larissa address point paired with its reciprocal map label.'),
                (10, 1, 20, 216, -68,
                 './Localization/en_us/Monster/Larissa_All/Address.ini',
                 'reciprocal-address-point', 'automatic',
                 'Exact Delphi Forest address point paired with its reciprocal map label.'),
                (10, 2, 22, -195, 150,
                 './Localization/en_us/Monster/Larissa_All/Address.ini',
                 'reciprocal-address-point', 'automatic',
                 'Exact Elasson address point paired with its reciprocal map label.'),
                (22, 1, 10, 208, -16,
                 './Localization/en_us/Monster/Elasson_All/Address.ini',
                 'reciprocal-address-point', 'automatic',
                 'Exact Larissa address point paired with its reciprocal map label.'),
                (22, 2, 21, -208, 124,
                 './Localization/en_us/Monster/Elasson_All/Address.ini',
                 'reciprocal-address-point', 'automatic',
                 'Exact Olympus address point paired with its reciprocal map label.'),
                (21, 1, 22, 212, 80,
                 './Localization/en_us/Monster/Olympus_All/Address.ini',
                 'reciprocal-address-point', 'automatic',
                 'Exact Elasson address point paired with its reciprocal map label.')
            ON CONFLICT (map_id, link_index, target_map_id) DO UPDATE
            SET pos_x = EXCLUDED.pos_x,
                pos_z = EXCLUDED.pos_z,
                source = EXCLUDED.source,
                confidence = EXCLUDED.confidence,
                activation = EXCLUDED.activation,
                note = EXCLUDED.note;

            UPDATE world_boss_areas SET enabled = false;

            INSERT INTO world_boss_areas (
                map_id, boss_template_key, boss_display_name,
                bonus_basis_points, respawn_interval_seconds, enabled
            )
            VALUES
                (3, 'A_boss_boar_001', 'Boar King Tomas', 2500, 43200, true),
                (5, 'A_boss_wolf_005', 'Astrien', 2500, 43200, true),
                (6, 'A_boss_kingofscorpion_001', '[BOSS]Darkmist', 2500, 43200, true),
                (7, 'C_boss_centaur_001', 'Centaur Leader', 2500, 43200, true),
                (8, 'B_bossB_xerxes_001', 'Mardonius', 2500, 43200, true),
                (9, 'A_boss_kingofscorpiondi_001', '[BOSS]Scorpion Lord Selket', 2500, 43200, true),
                (10, 'C_boss_dragon_014', 'Little Demate', 2500, 43200, true),
                (11, 'A_boss_bull_001', 'Minos the Bull King', 2500, 43200, true),
                (12, 'B_bossB_octopus_001', 'Naga Siren Eirsigel', 2500, 43200, true),
                (13, 'A_boss_spider_008', 'Spider Queen Ala', 2500, 43200, true),
                (14, 'B_bossB_spriggan_001', 'Evil Treant Falio', 2500, 43200, true),
                (15, 'B_boss_centaur_001', 'Centaur Shaikh Hailer', 2500, 43200, true),
                (16, 'A_boss_amazon_004', 'Leader Cassirer', 2500, 43200, true),
                (17, 'B_boss_dragon_001', 'Red Dragon Puluo', 2500, 43200, true),
                (18, 'A_boss_mage_018', 'Lord Barryonyx', 2500, 43200, true),
                (19, 'B_boss_cyclops_001', 'Giant Alcyoneus', 2500, 43200, true),
                (20, 'A_boss_long_005', 'Hydra Lord Xausa', 2500, 43200, true),
                (21, 'C_boss_dragon_013', 'Bahamut', 2500, 43200, true),
                (22, 'C_boss_dragon_002', 'Ice Dragon', 2500, 43200, true)
            ON CONFLICT (map_id) DO UPDATE
            SET boss_template_key = EXCLUDED.boss_template_key,
                boss_display_name = EXCLUDED.boss_display_name,
                bonus_basis_points = EXCLUDED.bonus_basis_points,
                respawn_interval_seconds = EXCLUDED.respawn_interval_seconds,
                enabled = EXCLUDED.enabled;

            INSERT INTO pending_world_boss_areas (map_id, scene_key, reason)
            VALUES (
                68,
                'Parnassus',
                'Outdoor faction area; requires a new neutral boss because its Athenian and Spartan Generals are opposing-faction quest objectives.'
            )
            ON CONFLICT (map_id) DO UPDATE
            SET scene_key = EXCLUDED.scene_key,
                reason = EXCLUDED.reason;
            """,
            connection,
            transaction)
        {
            CommandTimeout = 120
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
