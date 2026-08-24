using System.Diagnostics;
using System.Diagnostics.Metrics;
using Godswar.Server.Operations.Observability;

namespace Godswar.Server.Application.Commands;

internal enum CommandOutcome : byte
{
    Accepted = 1,
    Malformed = 2,
    InvalidIntent = 3,
    PreconditionFailed = 4,
    Duplicate = 5,
    RequestHashConflict = 6,
    ProviderUnavailable = 7,
    Cancelled = 8
}

internal static class CommandMetrics
{
    public const string MeterName =
        "Godswar.Server.Application.Commands";
    public const string CommandInstrumentName =
        "godswar_commands_total";
    public const string UnsupportedIdentityInstrumentName =
        "godswar_legacy_commands_without_retry_identity_total";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Commands =
        Meter.CreateCounter<long>(
            CommandInstrumentName,
            description:
            "Completed application commands by bounded family, identity, and outcome.");
    private static readonly Counter<long> UnsupportedLegacyIdentity =
        Meter.CreateCounter<long>(
            UnsupportedIdentityInstrumentName,
            description:
            "Legacy command attempts whose family cannot provide stable retry identity.");

    public static void Record(
        CommandFamily family,
        CommandIdentityStrength identityStrength,
        CommandOutcome outcome)
    {
        var tags = new TagList
        {
            { "family", FamilyCode(family) },
            { "identity_strength", IdentityCode(identityStrength) },
            { "outcome", OutcomeCode(outcome) }
        };
        Commands.Add(1, tags);
        ServerActivity.RecordCompleted(
            ServerTraceOperation.ApplicationCommand,
            TimeSpan.Zero,
            TraceOutcome(outcome),
            ActivityKind.Internal,
            ServerTraceAttribute.FromCode(
                ServerTraceTag.CommandFamily,
                FamilyCode(family)),
            ServerTraceAttribute.FromBoolean(
                ServerTraceTag.Duplicate,
                outcome == CommandOutcome.Duplicate));
    }

    public static void RecordUnsupportedLegacyIdentity(
        CommandFamily family)
    {
        var tags = new TagList
        {
            { "family", FamilyCode(family) }
        };
        UnsupportedLegacyIdentity.Add(1, tags);
    }

