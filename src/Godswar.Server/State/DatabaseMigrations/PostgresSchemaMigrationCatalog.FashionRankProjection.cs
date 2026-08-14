namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateFashionRankProjectionRepair() => new(
        "20260810_063_fashion_rank_projection_repair",
        "Restore the fashion-safe rank summary to immutable rank content",
        """
        CREATE OR REPLACE VIEW public.character_rank_summary AS
        WITH totals AS (
            SELECT
                user_id,
                COALESCE(SUM(item_score) FILTER (
                    WHERE kind = 'weapon'
                ), 0)::integer AS weapon_score,
                COALESCE(SUM(item_score) FILTER (
                    WHERE kind <> 'weapon'
                      AND kind NOT IN (
                          'stylish',
                          'mount',
                          'mounthead',
                          'mountarmor',
                          'mountsoul',
                          'mountornament',
                          'mountamulet'
                      )
                ), 0)::integer AS armor_score
            FROM public.character_equipment_scores
            GROUP BY user_id
        )
        SELECT
            fighter.id AS user_id,
            fighter.name,
            COALESCE(total.weapon_score, 0) AS weapon_score,
            COALESCE(weapon_rank.rank_level, 0)::smallint
                AS weapon_rank,
            COALESCE(weapon_rank.aura_effect, 0)
                AS weapon_aura_effect,
            COALESCE(total.armor_score, 0) AS armor_score,
            COALESCE(armor_rank.rank_level, 0)::smallint
                AS armor_rank,
            COALESCE(armor_rank.aura_effect, 0)
                AS armor_aura_effect
        FROM public.character_base fighter
        LEFT JOIN totals total ON total.user_id = fighter.id
        LEFT JOIN LATERAL (
            SELECT rank_level, aura_effect
            FROM public.official_equipment_rank_content
            WHERE rank_kind = 'weapon'
              AND required_score <= COALESCE(total.weapon_score, 0)
            ORDER BY rank_level DESC
            LIMIT 1
        ) weapon_rank ON true
        LEFT JOIN LATERAL (
            SELECT rank_level, aura_effect
            FROM public.official_equipment_rank_content
            WHERE rank_kind = 'armor'
              AND required_score <= COALESCE(total.armor_score, 0)
            ORDER BY rank_level DESC
            LIMIT 1
        ) armor_rank ON true;
        """);
}
