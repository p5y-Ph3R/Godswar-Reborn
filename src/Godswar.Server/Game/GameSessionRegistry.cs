using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed class GameSessionRegistry
{
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<ClientSession, GameSessionContext> _sessions = [];
    private readonly ConcurrentDictionary<int, ClientSession> _accountSessions = [];
    private readonly ConcurrentDictionary<byte, MapInstance> _maps = [];

    public void JoinMap(
        ClientSession session,
        int accountId,
        GameCharacter character,
        uint objectId)
    {
        var context = new GameSessionContext(
            session,
            accountId,
            character.Id,
            character.Name,
            character.CurrentMap,
            objectId,
            character);
        GameSessionContext? previous = null;
        lock (_gate)
        {
            if (_sessions.TryGetValue(session, out previous) && previous.MapId != character.CurrentMap)
            {
                RemoveFromMap(previous);
            }

            _sessions[session] = context;
            AddToMap(context);
        }

        if (previous is null)
        {
            Console.WriteLine($"[world] joined map={context.MapId} character={context.DisplayName} object={context.ObjectId} account={accountId} population={GetMapPopulation(context.MapId)}");
        }
        else if (previous.MapId != context.MapId)
        {
            Console.WriteLine($"[world] moved map={previous.MapId}->{context.MapId} character={context.DisplayName} object={context.ObjectId} account={accountId} population={GetMapPopulation(context.MapId)}");
        }
    }

    public void Remove(ClientSession session)
    {
        GameSessionContext? context;
        lock (_gate)
        {
            if (!_sessions.TryRemove(session, out context))
            {
                return;
            }

            RemoveFromMap(context);
        }

        if (context is null)
        {
            return;
        }

        Console.WriteLine($"[world] left map={context.MapId} character={context.DisplayName} account={context.AccountId} population={GetMapPopulation(context.MapId)}");
    }

    public void UpdateCharacter(ClientSession session, GameCharacter character)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var existing))
            {
                return;
            }

            var updated = existing with
            {
                CharacterId = character.Id,
                CharacterName = character.Name,
                MapId = character.CurrentMap,
                Character = character
            };

            if (existing.MapId != updated.MapId)
            {
                RemoveFromMap(existing);
            }

            _sessions[session] = updated;
            AddToMap(updated);
        }
    }

    public ClientSession? ReplaceAccountSession(int accountId, ClientSession session)
    {
        ClientSession? replaced = null;
        _accountSessions.AddOrUpdate(
            accountId,
            session,
            (_, existing) =>
            {
                if (!ReferenceEquals(existing, session))
                {
                    replaced = existing;
                }

                return session;
            });

        return replaced;
    }

    public bool RemoveAccountSession(int accountId, ClientSession session)
    {
        return _accountSessions.TryGetValue(accountId, out var existing)
            && ReferenceEquals(existing, session)
            && _accountSessions.TryRemove(new KeyValuePair<int, ClientSession>(accountId, session));
    }

    public async Task<int> BroadcastToMapAsync(
        byte mapId,
        ReadOnlyMemory<byte> packet,
        CancellationToken cancellationToken,
        ClientSession? excludeSession = null,
        string? label = null,
        bool framed = true)
    {
        if (!_maps.TryGetValue(mapId, out var map))
        {
            return 0;
        }

        var sent = 0;
        foreach (var context in map.Snapshot())
        {
            if (excludeSession is not null && ReferenceEquals(context.Session, excludeSession))
            {
                continue;
            }

            try
            {
                await context.Session.SendAsync(packet, cancellationToken, label, framed);
                sent++;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Remove(context.Session);
            }
        }

        return sent;
    }

    public int GetMapPopulation(byte mapId)
    {
        return _maps.TryGetValue(mapId, out var map) ? map.Population : 0;
    }

    public IReadOnlyList<GameSessionContext> GetMapSessions(byte mapId, ClientSession? excludeSession = null)
    {
        if (!_maps.TryGetValue(mapId, out var map))
        {
            return [];
        }

        return map.Snapshot()
            .Where(context => excludeSession is null || !ReferenceEquals(context.Session, excludeSession))
            .ToArray();
    }

    public bool TryGetMapSessionByObjectId(
        byte mapId,
        uint objectId,
        ClientSession? excludeSession,
        out GameSessionContext context)
    {
        context = default!;
        if (!_maps.TryGetValue(mapId, out var map))
        {
            return false;
        }

        foreach (var candidate in map.Snapshot())
        {
            if (excludeSession is not null && ReferenceEquals(candidate.Session, excludeSession))
            {
                continue;
            }

            if (candidate.ObjectId != objectId)
            {
                continue;
            }

            context = candidate;
            return true;
        }

        return false;
    }

    public bool TryGetMapSessionByCharacterId(
        byte mapId,
        int characterId,
        ClientSession? excludeSession,
        out GameSessionContext context)
    {
        context = default!;
        if (!_maps.TryGetValue(mapId, out var map))
        {
            return false;
        }

        foreach (var candidate in map.Snapshot())
        {
            if (excludeSession is not null && ReferenceEquals(candidate.Session, excludeSession))
            {
                continue;
            }

            if (candidate.CharacterId != characterId)
            {
                continue;
            }

            context = candidate;
            return true;
        }

        return false;
    }

    private void AddToMap(GameSessionContext context)
    {
        var map = _maps.GetOrAdd(context.MapId, static mapId => new MapInstance(mapId));
        map.AddOrUpdate(context);
    }

    private void RemoveFromMap(GameSessionContext context)
    {
        if (_maps.TryGetValue(context.MapId, out var map))
        {
            map.Remove(context.Session, out _);
        }
    }
}