    internal static string FamilyCode(CommandFamily family) =>
        family switch
        {
            CommandFamily.TalentUpgrade => "talent_upgrade",
            CommandFamily.PetLevelUpgrade => "pet_level_upgrade",
            CommandFamily.EquipmentForge => "equipment_forge",
            CommandFamily.DeveloperItemGrant => "developer_item_grant",
            CommandFamily.DeveloperBagClear => "developer_bag_clear",
            CommandFamily.GearMentorMakeAttributeStone =>
                "gear_mentor_make_attribute_stone",
            CommandFamily.GearMentorTransformCrystal =>
                "gear_mentor_transform_crystal",
            CommandFamily.GearMentorCombineGemPieces =>
                "gear_mentor_combine_gem_pieces",
            CommandFamily.GearMentorDecomposeGear =>
                "gear_mentor_decompose_gear",
            CommandFamily.GearMentorEnhanceAttribute =>
                "gear_mentor_enhance_attribute",
            CommandFamily.GearMentorAddAttribute =>
                "gear_mentor_add_attribute",
            CommandFamily.GearMentorDeleteAttribute =>
                "gear_mentor_delete_attribute",
            CommandFamily.KitBagItemDelete =>
                "kit_bag_item_delete",
            CommandFamily.KitBagItemMove =>
                "kit_bag_item_move",
            CommandFamily.EquipmentBagTransfer =>
                "equipment_bag_transfer",
            CommandFamily.HolyStoneMount =>
                "holy_stone_mount",
            CommandFamily.HolyStoneRemove =>
                "holy_stone_remove",
            CommandFamily.HolyStoneDrill =>
                "holy_stone_drill",
            CommandFamily.HolyStoneAdvancedDrill =>
                "holy_stone_advanced_drill",
            CommandFamily.HolyStoneUpgrade =>
                "holy_stone_upgrade",
            CommandFamily.HolyStoneCombine =>
                "holy_stone_combine",
            CommandFamily.HolyStoneImplementSpirit =>
                "holy_spirit_implement",
            CommandFamily.MountGearDrill =>
                "mount_gear_drill",
            CommandFamily.ZodiacSkillGridActivation =>
                "zodiac_skill_grid_activation",
            CommandFamily.ZodiacSkillGridUpgrade =>
                "zodiac_skill_grid_upgrade",
            CommandFamily.ZodiacSkillGridSelection =>
                "zodiac_skill_grid_selection",
            CommandFamily.CharacterCreate =>
                "character_create",
            CommandFamily.CharacterDelete =>
                "character_delete",
            CommandFamily.CharacterRestore =>
                "character_restore",
            CommandFamily.CharacterPurge =>
                "character_purge",
            CommandFamily.BagItemActivation =>
                "bag_item_activation",
            CommandFamily.PetPresenceTransition =>
                "pet_presence_transition",
            CommandFamily.PetSkillUnlearn =>
                "pet_skill_unlearn",
            CommandFamily.PetGrowthReset =>
                "pet_growth_reset",
            CommandFamily.PetBasicSavvyReset =>
                "pet_basic_savvy_reset",
            CommandFamily.PetOwnerMergeToggle =>
                "pet_owner_merge_toggle",
            CommandFamily.PetToPetMerge =>
                "pet_to_pet_merge",
            CommandFamily.PetRebirth =>
                "pet_rebirth",
            CommandFamily.PetAppearanceChange =>
                "pet_appearance_change",
            CommandFamily.PetBind =>
                "pet_bind",
            CommandFamily.PetSoulContract =>
                "pet_soul_contract",
            CommandFamily.PetManagerUtility =>
                "pet_manager_utility",
            CommandFamily.WarehouseTransfer =>
                "warehouse_transfer",
            CommandFamily.WarehouseExpansion =>
                "warehouse_expansion",
            CommandFamily.MonsterRewardSettlement =>
                "monster_reward_settlement",
            CommandFamily.ProgressionIntervalSettlement =>
                "progression_interval_settlement",
            CommandFamily.HolySuitStoreExperience =>
                "holy_suit_store_experience",
            CommandFamily.HolySuitTransferExperience =>
                "holy_suit_transfer_experience",
            CommandFamily.HolySuitConsumeWare =>
                "holy_suit_consume_ware",
            CommandFamily.HolySuitTransformExperience =>
                "holy_suit_transform_experience",
            CommandFamily.ClassSuitExchangeTierI =>
                "class_suit_exchange_tier_i",
            CommandFamily.ClassSuitConvertToCommon =>
                "class_suit_convert_to_common",
            CommandFamily.ClassSuitUpgradeTierII =>
                "class_suit_upgrade_tier_ii",
            CommandFamily.ClassSuitUpgradeTierIII =>
                "class_suit_upgrade_tier_iii",
            CommandFamily.ClassSuitUpgradeTierIV =>
                "class_suit_upgrade_tier_iv",
            CommandFamily.ClassSuitAddAttribute =>
                "class_suit_add_attribute",
            CommandFamily.ClassSuitDeleteAttribute =>
                "class_suit_delete_attribute",
            _ => throw new ArgumentOutOfRangeException(nameof(family))
        };

    internal static string IdentityCode(
        CommandIdentityStrength strength) =>
        strength switch
        {
            CommandIdentityStrength.LegacyAggregateVersion =>
                "legacy_aggregate_version",
            CommandIdentityStrength.ClientOperationId =>
                "client_operation_id",
            CommandIdentityStrength.UnsupportedLegacyRetry =>
                "unsupported_legacy_retry",
            CommandIdentityStrength.ServerOperationId =>
                "server_operation_id",
            _ => throw new ArgumentOutOfRangeException(nameof(strength))
        };

    internal static string OutcomeCode(CommandOutcome outcome) =>
        outcome switch
        {
            CommandOutcome.Accepted => "accepted",
            CommandOutcome.Malformed => "malformed",
            CommandOutcome.InvalidIntent => "invalid_intent",
            CommandOutcome.PreconditionFailed => "precondition_failed",
            CommandOutcome.Duplicate => "duplicate",
            CommandOutcome.RequestHashConflict => "request_hash_conflict",
            CommandOutcome.ProviderUnavailable => "provider_unavailable",
            CommandOutcome.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

    private static ServerTraceOutcome TraceOutcome(
        CommandOutcome outcome) =>
        outcome switch
        {
            CommandOutcome.Accepted => ServerTraceOutcome.Accepted,
            CommandOutcome.Duplicate => ServerTraceOutcome.Duplicate,
            CommandOutcome.Cancelled => ServerTraceOutcome.Cancelled,
            CommandOutcome.ProviderUnavailable =>
                ServerTraceOutcome.Faulted,
            _ => ServerTraceOutcome.Rejected
        };
}
