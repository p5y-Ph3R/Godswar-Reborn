using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.State;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private static readonly DateTimeOffset StartedAt = new(
        2026,
        8,
        22,
        12,
        0,
        0,
        TimeSpan.Zero);

    private static MapInstance CreateMap(
        MedusaEncounterDifficulty difficulty,
        InstanceKind kind = InstanceKind.Dungeon,
        WorldInstanceLifecycleState lifecycle =
            WorldInstanceLifecycleState.Creating,
        int playerCapacity = 5,
        DateTimeOffset? createdAt = null)
    {
        Check.True(
            MedusaIslandEncounterPolicy.TryGetDifficulty(
                difficulty,
                out var definition),
            $"{difficulty} resolves for ownership fixture");
        return CreateMap(
            definition.ContentMapId,
            kind,
            lifecycle,
            playerCapacity,
            createdAt);
    }

    private static MapInstance CreateMap(
        WorldMapId contentMapId,
        InstanceKind kind,
        WorldInstanceLifecycleState lifecycle =
            WorldInstanceLifecycleState.Creating,
        int playerCapacity = 5,
        DateTimeOffset? createdAt = null)
    {
        var authoritativeCreatedAt = createdAt ?? StartedAt;
        var descriptor = WorldInstanceDescriptor.Create(
            RealmId.Tempest,
            WorldInstanceId.New(),
            contentMapId,
            kind,
            playerCapacity,
            authoritativeCreatedAt);
        if (lifecycle != WorldInstanceLifecycleState.Creating)
        {
            descriptor = descriptor.TransitionTo(
                lifecycle,
                authoritativeCreatedAt);
        }

        return new MapInstance(descriptor);
    }

    private static MedusaInstanceBindResult Bind(
        MapInstance map,
        MedusaEncounterDifficulty difficulty,
        IReadOnlyCollection<int>? characters = null,
        IReadOnlyCollection<MedusaRunSpawnDefinition>? spawns = null)
    {
        var fixtureDifficulty = MedusaIslandEncounterPolicy.TryGetDifficulty(
            difficulty,
            out _)
            ? difficulty
            : MedusaEncounterDifficulty.Enhanced;
        return map.BindMedusaEncounter(
            difficulty,
            characters ?? [101, 102, 103, 104, 105],
            spawns ?? MedusaRunRuntimeCheckFixture.Spawns(fixtureDifficulty));
    }

    private static MedusaOwnedMonsterBinding Binding(
        MedusaInstanceOwnershipSnapshot snapshot,
        string rosterSpawnId) => snapshot.MonsterBindings.Single(binding =>
        binding.RosterSpawnId == rosterSpawnId);

    private static GameSessionContext CreateContext(
        MapInstance map,
        Godswar.Server.Networking.ClientSession session) => new(
        session,
        AccountId: 77,
        CharacterId: 777,
        CharacterName: "OwnershipFence",
        map.RealmId,
        map.WorldInstanceId,
        map.MapId,
        ObjectId: 900_001,
        new GameCharacter
        {
            Id = 777,
            AccountId = 77,
            Name = "OwnershipFence",
            CreatedUtc = StartedAt.UtcDateTime,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = map.MapId,
            PositionX = 1,
            PositionZ = 1,
            Level = 120,
            CurrentHp = 10_000,
            MaxHp = 10_000,
            CurrentMp = 10_000,
            MaxMp = 10_000,
            Equipment = string.Empty,
            KitBag = string.Empty
        },
        WorldReady: true,
        WorldRevision: 1);
}
