using System.Text.Json.Serialization;

namespace Godswar.Server.State;

internal sealed class GameCharacter
{
    private string _equipment = string.Empty;

    [JsonIgnore]
    internal object VitalsSync { get; } = new();

    [JsonIgnore]
    internal object ZodiacSync { get; } = new();

    public int Id { get; set; }

    public int AccountId { get; set; }

    public short CharacterSlot { get; set; } =
        CharacterLifecyclePolicy.SingleCharacterSlot;

    public CharacterLifecycleState LifecycleState { get; set; } =
        CharacterLifecycleState.Active;

    public long LifecycleVersion { get; set; } = 1;

    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset? RestoreUntil { get; set; }

    public DateTimeOffset? PurgeAfter { get; set; }

    public string Name { get; set; } = string.Empty;

    public byte Gender { get; set; }

    public byte Camp { get; set; } = GameDefaults.AthensCamp;

    public byte Profession { get; set; }

    public byte Hair { get; set; }

    public byte Face { get; set; }

    public byte Faith { get; set; } = 1;

    public byte ZodiacType { get; set; }

    public int ZodiacLuckyStatus { get; set; }

    public DateTimeOffset? ZodiacLuckyExpiresAt { get; set; }

    public byte ZodiacLevel { get; set; } = 1;

    public int ZodiacEnergy { get; set; }

    public int ZodiacEnergyRemainderX100 { get; set; }

    public DateOnly? ZodiacOnlineDay { get; set; }

    public long ZodiacOnlineDurationTicksToday { get; set; }

    public DateTimeOffset? ZodiacLastOnlineAt { get; set; }

    public DateOnly? ZodiacLastCompensationDay { get; set; }

    public int ZodiacAccumulatedExperienceX100 { get; set; }

    public int ZodiacAccumulatedTalentExperienceX100 { get; set; }

    public int[] ZodiacSkillGridLevels { get; set; } =
        ZodiacSkillGridCatalog.CreateEmptyLevels();

    public int[] ZodiacSkillGridSkillIds { get; set; } =
        ZodiacSkillGridCatalog.CreateEmptySkillIds();

    public byte CurrentMap { get; set; } = GameDefaults.AthensCapitalMap;

    public int Level { get; set; } = 1;

    public long Experience { get; set; }

    public bool FighterLevelSealed { get; set; }

    public int Silver { get; set; } = 10_000;

    // The legacy database calls premium gold "Stone". Keep the game-facing
    // name here and translate it at the PostgreSQL boundary.
    public int Gold { get; set; } = 10;

    public int MaxHp { get; set; } = 1500;

    public int MaxMp { get; set; } = 177;

    public int CurrentHp { get; set; } = 1500;

    public int CurrentMp { get; set; } = 177;

    public long VitalsRevision { get; set; }

    public long PositionRevision { get; set; }

    [JsonIgnore]
    public Guid CheckpointOwnerId { get; set; }

    [JsonIgnore]
    public long CheckpointOwnerGeneration { get; set; }

    public int TalentPoints { get; set; } = 10;

    public int TalentExperience { get; set; }

    public int HolySuitPoints { get; set; }

    public short WeaponRank { get; set; }

    public int WeaponAuraEffect { get; set; }

    public short ArmorRank { get; set; }

    public int ArmorAuraEffect { get; set; }

    // The stock client owns this preference in BagSet.xml and resends it on
    // login (opcode 10200). It affects only world appearance projection: the
    // equipped Fashion item and all authoritative item state remain intact.
    [JsonIgnore]
    public bool FashionHidden { get; set; }

    // The stock client owns the Fashion Effect preference in BagSet.xml and
    // resends it through opcode 10202. The native renderer uses this one flag
    // for both armor/body and held-weapon aura effects. It is presentation-only
    // and must never change the equipped items, their ranks, or persisted stats.
    [JsonIgnore]
    public bool EquipmentEffectsVisible { get; set; } = true;

    public float PositionX { get; set; } = GameDefaults.StartingPositionX;

    public float PositionZ { get; set; } = GameDefaults.StartingPositionZ;

    public string Equipment
    {
        get => _equipment;
        set
        {
            _equipment = value ?? string.Empty;
            ElementalEquipment = ElementalAttributeCatalog
                .CalculateEquippedProfile(ParseEquipment(_equipment));
        }
    }

    public string KitBag { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public CharacterStats? CalculatedStats { get; set; }

    [JsonIgnore]
    public ElementalEquipmentProfile ElementalEquipment { get; private set; } =
        ElementalAttributeCatalog.CalculateEquippedProfile([]);

    internal long MarkVitalsChanged()
    {
        VitalsRevision = checked(VitalsRevision + 1);
        return VitalsRevision;
    }

    internal long MarkPositionChanged()
    {
        PositionRevision = checked(PositionRevision + 1);
        return PositionRevision;
    }

    private static IEnumerable<ElementalEquippedItem> ParseEquipment(
        string equipment) =>
        equipment.Split('#', StringSplitOptions.None)
            .Take(EquipmentSlots.Shield + 1)
            .Select((entry, slot) => new ElementalEquippedItem(
                slot,
                CompactItemEntry.Parse(entry)));
}
