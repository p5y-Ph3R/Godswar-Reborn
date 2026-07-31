using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.State;
using Godswar.Server.World.Maps;

namespace Godswar.Server.Game.WorldInstances;

/// <summary>
/// Owns one mutable map runtime and its bounded single-owner mailbox for one
/// immutable world-instance identity.
/// </summary>
internal sealed class WorldInstanceRuntime : IAsyncDisposable
{
    private BoundedSingleOwnerMailbox<MapInstance>? _owner;

    public WorldInstanceRuntime(
        MapInstance map,
        BoundedSingleOwnerMailbox<MapInstance>? owner = null)
    {
        Map = map ?? throw new ArgumentNullException(nameof(map));
        _owner = owner ?? new BoundedSingleOwnerMailbox<MapInstance>(
            Map,
            MapWorldInstanceRuntimeFactory.DefaultMailboxCapacity);
    }

    public MapInstance Map { get; }

    public BoundedSingleOwnerMailbox<MapInstance> Owner =>
        _owner ?? throw new ObjectDisposedException(
            nameof(WorldInstanceRuntime));

    public WorldInstanceDescriptor Descriptor => Map.Descriptor;

    public RealmId RealmId => Descriptor.RealmId;

    public WorldInstanceId InstanceId => Descriptor.InstanceId;

    public MapId ContentMapId => Descriptor.MapId;

    public byte MapId => Map.MapId;

    public InstanceKind Kind => Descriptor.Kind;

    /// <summary>
    /// Binds immutable control-plane metadata while the runtime-directory
    /// mutation gate is held. MapInstance guards this descriptor independently
    /// from simulation state, so lifecycle publication remains safe after the
    /// simulation mailbox has drained.
    /// </summary>
    internal void BindDescriptor(WorldInstanceDescriptor descriptor) =>
        Map.BindDescriptor(descriptor);

    public async ValueTask DisposeAsync()
    {
        var owner = Interlocked.Exchange(
            ref _owner,
            null);
        if (owner is not null)
        {
            await owner.DisposeAsync();
        }
    }
}

internal interface IWorldInstanceRuntimeFactory
{
    WorldInstanceRuntime Create(WorldInstanceDescriptor descriptor);
}

/// <summary>
/// Creates a map and owner mailbox without exposing either concern to the
/// placement directory.
/// </summary>
internal sealed class MapWorldInstanceRuntimeFactory(
    MonsterRuntimeMode monsterRuntimeMode = MonsterRuntimeMode.Ecs,
    PlayerRuntimeMode playerRuntimeMode = PlayerRuntimeMode.Ecs,
    int mailboxCapacity =
        MapWorldInstanceRuntimeFactory.DefaultMailboxCapacity,
    TimeSpan? mailboxShutdownTimeout = null) :
    IWorldInstanceRuntimeFactory
{
    internal const int DefaultMailboxCapacity = 4_096;

    public WorldInstanceRuntime Create(
        WorldInstanceDescriptor descriptor)
    {
        var map = new MapInstance(
            descriptor,
            monsterRuntimeMode,
            playerRuntimeMode);
        return new WorldInstanceRuntime(
            map,
            new BoundedSingleOwnerMailbox<MapInstance>(
                map,
                mailboxCapacity,
                mailboxShutdownTimeout));
    }
}
