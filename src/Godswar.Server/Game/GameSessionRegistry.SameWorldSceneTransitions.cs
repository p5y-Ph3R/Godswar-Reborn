using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal bool TryHideForSameWorldSceneTransition(
        ClientSession session,
        PlayerOwnershipFence ownership,
        out WorldInstanceId worldInstanceId)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            worldInstanceId = default;
            if (!_sessions.TryGetValue(session, out var existing) ||
                !existing.WorldReady ||
                existing.Ownership != ownership ||
                !IsCurrentAccountSession(
                    existing.AccountId,
                    session,
                    ownership))
            {
                return false;
            }

            var hidden = existing with
            {
                WorldReady = false,
                WorldRevision = checked(existing.WorldRevision + 1)
            };
            AddToMap(hidden);
            _sessions[session] = hidden;
            worldInstanceId = hidden.WorldInstanceId;
            return true;
        }
    }
}
