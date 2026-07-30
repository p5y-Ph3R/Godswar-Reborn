using Godswar.Server.Domain.World.Instances;
using Godswar.Server.World.Maps;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    public MapInstance(
        byte mapId,
        MonsterRuntimeMode monsterRuntimeMode = MonsterRuntimeMode.Ecs,
        PlayerRuntimeMode playerRuntimeMode = PlayerRuntimeMode.Ecs)
        : this(
            RealmId.Tempest,
            WorldInstanceId.New(),
            WorldMapId.FromLegacy(mapId),
            InstanceKind.OpenWorld,
            monsterRuntimeMode,
            playerRuntimeMode)
    {
    }

    internal MapInstance(
        RealmId realmId,
        WorldInstanceId worldInstanceId,
        WorldMapId contentMapId,
        InstanceKind instanceKind,
        MonsterRuntimeMode monsterRuntimeMode = MonsterRuntimeMode.Ecs,
        PlayerRuntimeMode playerRuntimeMode = PlayerRuntimeMode.Ecs)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentException(
                "A valid realm ID is required.",
                nameof(realmId));
        }

        if (!worldInstanceId.IsValid)
        {
            throw new ArgumentException(
                "A valid world-instance ID is required.",
                nameof(worldInstanceId));
        }

        if (!Enum.IsDefined(instanceKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(instanceKind),
                instanceKind,
                "Unsupported world-instance kind.");
        }

        if (!contentMapId.TryGetLegacyValue(out var legacyMapId))
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentMapId),
                contentMapId,
                "The current client-facing map runtime requires a byte map ID.");
        }

        RealmId = realmId;
        WorldInstanceId = worldInstanceId;
        ContentMapId = contentMapId;
        InstanceKind = instanceKind;
        MapId = legacyMapId;
        _monsterRuntimeMode = monsterRuntimeMode;
        _playerRuntimeMode = playerRuntimeMode;
        _ecsShadow = new MapEcsShadow(legacyMapId);
    }

    public RealmId RealmId { get; }

    public WorldInstanceId WorldInstanceId { get; }

    public WorldMapId ContentMapId { get; }

    public InstanceKind InstanceKind { get; }

    public byte MapId { get; }
}
