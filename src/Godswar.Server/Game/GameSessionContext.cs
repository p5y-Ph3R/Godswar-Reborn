using Godswar.Server.Application.Characters;
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
    GameCharacter Character,
    bool WorldReady,
    long WorldRevision)
{
    public PlayerOwnershipFence Ownership { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(CharacterName)
        ? $"character:{CharacterId}"
        : CharacterName;
}
