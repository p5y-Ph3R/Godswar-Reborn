using System.Collections.Frozen;

namespace Godswar.Server.State;

internal enum PetItemPurpose
{
    ShedCapacity,
    Food,
    AmityRecovery,
    Capture,
    Lifetime,
    PetMerge,
    Rebirth,
    SoulContract,
    Growth,
    SavvyReset,
    GrowthReset,
    SkillSlot,
    SkillUnlock,
    SkillRemoval,
    LegacyTalentArtifact,
    Experience,
    Seal,
    Summon,
    OwnerMerge,
    Gender,
    SpeciesChange,
    SkillBook
}

internal sealed record PetItemDefinition(
    uint ItemId,
    string DisplayName,
    PetItemPurpose Purpose,
    bool Restricted = false);

internal sealed record PetItemRange(
    uint FirstItemId,
    uint LastItemId,
    string DisplayName,
    PetItemPurpose Purpose)
{
    public bool Contains(uint itemId) =>
        itemId >= FirstItemId && itemId <= LastItemId;
}

/// <summary>
/// Stable item IDs transcribed from the installed client's
/// ItemBaseAttribute.xml and EquipName.dat. This catalog identifies purpose;
/// it does not make client quantities or operation outcomes authoritative.
/// </summary>
internal static class PetItemCatalog
{
    public const uint SpecialPetShed = 4109;
    public const uint FusedHarpyia = 10097;
    public const uint RebornHarpyia = 10098;
    public const uint PetEnhanceSpring = 10099;
    public const uint GoldenAppleJuice = 10100;
    public const uint StrongPurgePotion = 10101;
    public const uint WeakPurgePotion = 10102;
    public const uint MergedSpirit = 10103;
    public const uint RebirthSpirit = 10104;
    public const uint ContractSpirit = 10105;
    public const uint PixieTear = 10106;
    public const uint SpringWater = 10107;
    public const uint JuiceOfRebirth = 10145;
    public const uint RestrictedJuiceOfRebirth = 10146;
    public const uint EmptySealJade = 10108;
    public const uint PackedSealJade = 10109;
    public const uint RandomEventTalentStick = 10110;
    public const uint QuestDispatchTalentStick = 10111;
    public const uint WorkTalentStick = 10112;
    public const uint HealingTalentStick = 10113;
    public const uint MergeTalentStick = 10114;
    public const uint FairyFeather = 11000;
    public const uint PetCallCharm = 11003;
    public const uint MergeCharm = 11004;
    public const uint PhoenixFeather = 11005;
    public const uint RestrictedSpringWater = 11010;
    public const uint GenderReverser = 11015;
    public const uint AmbrosiaOfRebirth = 11095;

    public static IReadOnlyList<PetItemDefinition> Core { get; } =
        Array.AsReadOnly(
        new PetItemDefinition[]
        {
            D(SpecialPetShed, "Special Pet Shed", PetItemPurpose.ShedCapacity),
            D(10090, "Effective Water", PetItemPurpose.Lifetime),
            D(FusedHarpyia, "Fused Harpyia", PetItemPurpose.PetMerge, true),
            D(RebornHarpyia, "Reborn Harpyia", PetItemPurpose.Rebirth, true),
            D(PetEnhanceSpring, "Pet Enhance Spring", PetItemPurpose.SkillSlot),
            D(GoldenAppleJuice, "Golden Apple Juice", PetItemPurpose.SkillUnlock),
            D(StrongPurgePotion, "Strong Purge Potion", PetItemPurpose.SkillRemoval),
            D(WeakPurgePotion, "Weak Purge Potion", PetItemPurpose.SkillRemoval),
            D(MergedSpirit, "Merged Spirit", PetItemPurpose.PetMerge),
            D(RebirthSpirit, "Rebirth Spirit", PetItemPurpose.Rebirth),
            D(ContractSpirit, "Contract Spirit", PetItemPurpose.SoulContract),
            D(PixieTear, "Pixie Tear", PetItemPurpose.Growth),
            D(SpringWater, "Spring Water", PetItemPurpose.Rebirth),
            D(EmptySealJade, "Seal Jade (Empty)", PetItemPurpose.Seal),
            D(PackedSealJade, "Seal Jade (Packed)", PetItemPurpose.Seal),
            D(RandomEventTalentStick, "Stick: Random Event", PetItemPurpose.LegacyTalentArtifact),
            D(QuestDispatchTalentStick, "Stick: Quest Dispatch", PetItemPurpose.LegacyTalentArtifact),
            D(WorkTalentStick, "Stick: Work", PetItemPurpose.LegacyTalentArtifact),
            D(HealingTalentStick, "Stick: Healing", PetItemPurpose.LegacyTalentArtifact),
            D(MergeTalentStick, "Stick: Merge", PetItemPurpose.LegacyTalentArtifact),
            D(JuiceOfRebirth, "Juice of Rebirth", PetItemPurpose.Rebirth),
            D(RestrictedJuiceOfRebirth, "Juice of Rebirth (Limited)", PetItemPurpose.Rebirth, true),
            D(FairyFeather, "Fairy's Feather", PetItemPurpose.SavvyReset),
            D(PetCallCharm, "Charm: Pet Call", PetItemPurpose.Summon),
            D(MergeCharm, "Charm: Merge", PetItemPurpose.OwnerMerge),
            D(PhoenixFeather, "Phoenix's Feather", PetItemPurpose.GrowthReset),
            D(RestrictedSpringWater, "Spring Water (Restricted)", PetItemPurpose.Rebirth, true),
            D(GenderReverser, "Pet Gender Reverser", PetItemPurpose.Gender),
            D(AmbrosiaOfRebirth, "Ambrosia of Rebirth", PetItemPurpose.Rebirth)
        });

    public static IReadOnlyList<PetItemRange> Ranges { get; } =
        Array.AsReadOnly(
        new PetItemRange[]
        {
            R(10000, 10003, "Herbivore food", PetItemPurpose.Food),
            R(10020, 10023, "Carnivore food", PetItemPurpose.Food),
            R(10040, 10043, "Omnivore food", PetItemPurpose.Food),
            R(10060, 10061, "Pet wine", PetItemPurpose.AmityRecovery),
            R(10080, 10084, "Capture tools", PetItemPurpose.Capture),
            R(10130, 10134, "Morning Dew", PetItemPurpose.Experience),
            R(10140, 10144, "Restricted Morning Dew", PetItemPurpose.Experience),
            R(10200, 10745, "Pet skill books", PetItemPurpose.SkillBook),
            R(11050, 11094, "Magic Jade", PetItemPurpose.SpeciesChange)
        });

    private static readonly FrozenDictionary<uint, PetItemDefinition> CoreById =
        Core.ToFrozenDictionary(static definition => definition.ItemId);

    public static bool TryGetCore(uint itemId, out PetItemDefinition definition) =>
        CoreById.TryGetValue(itemId, out definition!);

    public static PetItemRange? FindRange(uint itemId) =>
        Ranges.FirstOrDefault(range => range.Contains(itemId));

    private static PetItemDefinition D(
        uint id,
        string name,
        PetItemPurpose purpose,
        bool restricted = false) =>
        new(id, name, purpose, restricted);

    private static PetItemRange R(
        uint first,
        uint last,
        string name,
        PetItemPurpose purpose) =>
        new(first, last, name, purpose);
}
