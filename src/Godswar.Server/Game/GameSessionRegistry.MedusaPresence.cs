using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal bool IsSessionInMedusaInstance(ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) ||
                context.MapId is not (200 or 204) ||
                context.Character.CurrentMap != context.MapId ||
                !TryGetWorldInstance(context, out var runtime))
            {
                return false;
            }

            return InvokeWorldOwner(
                runtime,
                static map => map.HasBoundMedusaEncounter());
        }
    }
}
