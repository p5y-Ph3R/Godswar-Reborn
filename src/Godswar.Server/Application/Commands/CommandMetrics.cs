using System.Diagnostics;
using System.Diagnostics.Metrics;

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
}
