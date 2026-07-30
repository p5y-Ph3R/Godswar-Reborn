using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.ProtocolChecks;

internal static class WorldInstancePlacementChecks
{
    public const string CheckName =
        "B18A bounded local world-instance placement";

    private static readonly DateTimeOffset StartedAt =
        new(2026, 7, 31, 1, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        CheckIdentifiersAndLegacyMapBridge();
        CheckLifecycleContract();
        await CheckBoundedRegistrationAsync();
        await CheckAssignmentsAndTransfersAsync();
        await CheckConcurrentSingleAssignmentAsync();
        await CheckCloseAndRemovalAsync();
        await CheckRetirementBoundAsync();
    }

    private static void CheckIdentifiersAndLegacyMapBridge()
    {
        Check.Equal(
            1,
            RealmId.Tempest.Value,
            "Tempest retains legacy server row identity 1");
        Check.True(
            RealmId.Tempest != new RealmId(2),
            "different realms have distinct identities");
        Check.Equal(
            "local-node",
            ServerNodeId.Local.ToString(),
            "local node identity is separate from the realm");

        var legacyMap = MapId.FromLegacy(255);
        Check.True(
            legacyMap.TryGetLegacyValue(out var legacyValue) &&
            legacyValue == byte.MaxValue,
            "legacy byte map IDs round trip");

        var extendedMap = new MapId(256);
        Check.True(
            !extendedMap.TryGetLegacyValue(out _),
            "extended map IDs cannot silently truncate to the legacy protocol");

        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new RealmId(0),
            "zero realm identity is rejected");
        Check.Throws<ArgumentException>(
            () => _ = new ServerNodeId("node with spaces"),
            "unsafe node identity is rejected");
        Check.Throws<ArgumentException>(
            () => _ = new WorldInstanceId(Guid.Empty),
            "empty world-instance identity is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new MapId(-1),
            "negative map identity is rejected");
    }

    private static void CheckLifecycleContract()
    {
        var descriptor = CreateDescriptor(
            mapId: 1,
            InstanceKind.OpenWorld,
            playerCapacity: 50);
        Check.True(
            descriptor.LifecycleState ==
            WorldInstanceLifecycleState.Creating,
            "new instance begins in creating state");
        Check.Equal(
            1L,
            descriptor.Revision,
            "new instance starts at revision one");
        Check.True(
            descriptor.CanTransitionTo(
                WorldInstanceLifecycleState.Active),
            "creating instance can activate");
        Check.True(
            !descriptor.CanTransitionTo(
                WorldInstanceLifecycleState.Draining),
            "creating instance cannot skip activation into draining");

        var active = descriptor.TransitionTo(
            WorldInstanceLifecycleState.Active,
            StartedAt.AddSeconds(1));
        var draining = active.TransitionTo(
            WorldInstanceLifecycleState.Draining,
            StartedAt.AddSeconds(2));
        var closed = draining.TransitionTo(
            WorldInstanceLifecycleState.Closed,
            StartedAt.AddSeconds(3));

        Check.Equal(
            4L,
            closed.Revision,
            "each lifecycle transition advances the revision");
        Check.True(
            !closed.CanTransitionTo(
                WorldInstanceLifecycleState.Active),
            "closed lifecycle is terminal");
        Check.Throws<InvalidOperationException>(
            () => active.TransitionTo(
                WorldInstanceLifecycleState.Closed,
                StartedAt.AddSeconds(2)),
            "active instance must drain before normal closure");
    }

    private static async Task CheckBoundedRegistrationAsync()
    {
        var registry = CreateRegistry(
            maximumInstances: 3,
            maximumPlayerAssignments: 10);
        var sparta = CreateDescriptor(
            mapId: 1,
            InstanceKind.OpenWorld,
            playerCapacity: 5);
        var duplicateSparta = CreateDescriptor(
            mapId: 1,
            InstanceKind.OpenWorld,
            playerCapacity: 5);
        var otherRealmSparta = CreateDescriptor(
            mapId: 1,
            InstanceKind.OpenWorld,
            playerCapacity: 5,
            realmId: new RealmId(2));

        AssertStatus(
            WorldInstancePlacementStatus.Registered,
            await registry.RegisterAsync(sparta, default),
            "first Tempest open world registers");
        AssertStatus(
            WorldInstancePlacementStatus.OpenWorldConflict,
            await registry.RegisterAsync(duplicateSparta, default),
            "same realm/map open world cannot have two owners");
        AssertStatus(
            WorldInstancePlacementStatus.Registered,
            await registry.RegisterAsync(otherRealmSparta, default),
            "different realm may host the same map definition");

        var dungeonOne = CreateDescriptor(
            mapId: 20,
            InstanceKind.Dungeon,
            playerCapacity: 5);
        var dungeonTwo = CreateDescriptor(
            mapId: 20,
            InstanceKind.Dungeon,
            playerCapacity: 5);
        AssertStatus(
            WorldInstancePlacementStatus.Registered,
            await registry.RegisterAsync(dungeonOne, default),
            "first dungeon instance registers");
        AssertStatus(
            WorldInstancePlacementStatus.RegistryFull,
            await registry.RegisterAsync(dungeonTwo, default),
            "bounded registry rejects excess runtime instances");
        Check.Equal(
            3,
            registry.Snapshot().Count,
            "rejected registrations allocate no registry entry");

        var duplicateRegistry = CreateRegistry(2, 10);
        AssertStatus(
            WorldInstancePlacementStatus.Registered,
            await duplicateRegistry.RegisterAsync(dungeonOne, default),
            "dungeon registration succeeds");
        AssertStatus(
            WorldInstancePlacementStatus.Registered,
            await duplicateRegistry.RegisterAsync(dungeonTwo, default),
            "two dungeon instances may use the same map definition");
    }

    private static async Task CheckAssignmentsAndTransfersAsync()
    {
        var registry = CreateRegistry(3, 3);
        var first = await RegisterActiveAsync(
            registry,
            mapId: 20,
            InstanceKind.Dungeon,
            playerCapacity: 1);
        var second = await RegisterActiveAsync(
            registry,
            mapId: 20,
            InstanceKind.Dungeon,
            playerCapacity: 2);

        AssertStatus(
            WorldInstancePlacementStatus.Assigned,
            await registry.AssignCharacterAsync(
                101,
                first.Descriptor.InstanceId,
                default),
            "character enters first dungeon");
        AssertStatus(
            WorldInstancePlacementStatus.NoChange,
            await registry.AssignCharacterAsync(
                101,
                first.Descriptor.InstanceId,
                default),
            "same assignment is idempotent");
        AssertStatus(
            WorldInstancePlacementStatus.CharacterAlreadyAssigned,
            await registry.AssignCharacterAsync(
                101,
                second.Descriptor.InstanceId,
                default),
            "character cannot exist in two instances");
        AssertStatus(
            WorldInstancePlacementStatus.InstanceFull,
            await registry.AssignCharacterAsync(
                102,
                first.Descriptor.InstanceId,
                default),
            "instance player capacity is authoritative");

        AssertStatus(
            WorldInstancePlacementStatus.Transferred,
            await registry.TransferCharacterAsync(
                101,
                first.Descriptor.InstanceId,
                second.Descriptor.InstanceId,
                default),
            "explicit transfer atomically changes instance ownership");
        AssertStatus(
            WorldInstancePlacementStatus.NoChange,
            await registry.TransferCharacterAsync(
                101,
                first.Descriptor.InstanceId,
                second.Descriptor.InstanceId,
                default),
            "completed transfer retry is idempotent");

        var firstAfter = await registry.FindAsync(
            first.Descriptor.InstanceId,
            default);
        var secondAfter = await registry.FindAsync(
            second.Descriptor.InstanceId,
            default);
        Check.Equal(
            0,
            firstAfter!.Population,
            "source dungeon no longer contains transferred character");
        Check.Equal(
            1,
            secondAfter!.Population,
            "destination dungeon contains transferred character");

        var draining = await registry.TransitionAsync(
            second.Descriptor.InstanceId,
            second.Descriptor.Revision,
            WorldInstanceLifecycleState.Draining,
            StartedAt.AddMinutes(1),
            default);
        AssertStatus(
            WorldInstancePlacementStatus.Transitioned,
            draining,
            "active dungeon begins draining");
        AssertStatus(
            WorldInstancePlacementStatus.InstanceNotActive,
            await registry.AssignCharacterAsync(
                102,
                second.Descriptor.InstanceId,
                default),
            "draining instance rejects new players");
    }

    private static async Task CheckConcurrentSingleAssignmentAsync()
    {
        var registry = CreateRegistry(2, 10);
        var first = await RegisterActiveAsync(
            registry,
            mapId: 30,
            InstanceKind.Battlefield,
            playerCapacity: 10);
        var second = await RegisterActiveAsync(
            registry,
            mapId: 30,
            InstanceKind.Battlefield,
            playerCapacity: 10);

        var start = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstOffer = Task.Run(async () =>
        {
            await start.Task;
            return await registry.AssignCharacterAsync(
                    201,
                    first.Descriptor.InstanceId,
                    default);
        });
        var secondOffer = Task.Run(async () =>
        {
            await start.Task;
            return await registry.AssignCharacterAsync(
                    201,
                    second.Descriptor.InstanceId,
                    default);
        });
        start.SetResult();

        var offers = await Task.WhenAll(firstOffer, secondOffer);

        Check.Equal(
            1,
            offers.Count(result =>
                result.Status ==
                WorldInstancePlacementStatus.Assigned),
            "concurrent assignment has exactly one winner");
        Check.Equal(
            1,
            registry.Snapshot().Sum(static value => value.Population),
            "concurrent assignment creates only one membership");

        var characterPlacement = await registry.FindCharacterAsync(
            201,
            default);
        Check.True(
            characterPlacement is not null &&
            characterPlacement.Population == 1,
            "winning concurrent placement is queryable");
    }

    private static async Task CheckCloseAndRemovalAsync()
    {
        var registry = CreateRegistry(1, 2);
        var dungeon = await RegisterActiveAsync(
            registry,
            mapId: 40,
            InstanceKind.Dungeon,
            playerCapacity: 2);
        await registry.AssignCharacterAsync(
            301,
            dungeon.Descriptor.InstanceId,
            default);

        var staleTransition = await registry.TransitionAsync(
            dungeon.Descriptor.InstanceId,
            expectedRevision: 1,
            WorldInstanceLifecycleState.Draining,
            StartedAt.AddMinutes(1),
            default);
        AssertStatus(
            WorldInstancePlacementStatus.RevisionConflict,
            staleTransition,
            "stale lifecycle owner cannot mutate the instance");

        var draining = await registry.TransitionAsync(
            dungeon.Descriptor.InstanceId,
            dungeon.Descriptor.Revision,
            WorldInstanceLifecycleState.Draining,
            StartedAt.AddMinutes(1),
            default);
        AssertStatus(
            WorldInstancePlacementStatus.RevisionConflict,
            await registry.TransitionAsync(
                dungeon.Descriptor.InstanceId,
                dungeon.Descriptor.Revision,
                WorldInstanceLifecycleState.Draining,
                StartedAt.AddMinutes(1),
                default),
            "stale same-state lifecycle retry cannot report success");
        AssertStatus(
            WorldInstancePlacementStatus.InstanceNotEmpty,
            await registry.TransitionAsync(
                dungeon.Descriptor.InstanceId,
                draining.Placement!.Descriptor.Revision,
                WorldInstanceLifecycleState.Closed,
                StartedAt.AddMinutes(2),
                default),
            "draining instance cannot close while a character is assigned");
        AssertStatus(
            WorldInstancePlacementStatus.Released,
            await registry.ReleaseCharacterAsync(
                301,
                dungeon.Descriptor.InstanceId,
                default),
            "draining instance releases its final character explicitly");
        var closed = await registry.TransitionAsync(
            dungeon.Descriptor.InstanceId,
            draining.Placement!.Descriptor.Revision,
            WorldInstanceLifecycleState.Closed,
            StartedAt.AddMinutes(2),
            default);
        AssertStatus(
            WorldInstancePlacementStatus.Transitioned,
            closed,
            "draining dungeon closes");
        Check.Equal(
            0,
            closed.Placement!.Population,
            "closed instance has no runtime assignments");
        Check.True(
            await registry.FindCharacterAsync(301, default) is null,
            "closed instance leaves no stale character route");

        AssertStatus(
            WorldInstancePlacementStatus.Removed,
            await registry.RemoveClosedAsync(
                dungeon.Descriptor.InstanceId,
                default),
            "closed descriptor can be removed");
        AssertStatus(
            WorldInstancePlacementStatus.RetiredInstance,
            await registry.RegisterAsync(
                dungeon.Descriptor,
                default),
            "terminal world-instance identity cannot be registered again");

        var replacement = CreateDescriptor(
            mapId: 40,
            InstanceKind.Dungeon,
            playerCapacity: 2);
        AssertStatus(
            WorldInstancePlacementStatus.Registered,
            await registry.RegisterAsync(replacement, default),
            "removing a closed instance reclaims bounded capacity");
    }

    private static async Task CheckRetirementBoundAsync()
    {
        var registry = new LocalWorldInstancePlacementRegistry(
            ServerNodeId.Local,
            maximumInstances: 1,
            maximumPlayerAssignments: 1,
            maximumRetiredInstanceIds: 1);
        var retired = await RegisterActiveAsync(
            registry,
            mapId: 41,
            InstanceKind.Dungeon,
            playerCapacity: 1);
        var draining = await registry.TransitionAsync(
            retired.Descriptor.InstanceId,
            retired.Descriptor.Revision,
            WorldInstanceLifecycleState.Draining,
            StartedAt.AddMinutes(1),
            default);
        var closed = await registry.TransitionAsync(
            retired.Descriptor.InstanceId,
            draining.Placement!.Descriptor.Revision,
            WorldInstanceLifecycleState.Closed,
            StartedAt.AddMinutes(2),
            default);
        await registry.RemoveClosedAsync(
            closed.Placement!.Descriptor.InstanceId,
            default);

        var next = await RegisterActiveAsync(
            registry,
            mapId: 42,
            InstanceKind.Dungeon,
            playerCapacity: 1);
        var nextDraining = await registry.TransitionAsync(
            next.Descriptor.InstanceId,
            next.Descriptor.Revision,
            WorldInstanceLifecycleState.Draining,
            StartedAt.AddMinutes(3),
            default);
        var nextClosed = await registry.TransitionAsync(
            next.Descriptor.InstanceId,
            nextDraining.Placement!.Descriptor.Revision,
            WorldInstanceLifecycleState.Closed,
            StartedAt.AddMinutes(4),
            default);
        AssertStatus(
            WorldInstancePlacementStatus.RetirementRegistryFull,
            await registry.RemoveClosedAsync(
                nextClosed.Placement!.Descriptor.InstanceId,
                default),
            "retired identity bound fails closed instead of forgetting IDs");
        Check.Equal(
            1,
            registry.Snapshot().Count,
            "closed descriptor remains registered when retirement is full");
    }

    private static LocalWorldInstancePlacementRegistry CreateRegistry(
        int maximumInstances,
        int maximumPlayerAssignments) =>
        new(
            ServerNodeId.Local,
            maximumInstances,
            maximumPlayerAssignments);

    private static WorldInstanceDescriptor CreateDescriptor(
        short mapId,
        InstanceKind kind,
        int playerCapacity,
        RealmId? realmId = null) =>
        WorldInstanceDescriptor.Create(
            realmId ?? RealmId.Tempest,
            WorldInstanceId.New(),
            new MapId(mapId),
            kind,
            playerCapacity,
            StartedAt);

    private static async Task<WorldInstancePlacementSnapshot>
        RegisterActiveAsync(
            LocalWorldInstancePlacementRegistry registry,
            short mapId,
            InstanceKind kind,
            int playerCapacity)
    {
        var descriptor = CreateDescriptor(
            mapId,
            kind,
            playerCapacity);
        AssertStatus(
            WorldInstancePlacementStatus.Registered,
            await registry.RegisterAsync(descriptor, default),
            "instance registers before activation");
        var activation = await registry.TransitionAsync(
            descriptor.InstanceId,
            descriptor.Revision,
            WorldInstanceLifecycleState.Active,
            StartedAt.AddSeconds(1),
            default);
        AssertStatus(
            WorldInstancePlacementStatus.Transitioned,
            activation,
            "instance activates");
        return activation.Placement!;
    }

    private static void AssertStatus(
        WorldInstancePlacementStatus expected,
        WorldInstancePlacementResult actual,
        string message)
    {
        Check.True(actual.Status == expected, message);
        Check.Equal(
            expected is
                WorldInstancePlacementStatus.Registered or
                WorldInstancePlacementStatus.Transitioned or
                WorldInstancePlacementStatus.Assigned or
                WorldInstancePlacementStatus.Transferred or
                WorldInstancePlacementStatus.Released or
                WorldInstancePlacementStatus.Removed or
                WorldInstancePlacementStatus.NoChange,
            actual.Succeeded,
            $"{message} success classification");
    }
}
