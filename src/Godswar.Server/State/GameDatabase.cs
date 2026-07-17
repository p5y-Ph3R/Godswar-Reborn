namespace Godswar.Server.State;

internal sealed class GameDatabase
{
    public int NextAccountId { get; set; } = 1;

    public int NextCharacterId { get; set; } = 1;

    public List<GameAccount> Accounts { get; set; } = [];

    public List<GameCharacter> Characters { get; set; } = [];
}
