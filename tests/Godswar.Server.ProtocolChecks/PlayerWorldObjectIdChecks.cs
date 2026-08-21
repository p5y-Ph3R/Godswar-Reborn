using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PlayerWorldObjectIdChecks
{
    public const string CheckName =
        "Native remote-player object-ID allocation";

    public static IReadOnlyList<(string Name, Func<Task> Run)> All =>
        [(CheckName, RunAsync)];

    public static async Task RunAsync()
    {
        CheckNoProductionRecomputation();
        CheckNativeRangeContract();
        CheckDeterministicCollisionProbe();
        CheckPoolExhaustion();
        await CheckConcurrentRegistryLifecycleAsync();
        await CheckAccountReplacementRemovalOrderingAsync();
        await CheckFailedJoinDoesNotLeakAsync();
        await CheckRemoveReplacementRaceAsync();
        await CheckExplicitObjectIdCompatibilityAsync();
    }

    private static void CheckNativeRangeContract()
    {
        Check.Equal(
            1u,
            WorldObjectIds.ForPlayer(1),
            "first character prefers the first native remote-player ID");
        Check.Equal(
            0x05DBu,
            WorldObjectIds.ForPlayer(0x05DB),
            "last in-range character prefers the native upper bound");
        Check.Equal(
            1u,
            WorldObjectIds.ForPlayer(0x05DC),
            "preferred IDs wrap without entering the monster namespace");
        Check.True(
            WorldObjectIds.IsRemotePlayer(1) &&
            WorldObjectIds.IsRemotePlayer(0x05DB) &&
            !WorldObjectIds.IsRemotePlayer(0) &&
            !WorldObjectIds.IsRemotePlayer(0x05DC),
            "remote-player IDs are exactly the native 1..1499 interval");
        Check.True(
            !WorldObjectIds.IsRemotePlayer(0x1448) &&
            WorldObjectIds.IsReservedForPlayer(0x1448),
            "the stock client's local-player identity is reserved but " +
            "excluded from the remote allocation pool");
    }

    private static void CheckDeterministicCollisionProbe()
    {
        var occupied = new HashSet<uint> { 1, 2, 3 };
        Check.Equal(
            4u,
            WorldObjectIds.AllocateForPlayer(1, occupied),
            "allocation probes forward from the character's preferred ID");

        occupied = [0x05DB, 1];
        Check.Equal(
            2u,
            WorldObjectIds.AllocateForPlayer(0x05DB, occupied),
            "collision probing wraps at the native upper bound");
    }

    private static void CheckPoolExhaustion()
    {
        var occupied = Enumerable.Range(
                (int)WorldObjectIds.FirstRemotePlayerObjectId,
                WorldObjectIds.RemotePlayerObjectIdCapacity)
            .Select(static value => (uint)value)
            .ToHashSet();
        Check.Throws<InvalidOperationException>(
            () => WorldObjectIds.AllocateForPlayer(1, occupied),
            "a full native player-ID pool fails before aliasing a live player");
    }

    private static async Task CheckConcurrentRegistryLifecycleAsync()
    {
        const int sessionCount = 8;
        await using var registry = new GameSessionRegistry();
        var sessions = Enumerable.Range(0, sessionCount)
            .Select(_ => new ClientSession(new NoopTransport()))
            .ToArray();
        try
        {
            var characters = Enumerable.Range(0, sessionCount)
                .Select(index => CreateCharacter(
                    1 +
                        index *
                        WorldObjectIds.RemotePlayerObjectIdCapacity,
                    index,
                    index % 2 == 0
                        ? GameDefaults.SpartaCapitalMap
                        : GameDefaults.AthensCapitalMap))
                .ToArray();
            var joins = sessions
                .Select((session, index) => Task.Run(() =>
                    registry.JoinPlayerMap(
                        session,
                        characters[index].AccountId,
                        characters[index])))
                .ToArray();
            var assigned = await Task.WhenAll(joins);

            Check.Equal(
                sessionCount,
                assigned.Distinct().Count(),
                "concurrent colliding joins receive globally unique IDs");
            Check.True(
                assigned.All(WorldObjectIds.IsRemotePlayer),
                "every concurrent join remains in the native player range");
            Check.True(
                assigned.Order().SequenceEqual(
                    Enumerable.Range(1, sessionCount)
                        .Select(static value => (uint)value)),
                "serialized collision probing consumes the first free IDs");

            var transferredCharacter = CreateCharacter(
                characters[0].Id,
                0,
                GameDefaults.AthensCapitalMap);
            var stable = registry.JoinPlayerMap(
                sessions[0],
                transferredCharacter.AccountId,
                transferredCharacter);
            Check.Equal(
                assigned[0],
                registry.GetRequiredPlayerObjectId(sessions[0]),
                "an active character keeps its assigned identity across maps");
            Check.Equal(
                assigned[0],
                stable,
                "the map-transfer join returns the preserved identity");

            var activeReplacementCharacter = CreateCharacter(
                1 +
                    20 *
                    WorldObjectIds.RemotePlayerObjectIdCapacity,
                80,
                GameDefaults.SpartaCapitalMap);
            var activeReplacement = registry.JoinPlayerMap(
                sessions[0],
                activeReplacementCharacter.AccountId,
                activeReplacementCharacter);
            Check.True(
                WorldObjectIds.IsRemotePlayer(activeReplacement) &&
                !assigned.Skip(1).Contains(activeReplacement),
                "same-session character replacement cannot alias another " +
                "active player");
            Check.Equal(
                activeReplacement,
                registry.GetRequiredPlayerObjectId(sessions[0]),
                "same-session replacement publishes its allocated identity");

            var released = activeReplacement;
            registry.Remove(sessions[0]);
            Check.Throws<InvalidOperationException>(
                () => registry.GetRequiredPlayerObjectId(sessions[0]),
                "removing an active session releases its identity");

            var replacementCharacter = CreateCharacter(
                checked((int)released),
                sessionCount,
                GameDefaults.SpartaCapitalMap);
            var replacement = registry.JoinPlayerMap(
                sessions[0],
                replacementCharacter.AccountId,
                replacementCharacter);
            Check.Equal(
                released,
                replacement,
                "a disconnected identity becomes available to a later join");
        }
        finally
        {
            foreach (var session in sessions)
            {
                registry.Remove(session);
                await session.DisposeAsync();
            }
        }
    }

    private static async Task CheckFailedJoinDoesNotLeakAsync()
    {
        await using var registry = new GameSessionRegistry(
            worldInstanceOptions: new WorldInstanceRuntimeOptions
            {
                RealmId = 1,
                MaximumRuntimes = 1,
                MaximumPlayerAssignments = 1,
                MaximumRetiredInstanceIds = 1,
                DefaultOpenWorldPlayerCapacity = 1
            });
        await using var owner = new ClientSession(new NoopTransport());
        await using var rejected = new ClientSession(new NoopTransport());
        var ownerCharacter = CreateCharacter(
            1,
            90,
            GameDefaults.SpartaCapitalMap);
        var rejectedCharacter = CreateCharacter(
            1 + WorldObjectIds.RemotePlayerObjectIdCapacity,
            91,
            GameDefaults.SpartaCapitalMap);
        var ownerId = registry.JoinPlayerMap(
            owner,
            ownerCharacter.AccountId,
            ownerCharacter);

        Check.Throws<InvalidOperationException>(
            () => registry.JoinPlayerMap(
                rejected,
                rejectedCharacter.AccountId,
                rejectedCharacter),
            "failed world admission rolls back before publishing a session");
        Check.Throws<InvalidOperationException>(
            () => registry.GetRequiredPlayerObjectId(rejected),
            "failed world admission exposes no transient object identity");

        registry.Remove(owner);
        var retriedId = registry.JoinPlayerMap(
            rejected,
            rejectedCharacter.AccountId,
            rejectedCharacter);
        Check.Equal(
            ownerId,
            retriedId,
            "a failed admission does not leak its probed object-ID lease");
        registry.Remove(rejected);
    }

    private static async Task CheckRemoveReplacementRaceAsync()
    {
        await using var registry = new GameSessionRegistry();
        for (var iteration = 0; iteration < 16; iteration++)
        {
            await using var original =
                new ClientSession(new NoopTransport());
            await using var replacement =
                new ClientSession(new NoopTransport());
            var originalCharacter = CreateCharacter(
                1,
                100 + iteration * 2,
                GameDefaults.SpartaCapitalMap);
            var replacementCharacter = CreateCharacter(
                1 + WorldObjectIds.RemotePlayerObjectIdCapacity,
                101 + iteration * 2,
                GameDefaults.AthensCapitalMap);
            registry.JoinPlayerMap(
                original,
                originalCharacter.AccountId,
                originalCharacter);

            using var start = new ManualResetEventSlim();
            var remove = Task.Run(() =>
            {
                start.Wait();
                registry.Remove(original);
            });
            var replace = Task.Run(() =>
            {
                start.Wait();
                return registry.JoinPlayerMap(
                    replacement,
                    replacementCharacter.AccountId,
                    replacementCharacter);
            });
            start.Set();

            await remove;
            Check.Throws<InvalidOperationException>(
                () => registry.GetRequiredPlayerObjectId(original),
                "the departing owner is removed during a replacement race");
            var replacementId = await replace;
            Check.True(
                WorldObjectIds.IsRemotePlayer(replacementId) &&
                registry.GetRequiredPlayerObjectId(replacement) ==
                    replacementId,
                "a racing replacement retains one valid authoritative ID");
            registry.Remove(replacement);
        }
    }

    private static async Task CheckExplicitObjectIdCompatibilityAsync()
    {
        const uint injectedObjectId = 0x0000_7F01;
        await using var registry = new GameSessionRegistry();
        await using var session = new ClientSession(new NoopTransport());
        var character = CreateCharacter(
            90_001,
            900,
            GameDefaults.SpartaCapitalMap);

        registry.JoinMap(
            session,
            character.AccountId,
            character,
            injectedObjectId);
        Check.Equal(
            injectedObjectId,
            registry.GetRequiredPlayerObjectId(session),
            "focused fixtures can still inject an exact object ID");
        registry.Remove(session);
    }

    private static GameCharacter CreateCharacter(
        int characterId,
        int index,
        byte mapId) =>
        new()
        {
            Id = characterId,
            AccountId = 20_000 + index,
            Name = $"ObjectId{index}",
            CreatedUtc = DateTime.UnixEpoch,
            Camp = mapId == GameDefaults.SpartaCapitalMap
                ? GameDefaults.SpartaCamp
                : GameDefaults.AthensCamp,
            CurrentMap = mapId,
            CurrentHp = 1_000,
            MaxHp = 1_000,
            CurrentMp = 1_000,
            MaxMp = 1_000,
            Equipment = string.Empty,
            KitBag = string.Empty
        };

    private sealed class NoopTransport : ILegacyByteTransport
    {
        public string RemoteEndPoint => "player-object-id-check";

        public ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public void MarkAuthenticated()
        {
        }

        public void Disconnect()
        {
        }

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }

}
