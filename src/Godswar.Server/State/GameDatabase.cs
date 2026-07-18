namespace Godswar.Server.State;

internal sealed class GameDatabase
{
    public int NextAccountId { get; set; } = 1;

    public int NextCharacterId { get; set; } = 1;

    public List<GameAccount> Accounts { get; set; } = [];

    public List<GameCharacter> Characters { get; set; } = [];

    public List<GameCharacterTalent> CharacterTalents { get; set; } = [];
}

internal sealed class GameCharacterTalent
{
    public int CharacterId { get; set; }

    public int TalentId { get; set; }

    public int Rank { get; set; }
}
