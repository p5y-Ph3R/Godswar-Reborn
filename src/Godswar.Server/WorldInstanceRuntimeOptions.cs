using WorldServerNodeId =
    Godswar.Server.Domain.World.Instances.ServerNodeId;
using WorldRealmId =
    Godswar.Server.Domain.World.Instances.RealmId;
using WorldMapId =
    Godswar.Server.Domain.World.Instances.MapId;
using WorldRuntimeId =
    Godswar.Server.Domain.World.Instances.WorldInstanceId;

namespace Godswar.Server;

/// <summary>
/// Process-local limits for world-instance ownership. These limits are
/// deliberately bounded independently of transport and persistence capacity.
/// </summary>
internal sealed class WorldInstanceRuntimeOptions
{
    public string ServerNodeId { get; set; } =
        WorldServerNodeId.Local.ToString();

    public int MaximumRuntimes { get; set; } = 256;

    public int MaximumPlayerAssignments { get; set; } = 4_096;

    public int MaximumRetiredInstanceIds { get; set; } = 65_536;

    public int DefaultOpenWorldPlayerCapacity { get; set; } = 512;

    public int MailboxCapacity { get; set; } = 1_024;

    public int OwnerInvocationTimeoutMilliseconds { get; set; } = 1_000;

    public int ShutdownDrainTimeoutMilliseconds { get; set; } = 5_000;

    public int MaximumFanoutConcurrency { get; set; } = 8;

    public StaticOpenWorldInstanceOptions[] StaticOpenWorldInstances
    {
        get;
        set;
    } = [];

    internal bool RequireStaticOpenWorldOwnership { get; set; }

    public TimeSpan OwnerInvocationTimeout =>
        TimeSpan.FromMilliseconds(OwnerInvocationTimeoutMilliseconds);

    public TimeSpan ShutdownDrainTimeout =>
        TimeSpan.FromMilliseconds(ShutdownDrainTimeoutMilliseconds);

    public WorldServerNodeId ProcessServerNodeId =>
        new(ServerNodeId);

    public bool TryFindStaticOpenWorld(
        WorldRealmId realmId,
        WorldMapId mapId,
        out WorldRuntimeId instanceId)
    {
        foreach (var route in StaticOpenWorldInstances)
        {
            if (route.ProcessRealmId == realmId &&
                route.ProcessMapId == mapId)
            {
                instanceId = route.ProcessWorldInstanceId;
                return true;
            }
        }

        instanceId = default;
        return false;
    }

    public void Validate()
    {
        try
        {
            _ = ProcessServerNodeId;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                "ServerNodeId must contain at most 64 ASCII letters, " +
                "digits, periods, underscores, or hyphens.",
                exception);
        }

        RequireRange(
            MaximumRuntimes,
            1,
            65_536,
            nameof(MaximumRuntimes));
        RequireRange(
            MaximumPlayerAssignments,
            1,
            1_000_000,
            nameof(MaximumPlayerAssignments));
        RequireRange(
            MaximumRetiredInstanceIds,
            MaximumRuntimes,
            1_000_000,
            nameof(MaximumRetiredInstanceIds));
        RequireRange(
            DefaultOpenWorldPlayerCapacity,
            1,
            Math.Min(MaximumPlayerAssignments, 100_000),
            nameof(DefaultOpenWorldPlayerCapacity));
        RequireRange(
            MailboxCapacity,
            1,
            65_536,
            nameof(MailboxCapacity));
        RequireRange(
            OwnerInvocationTimeoutMilliseconds,
            10,
            120_000,
            nameof(OwnerInvocationTimeoutMilliseconds));
        RequireRange(
            ShutdownDrainTimeoutMilliseconds,
            10,
            120_000,
            nameof(ShutdownDrainTimeoutMilliseconds));
        RequireRange(
            MaximumFanoutConcurrency,
            1,
            128,
            nameof(MaximumFanoutConcurrency));

        StaticOpenWorldInstances ??= [];
        if (StaticOpenWorldInstances.Length > MaximumRuntimes)
        {
            throw new InvalidDataException(
                "StaticOpenWorldInstances cannot exceed MaximumRuntimes.");
        }

        var routeKeys = new HashSet<(WorldRealmId, WorldMapId)>();
        var instanceIds = new HashSet<WorldRuntimeId>();
        foreach (var route in StaticOpenWorldInstances)
        {
            if (route is null)
            {
                throw new InvalidDataException(
                    "StaticOpenWorldInstances cannot contain null entries.");
            }

            route.Validate();
            if (!routeKeys.Add(
                    (route.ProcessRealmId, route.ProcessMapId)))
            {
                throw new InvalidDataException(
                    "Static open-world realm/map routes must be unique.");
            }
            if (!instanceIds.Add(route.ProcessWorldInstanceId))
            {
                throw new InvalidDataException(
                    "Static open-world instance IDs must be unique.");
            }
        }
    }

    private static void RequireRange(
        int value,
        int minimum,
        int maximum,
        string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"{name} must be between {minimum} and {maximum}.");
        }
    }
}

internal sealed class StaticOpenWorldInstanceOptions
{
    public int RealmId { get; set; } = WorldRealmId.Tempest.Value;

    public short MapId { get; set; }

    public string WorldInstanceId { get; set; } = string.Empty;

    public WorldRealmId ProcessRealmId => new(RealmId);

    public WorldMapId ProcessMapId => new(MapId);

    public WorldRuntimeId ProcessWorldInstanceId =>
        Guid.TryParse(WorldInstanceId, out var value)
            ? new WorldRuntimeId(value)
            : throw new InvalidDataException(
                "Static open-world instance IDs must be nonempty GUIDs.");

    public void Validate()
    {
        try
        {
            _ = ProcessRealmId;
            _ = ProcessMapId;
            _ = ProcessWorldInstanceId;
        }
        catch (Exception exception)
            when (exception is ArgumentException or
                FormatException or
                OverflowException)
        {
            throw new InvalidDataException(
                "A static open-world route contains an invalid realm, " +
                "map, or instance identity.",
                exception);
        }

        if (!ProcessMapId.TryGetLegacyValue(out _))
        {
            throw new InvalidDataException(
                "Static open-world routes currently require a legacy-byte map ID.");
        }
    }
}
