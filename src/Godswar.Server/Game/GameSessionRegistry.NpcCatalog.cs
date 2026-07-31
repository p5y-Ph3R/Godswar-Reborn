using System.Collections.Concurrent;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private readonly ConcurrentDictionary<
        ClientSession,
        NpcCatalogSubscription> _npcCatalogSubscriptions = [];
    private readonly ConcurrentDictionary<WorldInstanceId, SemaphoreSlim>
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
        var runtime = TryResolveWorldInstance(
            mapId,
            originSession,
            out var routedRuntime)
            ? routedRuntime
            : GetOrCreateDefaultWorldInstance(mapId);
        var publicationGate = _npcCatalogPublicationGates.GetOrAdd(
            runtime.InstanceId,
            static _ => new SemaphoreSlim(1, 1));
        await publicationGate.WaitAsync(cancellationToken);
        try
        {
            var dispatch = InvokeWorldOwner(
                runtime,
                map =>
                {
                    var publication =
                        map.PublishNpcDefinitions(definitions);
                    var recipients = publication.Changed
                        ? map.Snapshot()
                            .Where(context =>
                                originSession is null ||
                                !ReferenceEquals(
                                    context.Session,
                                    originSession))
                            .OrderBy(
                                static context =>
                                    context.ObjectId)
                            .ToArray()
                        : [];
                    return new NpcCatalogPublicationDispatch(
                        publication,
                        recipients);
                },
                cancellationToken);
            if (dispatch.Publication.Changed)
            {
                await NotifyNpcCatalogSubscribersAsync(
                    dispatch.Publication.Snapshot,
                    dispatch.Recipients);
            }

            return dispatch.Publication.Snapshot;
        }
        finally
        {
            publicationGate.Release();
        }
    }

    internal bool IsCanonicalMapNpc(
        byte mapId,
        long expectedRevision,
        NpcSpawnDefinition definition,
        ClientSession? routingSession = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return TryResolveWorldInstance(
                   mapId,
                   routingSession,
                   out var runtime) &&
               InvokeWorldOwner(
                   runtime,
                   map => map.IsCanonicalNpc(
                       expectedRevision,
                       definition));
    }

    internal bool IsCanonicalMapNpcCatalog(
        byte mapId,
        long expectedRevision,
        ClientSession? routingSession = null)
    {
        return TryResolveWorldInstance(
                   mapId,
                   routingSession,
                   out var runtime) &&
               InvokeWorldOwner(
                   runtime,
                   map =>
                       map.SnapshotNpcCatalog().Revision ==
                       expectedRevision);
    }

    private async Task NotifyNpcCatalogSubscribersAsync(
        MapNpcCatalogSnapshot snapshot,
        IReadOnlyList<GameSessionContext> recipients)
    {
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
                    $"instance={snapshot.WorldInstanceId} " +
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
            snapshot.WorldInstanceId,
            snapshot.MapId,
            snapshot.Revision,
            NpcCatalogDefinitions.ReadOnlyClone(snapshot.Definitions));

    private readonly record struct NpcCatalogPublicationDispatch(
        MapNpcCatalogPublication Publication,
        GameSessionContext[] Recipients);
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
