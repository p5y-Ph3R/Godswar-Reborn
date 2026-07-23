using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<
        ClientSession,
        NpcCatalogSubscription> _npcCatalogSubscriptions = [];
    private readonly ConcurrentDictionary<byte, SemaphoreSlim>
        _npcCatalogPublicationGates = [];

    internal NpcCatalogSubscription RegisterNpcCatalogUpdates(
        ClientSession session,
        Func<MapNpcCatalogSnapshot, CancellationToken, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(callback);

        var subscription = new NpcCatalogSubscription(
            session,
            callback);
        if (!_npcCatalogSubscriptions.TryAdd(session, subscription))
        {
            throw new InvalidOperationException(
                "The client session already has an NPC catalog subscription.");
        }

        return subscription;
    }

    internal async Task UnregisterNpcCatalogUpdatesAsync(
        NpcCatalogSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        _npcCatalogSubscriptions.TryRemove(
            new KeyValuePair<ClientSession, NpcCatalogSubscription>(
                subscription.Session,
                subscription));
        await subscription.StopAsync();
    }

    internal async Task<MapNpcCatalogSnapshot>
        PublishMapNpcDefinitionsAsync(
            byte mapId,
            IReadOnlyList<NpcSpawnDefinition> definitions,
            ClientSession? originSession,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var publicationGate = _npcCatalogPublicationGates.GetOrAdd(
            mapId,
            static _ => new SemaphoreSlim(1, 1));
        await publicationGate.WaitAsync(cancellationToken);
        try
        {
            var map = _maps.GetOrAdd(
                mapId,
                id => new MapInstance(
                    id,
                    _monsterRuntimeMode,
                    _playerRuntimeMode));
            var publication = map.PublishNpcDefinitions(definitions);
            if (publication.Changed)
            {
                await NotifyNpcCatalogSubscribersAsync(
                    map,
                    publication.Snapshot,
                    originSession);
            }

            return publication.Snapshot;
        }
        finally
        {
            publicationGate.Release();
        }
    }

    internal bool IsCanonicalMapNpc(
        byte mapId,
        long expectedRevision,
        NpcSpawnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return _maps.TryGetValue(mapId, out var map) &&
               map.IsCanonicalNpc(expectedRevision, definition);
    }

    internal bool IsCanonicalMapNpcCatalog(
        byte mapId,
        long expectedRevision)
    {
        return _maps.TryGetValue(mapId, out var map) &&
               map.SnapshotNpcCatalog().Revision == expectedRevision;
    }

    private async Task NotifyNpcCatalogSubscribersAsync(
        MapInstance map,
        MapNpcCatalogSnapshot snapshot,
        ClientSession? originSession)
    {
        var recipients = map.Snapshot()
            .Where(context =>
                originSession is null ||
                !ReferenceEquals(context.Session, originSession))
            .OrderBy(static context => context.ObjectId)
            .ToArray();
        foreach (var recipient in recipients)
        {
            if (!_npcCatalogSubscriptions.TryGetValue(
                    recipient.Session,
                    out var subscription))
            {
                continue;
            }

            try
            {
                await subscription.InvokeAsync(
                    CloneNpcCatalogSnapshot(snapshot),
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[npc] catalog revision delivery failed " +
                    $"map={snapshot.MapId} revision={snapshot.Revision} " +
                    $"character={recipient.DisplayName}: {ex.Message}");
                recipient.Session.Disconnect();
                Remove(recipient.Session);
            }
        }
    }

    private static MapNpcCatalogSnapshot CloneNpcCatalogSnapshot(
        MapNpcCatalogSnapshot snapshot) =>
        new(
            snapshot.MapId,
            snapshot.Revision,
            NpcCatalogDefinitions.ReadOnlyClone(snapshot.Definitions));
}

internal sealed class NpcCatalogSubscription
{
    private readonly object _gate = new();
    private readonly Func<
        MapNpcCatalogSnapshot,
        CancellationToken,
        Task> _callback;
    private bool _acceptingCallbacks = true;
    private int _activeCallbacks;
    private TaskCompletionSource? _stopped;

    public NpcCatalogSubscription(
        ClientSession session,
        Func<MapNpcCatalogSnapshot, CancellationToken, Task> callback)
    {
        Session = session;
        _callback = callback;
    }

    public ClientSession Session { get; }

    public async Task InvokeAsync(
        MapNpcCatalogSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_acceptingCallbacks)
            {
                return;
            }

            _activeCallbacks++;
        }

        try
        {
            await _callback(snapshot, cancellationToken);
        }
        finally
        {
            lock (_gate)
            {
                _activeCallbacks--;
                if (!_acceptingCallbacks && _activeCallbacks == 0)
                {
                    _stopped?.TrySetResult();
                }
            }
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            _acceptingCallbacks = false;
            if (_activeCallbacks == 0)
            {
                return Task.CompletedTask;
            }

            _stopped ??= new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return _stopped.Task;
        }
    }
}
