using Godswar.Server.Application.Characters;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal bool IsCurrentWorldOwnership(
        ClientSession session,
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership)
    {
        if (!ownership.IsValid ||
            !IsCurrentAccountSession(
                accountId,
                session,
                ownership))
        {
            return false;
        }

        lock (_gate)
        {
            return _sessions.TryGetValue(session, out var context) &&
                context.AccountId == accountId &&
                context.CharacterId == characterId &&
                context.Ownership == ownership;
        }
    }

    internal bool TryGetCurrentWorldOwnership(
        ClientSession session,
        int accountId,
        int characterId,
        out PlayerOwnershipFence ownership)
    {
        ownership = default;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) ||
                context.AccountId != accountId ||
                context.CharacterId != characterId ||
                !context.Ownership.IsValid)
            {
                return false;
            }

            ownership = context.Ownership;
        }

        return IsCurrentAccountSession(
            accountId,
            session,
            ownership);
    }

    private static PlayerOwnershipFence PlayerOwnership(
        GameCharacter character) =>
        new(
            character.CheckpointOwnerId,
            character.CheckpointOwnerGeneration);
}
