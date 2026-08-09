namespace Godswar.Server.State;

internal static partial class PostgresSchemaMigrationCatalog
{
    private static PostgresSchemaMigration
        CreateFashionSlotConsistency() => new(
        "20260810_060_fashion_slot_consistency",
        "Move unambiguous legacy fashion items to slot 12 and exclude fashion from armor rank",
        """
        CREATE TEMPORARY TABLE fashion_slot_repair_candidates
        ON COMMIT DROP
        AS
        SELECT
            legacy_item.id AS item_instance_id,
            legacy_item.user_id AS character_id
        FROM public.character_items AS legacy_item
        JOIN public.item_templates AS template
          ON template.id = legacy_item.prop_id
         AND template.kind = 'stylish'
         AND template.equipment_slot = 12
        JOIN public.character_inventory_baseline_items AS baseline_item
          ON baseline_item.character_id = legacy_item.user_id
         AND baseline_item.item_instance_id = legacy_item.id
         AND baseline_item.item_location = 0
         AND baseline_item.slot_index = 13
         AND baseline_item.prop_id = legacy_item.prop_id
        WHERE legacy_item.item_location = 0
          AND legacy_item.slot_index = 13
          AND NOT EXISTS (
              SELECT 1
              FROM public.character_items AS target_item
              WHERE target_item.user_id = legacy_item.user_id
                AND target_item.item_location = 0
                AND target_item.slot_index = 12
          )
          AND NOT EXISTS (
              SELECT 1
              FROM public.character_inventory_baseline_items
                  AS target_baseline
              WHERE target_baseline.character_id = legacy_item.user_id
                AND target_baseline.item_location = 0
                AND target_baseline.slot_index = 12
          )
          AND NOT EXISTS (
              SELECT 1
              FROM public.character_inventory_ledger AS ledger_item
              WHERE ledger_item.character_id = legacy_item.user_id
                AND ledger_item.item_instance_id = legacy_item.id
          );

        ALTER TABLE public.character_inventory_baseline_items
            DISABLE TRIGGER
                trg_character_inventory_baseline_items_immutable;

        UPDATE public.character_inventory_baseline_items AS baseline_item
        SET slot_index = 12,
            item_state = jsonb_set(
                baseline_item.item_state,
                '{slot_index}',
                to_jsonb(12::smallint),
                false
            )
        FROM fashion_slot_repair_candidates AS candidate
        WHERE baseline_item.character_id = candidate.character_id
          AND baseline_item.item_instance_id = candidate.item_instance_id;

        ALTER TABLE public.character_inventory_baseline_items
            ENABLE TRIGGER
                trg_character_inventory_baseline_items_immutable;

        UPDATE public.character_items AS legacy_item
        SET slot_index = 12
        FROM fashion_slot_repair_candidates AS candidate
        WHERE legacy_item.user_id = candidate.character_id
          AND legacy_item.id = candidate.item_instance_id;

        CREATE OR REPLACE VIEW character_rank_summary AS
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
            FROM character_equipment_scores
            GROUP BY user_id
        )
        SELECT
            cb.id AS user_id,
            cb.name,
            COALESCE(t.weapon_score, 0) AS weapon_score,
            COALESCE(wr.rank_level, 0)::smallint AS weapon_rank,
            COALESCE(wr.aura_effect, 0) AS weapon_aura_effect,
            COALESCE(t.armor_score, 0) AS armor_score,
            COALESCE(ar.rank_level, 0)::smallint AS armor_rank,
            COALESCE(ar.aura_effect, 0) AS armor_aura_effect
        FROM character_base cb
        LEFT JOIN totals t ON t.user_id = cb.id
        LEFT JOIN LATERAL (
            SELECT rank_level, aura_effect
            FROM equipment_rank_rules
            WHERE rank_kind = 'weapon'
              AND required_score <= COALESCE(t.weapon_score, 0)
            ORDER BY rank_level DESC
            LIMIT 1
        ) wr ON true
        LEFT JOIN LATERAL (
            SELECT rank_level, aura_effect
            FROM equipment_rank_rules
            WHERE rank_kind = 'armor'
              AND required_score <= COALESCE(t.armor_score, 0)
            ORDER BY rank_level DESC
            LIMIT 1
        ) ar ON true;
        """);
}
