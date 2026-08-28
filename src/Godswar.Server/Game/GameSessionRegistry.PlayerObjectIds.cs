using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly Dictionary<uint, int>
        _detachedPlayerObjectIdReservations = [];

    internal uint JoinPlayerMap(
        ClientSession session,
        int accountId,
        GameCharacter character,
        bool worldReady = true,
        DateTimeOffset? joinedAt = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(character);
        if (character.CurrentMap is 200 or 204)
        {
            throw new InvalidOperationException(
                "Medusa Island reconnect requires a durable exact-instance " +
                "assignment; default map-only fallback is forbidden.");
        }

        return JoinWorldInstanceCore(
            session,
            accountId,
            character,
            requestedObjectId: null,
            runtime: GetOrCreateDefaultWorldInstance(
                character.CurrentMap),
            worldReady: worldReady,
            joinedAt: joinedAt);
    }

    internal uint GetRequiredPlayerObjectId(
        ClientSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            if (_sessions.TryGetValue(session, out var context))
            {
                return context.ObjectId;
            }
        }

        throw new InvalidOperationException(
            "The client session has not joined a world.");
    }

    private uint AllocatePlayerObjectIdLocked(
        ClientSession session,
        int characterId,
        GameSessionContext? previous)
    {
        if (previous is not null &&
            previous.CharacterId == characterId &&
            WorldObjectIds.IsRemotePlayer(previous.ObjectId))
        {
            return previous.ObjectId;
        }

        var activeObjectIds = _sessions
            .Where(pair =>
                !ReferenceEquals(pair.Key, session) &&
                WorldObjectIds.IsRemotePlayer(
                    pair.Value.ObjectId))
            .Select(static pair => pair.Value.ObjectId)
            .ToHashSet();
        activeObjectIds.UnionWith(
            _detachedPlayerObjectIdReservations.Keys);
        return WorldObjectIds.AllocateForPlayer(
            characterId,
            activeObjectIds);
    }

    private DetachedPlayerWorldSession ReserveDetachedPlayerWorldLocked(
        GameSessionContext context)
    {
        var reserved = WorldObjectIds.IsRemotePlayer(
            context.ObjectId);
        if (reserved)
        {
            _detachedPlayerObjectIdReservations.TryGetValue(
                context.ObjectId,
                out var references);
            _detachedPlayerObjectIdReservations[context.ObjectId] =
                checked(references + 1);
        }

        return new DetachedPlayerWorldSession(
            context,
            reserved);
    }

    internal void ReleaseDetachedPlayerWorld(
        DetachedPlayerWorldSession detached)
    {
        ArgumentNullException.ThrowIfNull(detached);
        if (!detached.TryReleaseReservation() ||
            !detached.ObjectIdReserved)
        {
            return;
        }

        lock (_gate)
        {
            if (!_detachedPlayerObjectIdReservations.TryGetValue(
                    detached.Context.ObjectId,
                    out var references) ||
                references <= 0)
            {
                throw new InvalidOperationException(
                    "The detached player object-ID reservation is missing.");
            }

            if (references == 1)
            {
                _detachedPlayerObjectIdReservations.Remove(
                    detached.Context.ObjectId);
            }
            else
            {
                _detachedPlayerObjectIdReservations[
                    detached.Context.ObjectId] = references - 1;
            }
        }
    }
}

internal sealed class DetachedPlayerWorldSession
{
    private int _reservationReleased;

    internal DetachedPlayerWorldSession(
        GameSessionContext context,
        bool objectIdReserved)
    {
        Context = context;
        ObjectIdReserved = objectIdReserved;
    }

    public GameSessionContext Context { get; }

    internal bool ObjectIdReserved { get; }

    internal bool TryReleaseReservation() =>
        Interlocked.Exchange(ref _reservationReleased, 1) == 0;
}
