using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.State;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WorldInstanceRuntimeDirectoryChecks
{
    public const string CheckName =
        "B18B1 local world-instance runtime directory";

    private static readonly DateTimeOffset CreatedAt =
        new(2026, 7, 31, 4, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        await CheckStableLegacyDefaultAsync();
        await CheckRepeatedDungeonIsolationAsync();
        await CheckLifecycleAndCapacityAsync();
        await CheckOwnerLifecycleAsync();
    }

    private static async Task CheckStableLegacyDefaultAsync()
    {
        var placement = CreatePlacement(maximumInstances: 8);
        await using var directory = CreateDirectory(placement);
        var start = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var offers = Enumerable.Range(0, 8)
            .Select(async _ =>
            {
                await start.Task;
                return await directory.GetOrCreateOpenWorldAsync(
                    RealmId.Tempest,
                    legacyMapId: 1,
                    playerCapacity: 100,
                    CreatedAt,
                    default);
            })
            .ToArray();
        start.SetResult();
        var results = await Task.WhenAll(offers);

        Check.Equal(
            1,
            results.Count(result =>
                result.Status ==
                WorldInstanceRuntimeDirectoryStatus.Created),
            "concurrent default creation has one creator");
        Check.Equal(
            7,
            results.Count(result =>
                result.Status ==
                WorldInstanceRuntimeDirectoryStatus.ExistingDefault),
            "concurrent default creation reuses the one projection");

        var runtime = results[0].Runtime!;
        Check.True(
            results.All(result =>
                ReferenceEquals(runtime, result.Runtime)),
            "all default lookups return the same runtime object");
        Check.True(
            runtime.Descriptor.LifecycleState ==
                WorldInstanceLifecycleState.Active &&
            runtime.Map.Descriptor == runtime.Descriptor,
            "created runtime and map bind the active descriptor");
        Check.True(
            runtime.RealmId == RealmId.Tempest &&
            runtime.Map.MapId == 1 &&
            runtime.ContentMapId == WorldMapId.FromLegacy(1) &&
            runtime.Kind == InstanceKind.OpenWorld,
            "legacy map one resolves to Tempest open world");
        Check.True(
            directory.TryFind(runtime.InstanceId, out var byId) &&
            ReferenceEquals(runtime, byId),
            "WorldInstanceId is the primary runtime lookup");
        Check.True(
            directory.TryFindOpenWorld(
                RealmId.Tempest,
                1,
                out var byMap) &&
            ReferenceEquals(runtime, byMap),
            "legacy byte map bridge is a projection to the primary runtime");
        Check.Equal(
            1,
            directory.GetSnapshot().RuntimeCount,
            "default race creates one runtime");
        Check.Equal(
            1,
            placement.Snapshot().Count,
            "default race creates one placement");
    }

    private static async Task CheckRepeatedDungeonIsolationAsync()
    {
        var placement = CreatePlacement(maximumInstances: 4);
        await using var directory = CreateDirectory(placement);
        var first = await CreateInstancedAsync(
            directory,
            mapId: 40,
            InstanceKind.Dungeon);
        var second = await CreateInstancedAsync(
            directory,
            mapId: 40,
            InstanceKind.Dungeon);

        Check.True(
            first.InstanceId != second.InstanceId &&
            first.ContentMapId == second.ContentMapId &&
            !ReferenceEquals(first.Map, second.Map),
            "dungeons may share content without sharing runtime identity");
        Check.Equal(
            2,
            directory.Snapshot().Count,
            "both dungeon runtimes are indexed by instance identity");
        Check.True(
            !directory.TryFindOpenWorld(RealmId.Tempest, 40, out _),
            "dungeons never occupy the open-world compatibility projection");

        await using var firstSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var secondSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var firstCharacter = CreateCharacter(
            characterId: 901,
            accountId: 91,
            name: "FirstDungeonHero");
        var secondCharacter = CreateCharacter(
            characterId: 902,
            accountId: 92,
            name: "SecondDungeonHero");
        const uint sharedObjectId = 0x6501;
        first.Owner.Invoke(
            map =>
            {
                map.AddOrUpdate(
                    CreateContext(
                        first,
                        firstSocket.Session,
                        firstCharacter,
                        sharedObjectId));
                return SingleOwnerMailboxUnit.Value;
            },
            TimeSpan.FromSeconds(1));
        second.Owner.Invoke(
            map =>
            {
                map.AddOrUpdate(
                    CreateContext(
                        second,
                        secondSocket.Session,
                        secondCharacter,
                        sharedObjectId));
                return SingleOwnerMailboxUnit.Value;
            },
            TimeSpan.FromSeconds(1));
        AssertPlacementStatus(
            WorldInstancePlacementStatus.Assigned,
            await directory.AssignCharacterAsync(
                firstCharacter.Id,
                first.InstanceId,
                default),
            "first character assignment uses directory authority");
        AssertPlacementStatus(
            WorldInstancePlacementStatus.Assigned,
            await directory.AssignCharacterAsync(
                secondCharacter.Id,
                second.InstanceId,
                default),
            "second character assignment uses directory authority");

        Check.Equal(
            1,
            first.Owner.Invoke(
                static map => map.Population,
                TimeSpan.FromSeconds(1)),
            "first dungeon owns its one session");
        Check.Equal(
            1,
            second.Owner.Invoke(
                static map => map.Population,
                TimeSpan.FromSeconds(1)),
            "second dungeon independently owns its one session");
        Check.Equal(
            firstCharacter.Id,
            first.Owner.Invoke(
                    static map => map.Snapshot(),
                    TimeSpan.FromSeconds(1))
                .Single()
                .CharacterId,
            "first dungeon snapshot contains only first character");
        Check.Equal(
            secondCharacter.Id,
            second.Owner.Invoke(
                    static map => map.Snapshot(),
                    TimeSpan.FromSeconds(1))
                .Single()
                .CharacterId,
            "second dungeon snapshot contains only second character");
        var firstShadow = first.Owner.Invoke(
            map =>
            {
                var found = map.TryGetShadowPlayerEntity(
                    firstSocket.Session,
                    out var entity);
                return (
                    Found: found,
                    Alive: found &&
                        map.IsShadowEntityAlive(entity));
            },
            TimeSpan.FromSeconds(1));
        var secondShadow = second.Owner.Invoke(
            map =>
            {
                var found = map.TryGetShadowPlayerEntity(
                    secondSocket.Session,
                    out var entity);
                return (
                    Found: found,
                    Alive: found &&
                        map.IsShadowEntityAlive(entity));
            },
            TimeSpan.FromSeconds(1));
        Check.True(
            firstShadow is { Found: true, Alive: true } &&
            secondShadow is { Found: true, Alive: true },
            "duplicate object IDs live in isolated ECS worlds");

        first.Owner.Invoke(
            map =>
            {
                map.Remove(firstSocket.Session, out _);
                return SingleOwnerMailboxUnit.Value;
            },
            TimeSpan.FromSeconds(1));
        Check.Equal(
            0,
            first.Owner.Invoke(
                static map => map.Population,
                TimeSpan.FromSeconds(1)),
            "removing from first dungeon changes only first runtime");
        Check.Equal(
            1,
            second.Owner.Invoke(
                static map => map.Population,
                TimeSpan.FromSeconds(1)),
            "removing from first dungeon preserves second runtime");
        AssertPlacementStatus(
            WorldInstancePlacementStatus.Transferred,
            await directory.TransferCharacterAsync(
                firstCharacter.Id,
                first.InstanceId,
                second.InstanceId,
                default),
            "directory transfers assignment between instance identities");
        AssertPlacementStatus(
            WorldInstancePlacementStatus.Transferred,
            await directory.TransferCharacterAsync(
                firstCharacter.Id,
                second.InstanceId,
                first.InstanceId,
                default),
            "directory transfer can return to the source instance");
        AssertPlacementStatus(
            WorldInstancePlacementStatus.Released,
            await directory.ReleaseCharacterAsync(
                firstCharacter.Id,
                first.InstanceId,
                default),
            "directory releases the first assignment before closure");

        var firstDraining = await DrainAsync(directory, first);
        Check.True(
            first.Owner.GetSnapshot().State ==
                SingleOwnerMailboxState.Accepting,
            "lifecycle drain keeps the owner open for resident removal");
        var firstClosed = await CloseAsync(directory, firstDraining);
        Check.True(
            firstClosed.Owner.GetSnapshot().State ==
                SingleOwnerMailboxState.Stopped,
            "closed runtime reports only after its owner drains");
        await RemoveAsync(directory, firstClosed);
        Check.True(
            !directory.TryFind(first.InstanceId, out _) &&
            directory.TryFind(second.InstanceId, out _),
            "removing one closed dungeon preserves the other runtime");

        var closeWithPlayer = await directory.CloseAsync(
            second.InstanceId,
            second.Descriptor.Revision,
            CreatedAt.AddMinutes(2),
            default);
        AssertStatus(
            WorldInstanceRuntimeDirectoryStatus.RuntimeNotEmpty,
            closeWithPlayer,
            "live map membership prevents closure");

        var secondDraining = await DrainAsync(directory, second);
        Check.True(
            secondDraining.Owner.GetSnapshot().State ==
                SingleOwnerMailboxState.Accepting,
            "draining runtime still accepts resident cleanup");
        secondDraining.Owner.Invoke(
            map =>
            {
                map.Remove(secondSocket.Session, out _);
                return SingleOwnerMailboxUnit.Value;
            },
            TimeSpan.FromSeconds(1));
        AssertPlacementStatus(
            WorldInstancePlacementStatus.Released,
            await directory.ReleaseCharacterAsync(
                secondCharacter.Id,
                second.InstanceId,
                default),
            "directory releases the second assignment before closure");
        var secondClosed = await CloseAsync(directory, secondDraining);
        Check.True(
            secondClosed.Owner.GetSnapshot().State ==
                SingleOwnerMailboxState.Stopped,
            "second closed runtime owns no accepted work");
        await RemoveAsync(directory, secondClosed);
    }

    private static async Task CheckLifecycleAndCapacityAsync()
    {
        var placement = CreatePlacement(maximumInstances: 2);
        await using var directory = CreateDirectory(placement);
        var battlefield = await CreateInstancedAsync(
            directory,
            mapId: 50,
            InstanceKind.Battlefield);
        var dungeon = await CreateInstancedAsync(
            directory,
            mapId: 51,
            InstanceKind.Dungeon);

        var overCapacity = await directory.CreateInstancedAsync(
            RealmId.Tempest,
            new WorldMapId(52),
            InstanceKind.Dungeon,
            playerCapacity: 10,
            CreatedAt,
            default);
        AssertStatus(
            WorldInstanceRuntimeDirectoryStatus.PlacementRejected,
            overCapacity,
            "placement capacity bounds runtime creation");
        Check.True(
            overCapacity.PlacementStatus ==
            WorldInstancePlacementStatus.RegistryFull,
            "capacity rejection preserves placement reason");

        var staleDrain = await directory.BeginDrainAsync(
            battlefield.InstanceId,
            expectedRevision: 1,
            CreatedAt.AddMinutes(1),
            default);
        AssertStatus(
            WorldInstanceRuntimeDirectoryStatus.PlacementRejected,
            staleDrain,
            "stale lifecycle revision is rejected");
        Check.True(
            staleDrain.PlacementStatus ==
            WorldInstancePlacementStatus.RevisionConflict,
            "stale drain exposes revision conflict");

        var draining = await DrainAsync(directory, battlefield);
        var unavailable = await directory.GetOrCreateOpenWorldAsync(
            RealmId.Tempest,
            legacyMapId: 50,
            playerCapacity: 10,
            CreatedAt.AddMinutes(1),
            default);
        AssertStatus(
            WorldInstanceRuntimeDirectoryStatus.PlacementRejected,
            unavailable,
            "battlefield does not become a default open world");
        Check.True(
            unavailable.PlacementStatus ==
            WorldInstancePlacementStatus.RegistryFull,
            "bounded placement remains authoritative across instance kinds");

        var closed = await CloseAsync(directory, draining);
        await RemoveAsync(directory, closed);
        Check.Equal(
            1,
            directory.GetSnapshot().RuntimeCount,
            "closed removal reclaims one runtime slot");

        var replacement = await CreateInstancedAsync(
            directory,
            mapId: 52,
            InstanceKind.Dungeon);
        Check.True(
            replacement.InstanceId != battlefield.InstanceId &&
            directory.TryFind(dungeon.InstanceId, out _) &&
            directory.TryFind(replacement.InstanceId, out _),
            "reclaimed capacity admits a new unique instance");
    }

    private static LocalWorldInstancePlacementRegistry CreatePlacement(
        int maximumInstances) =>
        new(
            ServerNodeId.Local,
            maximumInstances,
            maximumPlayerAssignments: 100,
            maximumRetiredInstanceIds: 100);

    private static LocalWorldInstanceRuntimeDirectory CreateDirectory(
        LocalWorldInstancePlacementRegistry placement) =>
        new(
            placement,
            new MapWorldInstanceRuntimeFactory());

    private static async Task<WorldInstanceRuntime> CreateInstancedAsync(
        LocalWorldInstanceRuntimeDirectory directory,
        short mapId,
        InstanceKind kind)
    {
        var created = await directory.CreateInstancedAsync(
            RealmId.Tempest,
            new WorldMapId(mapId),
            kind,
            playerCapacity: 10,
            CreatedAt,
            default);
        AssertStatus(
            WorldInstanceRuntimeDirectoryStatus.Created,
            created,
            $"{kind} runtime creates");
        return created.Runtime!;
    }

    private static async Task<WorldInstanceRuntime> DrainAsync(
        LocalWorldInstanceRuntimeDirectory directory,
        WorldInstanceRuntime runtime)
    {
        var result = await directory.BeginDrainAsync(
            runtime.InstanceId,
            runtime.Descriptor.Revision,
            CreatedAt.AddMinutes(1),
            default);
        AssertStatus(
            WorldInstanceRuntimeDirectoryStatus.Draining,
            result,
            "active runtime begins draining");
        return result.Runtime!;
    }

    private static async Task<WorldInstanceRuntime> CloseAsync(
        LocalWorldInstanceRuntimeDirectory directory,
        WorldInstanceRuntime runtime)
    {
        var result = await directory.CloseAsync(
            runtime.InstanceId,
            runtime.Descriptor.Revision,
            CreatedAt.AddMinutes(2),
            default);
        AssertStatus(
            WorldInstanceRuntimeDirectoryStatus.Closed,
            result,
            "draining runtime closes");
        return result.Runtime!;
    }

    private static async Task RemoveAsync(
        LocalWorldInstanceRuntimeDirectory directory,
        WorldInstanceRuntime runtime)
    {
        var result = await directory.RemoveClosedAsync(
            runtime.InstanceId,
            default);
        AssertStatus(
            WorldInstanceRuntimeDirectoryStatus.Removed,
            result,
            "closed runtime removes");
    }

    private static GameSessionContext CreateContext(
        WorldInstanceRuntime runtime,
        Godswar.Server.Networking.ClientSession session,
        GameCharacter character,
        uint objectId) =>
        new(
            session,
            character.AccountId,
            character.Id,
            character.Name,
            runtime.RealmId,
            runtime.InstanceId,
            character.CurrentMap,
            objectId,
            character,
            WorldReady: true,
            WorldRevision: 0);

    private static GameCharacter CreateCharacter(
        int characterId,
        int accountId,
        string name) =>
        new()
        {
            Id = characterId,
            AccountId = accountId,
            Name = name,
            CreatedUtc = CreatedAt.UtcDateTime,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = 40,
            PositionX = 4f,
            PositionZ = 5f,
            Level = 20,
            CurrentHp = 2_000,
            MaxHp = 2_500,
            CurrentMp = 1_000,
            MaxMp = 1_500,
            Equipment = string.Empty,
            KitBag = string.Empty
        };

}
