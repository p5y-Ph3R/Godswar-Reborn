using System.Text.Json.Serialization;

namespace Godswar.Server.State;

internal sealed class GameCharacter
{
    public int Id { get; set; }

    public int AccountId { get; set; }

    public string Name { get; set; } = string.Empty;

    public byte Gender { get; set; }

    public byte Camp { get; set; } = GameDefaults.AthensCamp;

    public byte Profession { get; set; }

    public byte Hair { get; set; }

    public byte Face { get; set; }

    public byte Faith { get; set; } = 1;

    public byte CurrentMap { get; set; } = GameDefaults.AthensCapitalMap;

    public int Level { get; set; } = 1;

    public int Experience { get; set; }

    public int MaxHp { get; set; } = 1500;

    public int MaxMp { get; set; } = 177;

    public int CurrentHp { get; set; } = 1500;

    public int CurrentMp { get; set; } = 177;

    public int TalentPoints { get; set; } = 10;

    public int TalentExperience { get; set; }

    public int HolySuitPoints { get; set; }

    public short WeaponRank { get; set; }

    public int WeaponAuraEffect { get; set; }

    public short ArmorRank { get; set; }

    public int ArmorAuraEffect { get; set; }

    public float PositionX { get; set; } = GameDefaults.StartingPositionX;

    public float PositionZ { get; set; } = GameDefaults.StartingPositionZ;

    public string Equipment { get; set; } = string.Empty;

    public string KitBag { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public CharacterStats? CalculatedStats { get; set; }
}
