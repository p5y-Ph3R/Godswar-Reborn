namespace Godswar.Server.Domain.World.Instances;

internal enum InstanceKind : byte
{
    OpenWorld = 1,
    Battlefield = 2,
    Dungeon = 3
}

internal enum WorldInstanceLifecycleState : byte
{
    Creating = 1,
    Active = 2,
    Draining = 3,
    Closed = 4
}

/// <summary>
/// Immutable control-plane description of one runtime simulation. It contains
/// no player, monster, or other mutable ECS state.
/// </summary>
internal sealed record WorldInstanceDescriptor
{
    public const int MaximumPlayerCapacity = 100_000;

    private WorldInstanceDescriptor(
        RealmId realmId,
        WorldInstanceId instanceId,
        MapId mapId,
        InstanceKind kind,
        WorldInstanceLifecycleState lifecycleState,
        int playerCapacity,
        DateTimeOffset createdAt,
        DateTimeOffset lastTransitionAt,
        long revision)
    {
        RealmId = realmId;
        InstanceId = instanceId;
        MapId = mapId;
        Kind = kind;
        LifecycleState = lifecycleState;
        PlayerCapacity = playerCapacity;
        CreatedAt = createdAt;
        LastTransitionAt = lastTransitionAt;
        Revision = revision;
    }

    public RealmId RealmId { get; }

    public WorldInstanceId InstanceId { get; }

    public MapId MapId { get; }

    public InstanceKind Kind { get; }

    public WorldInstanceLifecycleState LifecycleState { get; }

    public int PlayerCapacity { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastTransitionAt { get; }

    public long Revision { get; }

    public static WorldInstanceDescriptor Create(
        RealmId realmId,
        WorldInstanceId instanceId,
        MapId mapId,
        InstanceKind kind,
        int playerCapacity,
        DateTimeOffset createdAt)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentException(
                "A valid realm ID is required.",
                nameof(realmId));
        }

        if (!instanceId.IsValid)
        {
            throw new ArgumentException(
                "A valid world instance ID is required.",
                nameof(instanceId));
        }

        if (!mapId.IsValid)
        {
            throw new ArgumentException(
                "A valid map ID is required.",
                nameof(mapId));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Unsupported world instance kind.");
        }

        if (playerCapacity is <= 0 or > MaximumPlayerCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(playerCapacity),
                playerCapacity,
                $"Player capacity must be between 1 and " +
                $"{MaximumPlayerCapacity}.");
        }

        var createdUtc = createdAt.ToUniversalTime();
        return new WorldInstanceDescriptor(
            realmId,
            instanceId,
            mapId,
            kind,
            WorldInstanceLifecycleState.Creating,
            playerCapacity,
            createdUtc,
            createdUtc,
            revision: 1);
    }

    public bool CanTransitionTo(WorldInstanceLifecycleState target) =>
        (LifecycleState, target) switch
        {
            (WorldInstanceLifecycleState.Creating,
                WorldInstanceLifecycleState.Active) => true,
            (WorldInstanceLifecycleState.Creating,
                WorldInstanceLifecycleState.Closed) => true,
            (WorldInstanceLifecycleState.Active,
                WorldInstanceLifecycleState.Draining) => true,
            (WorldInstanceLifecycleState.Draining,
                WorldInstanceLifecycleState.Closed) => true,
            _ => false
        };

    public WorldInstanceDescriptor TransitionTo(
        WorldInstanceLifecycleState target,
        DateTimeOffset transitionedAt)
    {
        if (!Enum.IsDefined(target) || !CanTransitionTo(target))
        {
            throw new InvalidOperationException(
                $"World instance cannot transition from " +
                $"{LifecycleState} to {target}.");
        }

        var transitionedUtc = transitionedAt.ToUniversalTime();
        if (transitionedUtc < LastTransitionAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transitionedAt),
                transitionedAt,
                "Lifecycle transition time cannot move backwards.");
        }

        return new WorldInstanceDescriptor(
            RealmId,
            InstanceId,
            MapId,
            Kind,
            target,
            PlayerCapacity,
            CreatedAt,
            transitionedUtc,
            checked(Revision + 1));
    }
}
