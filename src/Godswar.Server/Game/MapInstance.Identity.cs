using Godswar.Server.Domain.World.Instances;
using Godswar.Server.World.Maps;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private const int LegacyPlayerCapacity =
        WorldInstanceDescriptor.MaximumPlayerCapacity;

    private readonly object _descriptorGate = new();
    private volatile WorldInstanceDescriptor _descriptor;

    public MapInstance(
        byte mapId,
        MonsterRuntimeMode monsterRuntimeMode = MonsterRuntimeMode.Ecs,
        PlayerRuntimeMode playerRuntimeMode = PlayerRuntimeMode.Ecs,
        WorldBossCatalog? worldBossCatalog = null,
        MonsterCombatProfileCatalog? monsterCombatProfiles = null)
        : this(
            CreateLegacyDescriptor(mapId),
            monsterRuntimeMode,
            playerRuntimeMode,
            worldBossCatalog,
            monsterCombatProfiles)
    {
    }

    internal MapInstance(
        WorldInstanceDescriptor descriptor,
        MonsterRuntimeMode monsterRuntimeMode = MonsterRuntimeMode.Ecs,
        PlayerRuntimeMode playerRuntimeMode = PlayerRuntimeMode.Ecs,
        WorldBossCatalog? worldBossCatalog = null,
        MonsterCombatProfileCatalog? monsterCombatProfiles = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.MapId.TryGetLegacyValue(out var legacyMapId))
        {
            throw new ArgumentOutOfRangeException(
                nameof(descriptor),
                descriptor.MapId,
                "The current client-facing map runtime requires a byte map ID.");
        }

        _descriptor = descriptor;
        MapId = legacyMapId;
        _monsterRuntimeMode = monsterRuntimeMode;
        _playerRuntimeMode = playerRuntimeMode;
        _worldBossCatalog = worldBossCatalog ?? WorldBossCatalog.Empty;
        _monsterCombatProfiles = monsterCombatProfiles ??
            MonsterCombatProfileCatalog.Empty;
        _ecsShadow = new MapEcsShadow(legacyMapId);
    }

    public WorldInstanceDescriptor Descriptor => _descriptor;

    public RealmId RealmId => Descriptor.RealmId;

    public WorldInstanceId WorldInstanceId => Descriptor.InstanceId;

    public WorldMapId ContentMapId => Descriptor.MapId;

    public InstanceKind InstanceKind => Descriptor.Kind;

    public byte MapId { get; }

    internal void BindDescriptor(WorldInstanceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_descriptorGate)
        {
            var current = _descriptor;
            if (descriptor.RealmId != current.RealmId ||
                descriptor.InstanceId != current.InstanceId ||
                descriptor.MapId != current.MapId ||
                descriptor.Kind != current.Kind ||
                descriptor.PlayerCapacity != current.PlayerCapacity ||
                descriptor.CreatedAt != current.CreatedAt)
            {
                throw new InvalidOperationException(
                    "A map runtime cannot be rebound to another world instance.");
            }

            if (descriptor == current)
            {
                return;
            }

            if (descriptor.Revision != checked(current.Revision + 1) ||
                !current.CanTransitionTo(descriptor.LifecycleState) ||
                descriptor.LastTransitionAt < current.LastTransitionAt)
            {
                throw new InvalidOperationException(
                    "The map runtime descriptor transition is not contiguous.");
            }

            _descriptor = descriptor;
        }
    }

    private static WorldInstanceDescriptor CreateLegacyDescriptor(byte mapId)
    {
        var now = DateTimeOffset.UtcNow;
        return WorldInstanceDescriptor.Create(
                RealmId.Tempest,
                WorldInstanceId.New(),
                WorldMapId.FromLegacy(mapId),
                InstanceKind.OpenWorld,
                LegacyPlayerCapacity,
                now)
            .TransitionTo(
                WorldInstanceLifecycleState.Active,
                now);
    }
}
