using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal bool EnsurePlayerStatusState(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return TryGetOrCreatePlayerStatusState(session, out _);
    }
}
