using Godswar.Server.Application.Characters;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class GameHandlerOwnershipTestFences
{
    public static GameSessionRegistry CreateRegistry(
        ClientSession session,
        int accountId,
        GameCharacter character)
    {
        var registry = new GameSessionRegistry();
        Bind(registry, session, accountId, character);
        return registry;
    }

    public static PlayerOwnershipFence Bind(
        GameSessionRegistry registry,
        ClientSession session,
        int accountId,
        GameCharacter character)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);

        var ownership =
            PlayerOwnershipTestFences.ForCharacter(character.Id);
        return Bind(
            registry,
            session,
            accountId,
            character,
            ownership);
    }

    public static PlayerOwnershipFence Bind(
        GameSessionRegistry registry,
        ClientSession session,
        int accountId,
        GameCharacter character,
        PlayerOwnershipFence ownership)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ownership.Validate();

        character.CheckpointOwnerId = ownership.OwnerId;
        character.CheckpointOwnerGeneration = ownership.Generation;
        registry.ReplaceAccountSession(accountId, session);
        Check.True(
            registry.TryBindAccountSessionOwnership(
                accountId,
                session,
                ownership),
            "handler fixture binds current player ownership");
        return ownership;
    }
}
