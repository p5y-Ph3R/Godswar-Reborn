using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed record GameSessionContext(
    ClientSession Session,
    int AccountId,
    int CharacterId,
    string CharacterName,
    byte MapId,
    uint ObjectId,
    GameCharacter Character)
{
    public string DisplayName => string.IsNullOrWhiteSpace(CharacterName)
        ? $"character:{CharacterId}"
        : CharacterName;
}
