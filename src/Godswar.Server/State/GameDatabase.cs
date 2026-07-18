namespace Godswar.Server.State;

internal sealed class GameDatabase
{
    public int NextAccountId { get; set; } = 1;

    public int NextCharacterId { get; set; } = 1;

    public List<GameAccount> Accounts { get; set; } = [];

    public List<GameCharacter> Characters { get; set; } = [];

    public List<GameCharacterTalent> CharacterTalents { get; set; } = [];

    public List<CharacterExperienceBoost> CharacterExperienceBoosts { get; set; } = [];

    public List<FactionAreaExperienceControl> FactionAreaExperienceControls { get; set; } = [];
}

internal sealed class GameCharacterTalent
{
    public int CharacterId { get; set; }

    public int TalentId { get; set; }

    public int Rank { get; set; }
}

internal sealed class CharacterExperienceBoost
{
    public int CharacterId { get; set; }

    public int StatusId { get; set; }

    public int Kind { get; set; }

    public int BonusBasisPoints { get; set; }

    public int Priority { get; set; }

    public DateTimeOffset ActivatedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public string Source { get; set; } = string.Empty;
}

internal sealed class FactionAreaExperienceControl
{
    public byte MapId { get; set; }

    public byte ControllingCamp { get; set; }

    public string BossTemplateKey { get; set; } = string.Empty;

    public string DeathToken { get; set; } = string.Empty;

    public int BonusBasisPoints { get; set; } = 2_500;

    public DateTimeOffset ActivatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}
