using Godswar.Server.Ecs;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Maps;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private readonly MapEcsShadow _ecsShadow;

    internal IReadOnlyList<NpcSpawnDefinition> ObserveNpcDefinitions(
        IReadOnlyList<NpcSpawnDefinition> definitions)
    {
        return PublishNpcDefinitions(definitions).Snapshot.Definitions;
    }

    private void ObserveNpcDefinitionsCore(
        IReadOnlyList<NpcSpawnDefinition> definitions)
    {
        if (!_ecsShadow.TryObserveNpcDefinitions(definitions))
        {
            throw new InvalidOperationException(
                $"ECS rejected NPC definitions for map {MapId}.");
        }

    }

    internal MapEcsShadowSnapshot SnapshotEcsShadow() =>
        _ecsShadow.Snapshot();

    internal MapEcsParityDiagnostics DiagnoseEcsShadow() =>
        _ecsShadow.Diagnose(_sessions.Values.ToArray());

    internal bool TryGetShadowPlayerEntity(
        ClientSession session,
        out EntityId entity) =>
        _ecsShadow.TryGetPlayerEntity(session, out entity);

    internal bool TryGetShadowNpcEntity(
        uint objectId,
        out EntityId entity) =>
        _ecsShadow.TryGetNpcEntity(objectId, out entity);

    internal bool TryGetShadowEntityByObjectId(
        uint objectId,
        out EntityId entity) =>
        _ecsShadow.TryGetEntityByObjectId(objectId, out entity);

    internal bool IsShadowEntityAlive(EntityId entity) =>
        _ecsShadow.IsEntityAlive(entity);

    private bool RemoveSessionAndShadow(
        ClientSession session,
        out GameSessionContext? context)
    {
        lock (_membershipGate)
        {
            var removed = _sessions.TryRemove(session, out context);
            if (removed)
            {
                _ecsShadow.TryRemovePlayer(session);
            }

            return removed;
        }
    }

    private bool ContainsPlayer(ClientSession session)
    {
        if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
        {
            return _ecsShadow.ContainsPlayer(session);
        }

        return _sessions.ContainsKey(session);
    }
}
