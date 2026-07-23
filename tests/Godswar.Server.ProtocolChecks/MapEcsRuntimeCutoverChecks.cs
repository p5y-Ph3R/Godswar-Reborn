using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class MapEcsRuntimeCutoverChecks
{
    private const int AccountId = 81;
    private const int InvalidAccountId = 82;
    private const int CharacterId = 831;
    private const uint PlayerObjectId = 0x6501;

    public static async Task RunAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();

        CheckFailedInitialHydration(socket.Session);
        CheckFailedSameMapUpdate(socket.Session);
        CheckFailedCrossMapUpdate(socket.Session);
        CheckValidLifecycle(
            socket.Session,
            PlayerRuntimeMode.Ecs);
        CheckValidLifecycle(
            socket.Session,
            PlayerRuntimeMode.Legacy);

        Check.Equal(
            0,
            socket.Available,
            "map runtime cutover emits no network packets");
    }

    private static void CheckFailedInitialHydration(
        ClientSession session)
    {
        var invalidCharacter = CreateCharacter(
            mapId: 0,
            accountId: InvalidAccountId,
            x: 100f,
            z: -100f);
        var ecs = CreateRegistry(PlayerRuntimeMode.Ecs);

        Check.Throws<InvalidOperationException>(
            () => ecs.JoinMap(
                session,
                AccountId,
                invalidCharacter,
                PlayerObjectId,
                worldReady: true),
            "initial ECS hydration failure rejects the join");
        AssertNoMapMembership(
            ecs,
            session,
            mapId: 0,
            "failed initial ECS hydration");
        Check.True(
            !ecs.TryMarkWorldReady(
                session,
                new Dictionary<uint, long>(),
                out var unseenPlayers),
            "failed initial ECS hydration does not publish global membership");
        Check.Equal(
            0,
            unseenPlayers.Count,
            "failed initial ECS hydration has no unseen-player result");

        var legacy = CreateRegistry(PlayerRuntimeMode.Legacy);
        legacy.JoinMap(
            session,
            AccountId,
            invalidCharacter,
            PlayerObjectId,
            worldReady: true);

        Check.Equal(
            1,
            legacy.GetMapPopulation(0),
            "legacy initial hydration failure keeps transport membership");
        var legacyContext = legacy.GetMapSessions(0).Single();
        Check.True(
            ReferenceEquals(invalidCharacter, legacyContext.Character),
            "legacy mode publishes the transport character despite shadow rejection");
        Check.True(
            legacy.TryMarkWorldReady(
                session,
                new Dictionary<uint, long>(),
                out _),
            "legacy initial hydration failure still publishes global membership");

        legacy.Remove(session);
        AssertNoMapMembership(
            legacy,
            session,
            mapId: 0,
            "legacy invalid-session removal");
    }

    private static void CheckFailedSameMapUpdate(
        ClientSession session)
    {
        var validCharacter = CreateCharacter(
            mapId: 0,
            accountId: AccountId,
            x: 110f,
            z: -90f);
        var invalidUpdate = CreateCharacter(
            mapId: 0,
            accountId: InvalidAccountId,
            x: 140f,
            z: -60f);
        var ecs = CreateRegistry(PlayerRuntimeMode.Ecs);
        ecs.JoinMap(
            session,
            AccountId,
            validCharacter,
            PlayerObjectId,
            worldReady: true);
        var prior = ecs.GetMapSessions(0).Single();

        Check.Throws<InvalidOperationException>(
            () => ecs.UpdateCharacter(session, invalidUpdate),
            "same-map ECS hydration failure rejects the update");

        Check.Equal(
            1,
            ecs.GetMapPopulation(0),
            "same-map ECS failure retains population");
        var retained = ecs.GetMapSessions(0).Single();
        Check.True(
            ReferenceEquals(prior, retained) &&
            ReferenceEquals(validCharacter, retained.Character),
            "same-map ECS failure retains the prior authoritative context");
        Check.Equal(
            110f,
            retained.Character.PositionX,
            "same-map ECS failure does not publish staged position");
        Check.True(
            ecs.TryGetMapSessionByCharacterId(
                0,
                CharacterId,
                excludeSession: null,
                out var retainedByCharacter) &&
            ReferenceEquals(retained, retainedByCharacter),
            "same-map ECS failure retains character lookup");

        ecs.Remove(session);
        AssertNoMapMembership(
            ecs,
            session,
            mapId: 0,
            "same-map ECS cleanup");

        var legacy = CreateRegistry(PlayerRuntimeMode.Legacy);
        legacy.JoinMap(
            session,
            AccountId,
            validCharacter,
            PlayerObjectId,
            worldReady: true);
        legacy.UpdateCharacter(session, invalidUpdate);

        Check.Equal(
            1,
            legacy.GetMapPopulation(0),
            "legacy same-map shadow rejection retains population");
        var legacyUpdated = legacy.GetMapSessions(0).Single();
        Check.True(
            ReferenceEquals(invalidUpdate, legacyUpdated.Character),
            "legacy same-map shadow rejection publishes the transport update");
        Check.Equal(
            140f,
            legacyUpdated.Character.PositionX,
            "legacy same-map mode publishes the staged position");

        legacy.Remove(session);
        AssertNoMapMembership(
            legacy,
            session,
            mapId: 0,
            "legacy same-map cleanup");
    }

    private static void CheckFailedCrossMapUpdate(
        ClientSession session)
    {
        var validCharacter = CreateCharacter(
            mapId: 0,
            accountId: AccountId,
            x: 120f,
            z: -80f);
        var invalidMovedCharacter = CreateCharacter(
            mapId: 1,
            accountId: InvalidAccountId,
            x: -45f,
            z: 80f);
        var ecs = CreateRegistry(PlayerRuntimeMode.Ecs);
        ecs.JoinMap(
            session,
            AccountId,
            validCharacter,
            PlayerObjectId,
            worldReady: true);
        var prior = ecs.GetMapSessions(0).Single();

        Check.Throws<InvalidOperationException>(
            () => ecs.UpdateCharacter(
                session,
                invalidMovedCharacter),
            "cross-map ECS hydration failure rejects the update");

        Check.Equal(
            1,
            ecs.GetMapPopulation(0),
            "cross-map ECS failure restores old-map population");
        Check.Equal(
            0,
            ecs.GetMapPopulation(1),
            "cross-map ECS failure leaves new-map population empty");
        var restored = ecs.GetMapSessions(0).Single();
        Check.True(
            ReferenceEquals(prior, restored) &&
            ReferenceEquals(validCharacter, restored.Character),
            "cross-map ECS failure restores the old authoritative context");
        Check.Equal(
            0,
            ecs.GetMapSessions(1).Count,
            "cross-map ECS failure publishes no new-map session");
        Check.True(
            ecs.TryGetMapSessionByObjectId(
                0,
                PlayerObjectId,
                excludeSession: null,
                out var restoredByObject) &&
            ReferenceEquals(restored, restoredByObject),
            "cross-map ECS compensation restores old-map object lookup");
        Check.True(
            !ecs.TryGetMapSessionByObjectId(
                1,
                PlayerObjectId,
                excludeSession: null,
                out _),
            "cross-map ECS failure leaves no new-map object lookup");

        ecs.Remove(session);
        AssertNoMapMembership(
            ecs,
            session,
            mapId: 0,
            "cross-map ECS cleanup");

        var legacy = CreateRegistry(PlayerRuntimeMode.Legacy);
        legacy.JoinMap(
            session,
            AccountId,
            validCharacter,
            PlayerObjectId,
            worldReady: true);
        legacy.UpdateCharacter(session, invalidMovedCharacter);

        Check.Equal(
            0,
            legacy.GetMapPopulation(0),
            "legacy cross-map shadow rejection removes old-map membership");
        Check.Equal(
            1,
            legacy.GetMapPopulation(1),
            "legacy cross-map shadow rejection publishes new-map membership");
        var legacyMoved = legacy.GetMapSessions(1).Single();
        Check.True(
            ReferenceEquals(
                invalidMovedCharacter,
                legacyMoved.Character),
            "legacy cross-map shadow rejection publishes the transport update");

        legacy.Remove(session);
        AssertNoMapMembership(
            legacy,
            session,
            mapId: 1,
            "legacy cross-map cleanup");
    }

    private static void CheckValidLifecycle(
        ClientSession session,
        PlayerRuntimeMode mode)
    {
        const byte mapId = 2;
        var character = CreateCharacter(
            mapId,
            AccountId,
            x: 25f,
            z: 35f);
        var registry = CreateRegistry(mode);

        registry.JoinMap(
            session,
            AccountId,
            character,
            PlayerObjectId,
            worldReady: false);

        Check.Equal(
            1,
            registry.GetMapPopulation(mapId),
            $"{mode} valid join population");
        Check.Equal(
            0,
            registry.GetMapSessions(mapId).Count,
            $"{mode} non-ready join is excluded from world readers");
        Check.True(
            registry.TryMarkWorldReady(
                session,
                new Dictionary<uint, long>(),
                out var unseenPlayers),
            $"{mode} valid world-ready transition");
        Check.Equal(
            0,
            unseenPlayers.Count,
            $"{mode} valid world-ready transition has no unseen players");

        var ready = registry.GetMapSessions(mapId).Single();
        Check.True(
            ready.WorldReady &&
            ReferenceEquals(character, ready.Character),
            $"{mode} world-ready context is authoritative");
        Check.True(
            registry.TryGetMapSessionByObjectId(
                mapId,
                PlayerObjectId,
                excludeSession: null,
                out var readyByObject) &&
            ReferenceEquals(ready, readyByObject),
            $"{mode} world-ready object lookup");

        registry.Remove(session);
        AssertNoMapMembership(
            registry,
            session,
            mapId,
            $"{mode} valid removal");
        Check.True(
            !registry.TryMarkWorldReady(
                session,
                new Dictionary<uint, long>(),
                out _),
            $"{mode} valid removal clears global membership");
    }

    private static void AssertNoMapMembership(
        GameSessionRegistry registry,
        ClientSession session,
        byte mapId,
        string description)
    {
        Check.Equal(
            0,
            registry.GetMapPopulation(mapId),
            $"{description} population");
        Check.Equal(
            0,
            registry.GetMapSessions(mapId).Count,
            $"{description} world-reader sessions");
        Check.True(
            !registry.TryGetMapSessionByObjectId(
                mapId,
                PlayerObjectId,
                excludeSession: null,
                out _),
            $"{description} object lookup");
        Check.True(
            !registry.TryGetMapSessionByCharacterId(
                mapId,
                CharacterId,
                excludeSession: null,
                out _),
            $"{description} character lookup");
    }

    private static GameSessionRegistry CreateRegistry(
        PlayerRuntimeMode mode) =>
        new(
            store: null,
            zodiacEnergyOptions: null,
            monsterRuntimeMode: MonsterRuntimeMode.Ecs,
            playerRuntimeMode: mode);

    private static GameCharacter CreateCharacter(
        byte mapId,
        int accountId,
        float x,
        float z) =>
        new()
        {
            Id = CharacterId,
            AccountId = accountId,
            Name = "MapRuntimeCutoverHero",
            CreatedUtc =
                new DateTime(
                    2026,
                    7,
                    23,
                    2,
                    3,
                    4,
                    DateTimeKind.Utc),
            Camp = mapId == 0
                ? GameDefaults.SpartaCamp
                : GameDefaults.AthensCamp,
            CurrentMap = mapId,
            PositionX = x,
            PositionZ = z,
            Level = 20,
            CurrentHp = 2_000,
            MaxHp = 2_500,
            CurrentMp = 1_000,
            MaxMp = 1_500,
            Equipment = string.Empty,
            KitBag = string.Empty
        };
}
