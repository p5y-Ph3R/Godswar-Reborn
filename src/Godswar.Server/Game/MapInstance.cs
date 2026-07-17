using System.Collections.Concurrent;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed class MapInstance
{
    private readonly ConcurrentDictionary<ClientSession, GameSessionContext> _sessions = [];

    public MapInstance(byte mapId)
    {
        MapId = mapId;
    }

    public byte MapId { get; }

    public int Population => _sessions.Count;

    public void AddOrUpdate(GameSessionContext context)
    {
        _sessions[context.Session] = context;
    }

    public bool Remove(ClientSession session, out GameSessionContext? context)
    {
        return _sessions.TryRemove(session, out context);
    }

    public IReadOnlyList<GameSessionContext> Snapshot()
    {
        return _sessions.Values.ToArray();
    }
}
