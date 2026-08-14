using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMigrationFoundationChecks
{
    private static void CheckForwardOnlyCatalog()
    {
        Check.Equal(
            94,
            PostgresSchemaMigrationCatalog.All.Count,
            "migration catalog entry count");
        var baseline = PostgresSchemaMigrationCatalog.All[0];
        Check.Equal(
            "20260723_000_legacy_schema_baseline",
            baseline.Id,
            "legacy database receives one explicit metadata baseline");
        Check.True(
            !baseline.Sql.Contains(
                "050_test_character",
                StringComparison.OrdinalIgnoreCase) &&
            !baseline.Sql.Contains(
                "character_base",
                StringComparison.OrdinalIgnoreCase) &&
            !baseline.Sql.Contains("UPDATE ", StringComparison.OrdinalIgnoreCase),
            "baseline cannot replay legacy bootstrap or test-character mutations");
        Check.Throws<ArgumentException>(
            () => new PostgresSchemaMigration(
                "050_test_character_fixture",
                "legacy fixture",
                "SELECT 1;"),
            "legacy numbered script IDs cannot enter the forward-only catalog");
        Check.True(
            PostgresSchemaMigrationCatalog.All
                .Select(static migration => migration.Id)
                .SequenceEqual(ExpectedMigrationIds),
            "explicit migration catalog remains ordered and complete");
        Check.True(
            PostgresSchemaMigrationCatalog.All.All(migration =>
                !migration.Sql.Contains(
                    "test_character",
                    StringComparison.OrdinalIgnoreCase)),
            "production migration catalog cannot contain local character fixtures");
        var indexCleanup = PostgresSchemaMigrationCatalog.All.Single(
            migration =>
                migration.Id == "20260723_004_remove_redundant_indexes");
        Check.True(
            indexCleanup.Sql.Contains(
                "UNIQUE USING INDEX ux_accounts_username",
                StringComparison.Ordinal) &&
            indexCleanup.Sql.Contains(
                "WHERE conindid = username_index",
                StringComparison.Ordinal),
            "fresh and existing databases retain an authoritative username uniqueness constraint");
        var petEvidenceV3 = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260811_077_pet_durable_evidence_v3");
        Check.True(
            petEvidenceV3.Sql.Contains(
                "'pet_to_pet_merge'",
                StringComparison.Ordinal) &&
            petEvidenceV3.Sql.Contains(
                "'pet_rebirth'",
                StringComparison.Ordinal),
            "pet durable evidence exposes native Merge and Rebirth commands");
        var monsterCombat = PostgresSchemaMigrationCatalog.All.Single(
            migration => migration.Id ==
                "20260814_093_monster_combat_authority");
        Check.Equal(
            "054B080ABAEEFCC3E88085CF5CF3C288D34E08E86BE3D90890C880D31F0E7803",
            monsterCombat.Checksum,
            "monster-combat authority migration checksum is pinned");
        Check.True(
            monsterCombat.Sql.Contains(
                "ADD COLUMN attack_type smallint",
                StringComparison.Ordinal) &&
            monsterCombat.Sql.Contains(
                "attack_type IN (1, 2, 3)",
                StringComparison.Ordinal) &&
            monsterCombat.Sql.Contains(
                "ADD COLUMN map_mode smallint",
                StringComparison.Ordinal) &&
            monsterCombat.Sql.Contains(
                "map_mode BETWEEN 0 AND 5",
                StringComparison.Ordinal),
            "sealed gameplay content retains constrained monster attack and map-mode PvP authority");
    }

    private static readonly string[] ExpectedMigrationIds =
    [
        "20260723_000_legacy_schema_baseline",
        "20260723_001_mount_ride_compatibility",
        "20260723_002_mount_rank_guard",
        "20260723_003_erebus_lion_mount",
        "20260723_004_remove_redundant_indexes",
        "20260723_005_starter_consumable_templates",
        "20260723_006_archive_legacy_character_kitbag",
        "20260723_007_character_item_template_foreign_key",
        "20260723_008_zodiac_skill_grid_state",
        "20260728_009_skill_cast_interrupt_opcode",
        "20260728_010_pet_foundation",
        "20260728_011_pet_aptitude_range",
        "20260728_012_pet_aptitude_catalog",
        "20260728_013_owned_pet_bootstrap_opcode",
        "20260728_014_pet_presence_protocol",
        "20260728_015_pet_presence_audit_operation",
        "20260728_016_pet_growth_policy",
        "20260728_017_pet_growth_midpoint_backfill",
        "20260728_018_pet_growth_policy_v2",
        "20260728_019_pet_initial_savvy_policy",
        "20260729_020_pet_savvy_semantics",
        "20260729_021_pet_savvy_semantics_hardening",
        "20260729_022_pet_level_progression",
        "20260729_023_npc_content_release",
        "20260729_024_npc_dialogue_content_release",
        "20260729_025_command_inbox_outbox_foundation",
        "20260729_026_command_inbox_outbox_hardening",
        "20260729_027_economy_ledger_foundation",
        "20260729_028_economy_ledger_hardening",
        "20260730_029_holy_stone_material_templates",
        "20260730_030_character_checkpoint_versions",
        "20260730_031_character_lifecycle_foundation",
        "20260731_032_progression_reward_foundation",
        "20260731_033_progression_interval_authority",
        "20260731_034_pet_durability_foundation",
        "20260731_035_tempest_realm_authority",
        "20260801_036_monster_content_release",
        "20260801_037_enter_bootstrap_content_release",
        "20260801_038_item_template_content_release",
        "20260801_039_gameplay_content_release",
        "20260801_040_item_runtime_projection_cutover",
        "20260801_041_item_policy_content_release",
        "20260801_042_pet_content_release",
        "20260801_043_item_content_header_seal_guard",
        "20260801_044_item_material_content_release",
        "20260801_045_item_material_recipe_content_release",
        "20260801_046_holy_suit_content_release",
        "20260801_047_fighter_level_seal",
        "20260801_048_fighter_experience_uint32",
        "20260802_049_holy_suit_singapore_day_boundary",
        "20260802_050_holy_suit_fixed_daily_cap",
        "20260802_051_npc_dialogue_multi_route",
        "20260802_052_class_suit_item_content",
        "20260803_053_class_suit_attribute_slots",
        "20260803_054_elemental_class_suit_attributes",
        "20260803_055_elemental_stone_icon_content",
        "20260803_056_canonical_elemental_stone_content",
        "20260804_057_socket_spell_item_templates",
        "20260804_058_stock_holy_stone_material_templates",
        "20260805_059_holy_spirit_effectiveness_values",
        "20260810_060_fashion_slot_consistency",
        "20260810_061_pet_skills_and_talents",
        "20260810_062_pet_manager_dialogue",
        "20260810_063_fashion_rank_projection_repair",
        "20260810_064_zephyr_holy_stone_material_templates",
        "20260810_065_pet_shed_capacity",
        "20260810_066_pet_skill_cell_protocol",
        "20260810_067_pet_skill_cell_protocol_correction",
        "20260810_068_pet_point_reset_dialogue",
        "20260810_069_pet_growth_savvy_semantics_v2",
        "20260811_070_pet_initial_savvy_policy_v3",
        "20260811_071_pet_phoenix_growth_activation",
        "20260811_072_pet_quality_innate_talents",
        "20260811_073_pet_owner_merge_content",
        "20260811_074_pet_merge_balance_content",
        "20260811_075_pet_durable_evidence_v2",
        "20260811_076_pet_consumable_mutable_projection",
        "20260811_077_pet_durable_evidence_v3",
        "20260811_078_pet_scaled_added_value_v3",
        "20260812_079_pet_growth_preview",
        "20260812_080_pet_basic_savvy_preview",
        "20260812_081_pet_rank_content",
        "20260812_082_pet_hatch_evidence_hardening",
        "20260812_083_pet_learned_skill_content",
        "20260812_084_pet_merge_savvy_lookup_content",
        "20260812_085_pet_magic_jade_appearance_groups",
        "20260812_086_pet_appearance_change",
        "20260812_087_pet_bind",
        "20260813_088_pet_soul_contract",
        "20260813_089_pet_manager_utility",
        "20260813_090_bag_consumable_cooldown_state",
        "20260813_091_pet_phoenix_rebirth_bracket",
        "20260814_092_packed_seal_ownership_hardening",
        "20260814_093_monster_combat_authority"
    ];
}
