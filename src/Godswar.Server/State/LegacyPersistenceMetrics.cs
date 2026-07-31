using System.Diagnostics.Metrics;

namespace Godswar.Server.State;

/// <summary>
/// Finite operation names for the broad persistence paths tracked by the B20
/// retirement gate. Values are server-defined and must never contain player,
/// session, network, or provider-supplied data.
/// </summary>
internal enum LegacyPersistenceOperation : byte
{
    ActivateZodiacSkillGrid = 2,
    AddDeveloperMount = 3,
    AddForgingMaterial = 4,
    ApplyMonsterKillReward = 5,
    ApplyWeaponHolyStone = 6,
    ClearKitBag = 8,
    CreateCharacter = 10,
    DeleteCharacter = 11,
    DeleteKitBagItem = 12,
    EnhanceGear = 13,
    EnsureSeedData = 14,
    ForgeEquipment = 15,
    MoveEquipmentToKitBag = 23,
    MoveKitBagItem = 24,
    MoveKitBagToEquipment = 25,
    ProcessGearMentor = 26,
    SaveCharacterPosition = 27,
    SaveCharacterVitals = 29,
    SelectZodiacSkillGrid = 30,
    UpgradeTalent = 33,
    UpgradeZodiacSkillGrid = 35
}

/// <summary>
/// Records attempted legacy persistence invocations. Callers record before
/// awaiting persistence so failures and cancellations cannot hide usage.
/// </summary>
internal static class LegacyPersistenceMetrics
{
    public const string MeterName =
        "Godswar.Server.State.LegacyPersistence";
    public const string InvocationInstrumentName =
        "godswar_legacy_persistence_invocations_total";
    public const string ObserverReadyInstrumentName =
        "godswar_legacy_persistence_observer_ready";
    public const string OperationTagName = "operation";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly Counter<long> Invocations =
        Meter.CreateCounter<long>(
            InvocationInstrumentName,
            "{invocation}",
            "Attempted broad or concrete legacy persistence calls by " +
            "finite operation.");
    private static readonly ObservableGauge<int> ObserverReady =
        Meter.CreateObservableGauge(
            ObserverReadyInstrumentName,
            static () => 1,
            description:
            "Whether this process initialized the legacy-usage observer.");

    /// <summary>
    /// Forces instrument publication even when no legacy call occurs.
    /// </summary>
    public static void EnsureInitialized()
    {
        GC.KeepAlive(ObserverReady);
    }

    public static void Record(LegacyPersistenceOperation operation)
    {
        var operationCode = ToMetricTag(operation);
        Invocations.Add(
            1,
            new KeyValuePair<string, object?>(
                OperationTagName,
                operationCode));
    }

    internal static string ToMetricTag(
        LegacyPersistenceOperation operation) =>
        operation switch
        {
            LegacyPersistenceOperation.ActivateZodiacSkillGrid =>
                "activate_zodiac_skill_grid",
            LegacyPersistenceOperation.AddDeveloperMount =>
                "add_developer_mount",
            LegacyPersistenceOperation.AddForgingMaterial =>
                "add_forging_material",
            LegacyPersistenceOperation.ApplyMonsterKillReward =>
                "apply_monster_kill_reward",
            LegacyPersistenceOperation.ApplyWeaponHolyStone =>
                "apply_weapon_holy_stone",
            LegacyPersistenceOperation.ClearKitBag => "clear_kit_bag",
            LegacyPersistenceOperation.CreateCharacter =>
                "create_character",
            LegacyPersistenceOperation.DeleteCharacter =>
                "delete_character",
            LegacyPersistenceOperation.DeleteKitBagItem =>
                "delete_kit_bag_item",
            LegacyPersistenceOperation.EnhanceGear => "enhance_gear",
            LegacyPersistenceOperation.EnsureSeedData =>
                "ensure_seed_data",
            LegacyPersistenceOperation.ForgeEquipment =>
                "forge_equipment",
            LegacyPersistenceOperation.MoveEquipmentToKitBag =>
                "move_equipment_to_kit_bag",
            LegacyPersistenceOperation.MoveKitBagItem =>
                "move_kit_bag_item",
            LegacyPersistenceOperation.MoveKitBagToEquipment =>
                "move_kit_bag_to_equipment",
            LegacyPersistenceOperation.ProcessGearMentor =>
                "process_gear_mentor",
            LegacyPersistenceOperation.SaveCharacterPosition =>
                "save_character_position",
            LegacyPersistenceOperation.SaveCharacterVitals =>
                "save_character_vitals",
            LegacyPersistenceOperation.SelectZodiacSkillGrid =>
                "select_zodiac_skill_grid",
            LegacyPersistenceOperation.UpgradeTalent => "upgrade_talent",
            LegacyPersistenceOperation.UpgradeZodiacSkillGrid =>
                "upgrade_zodiac_skill_grid",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
}
