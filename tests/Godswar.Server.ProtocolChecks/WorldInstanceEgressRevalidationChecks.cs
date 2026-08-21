using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;
using WorldMapId = Godswar.Server.Domain.World.Instances.MapId;

namespace Godswar.Server.ProtocolChecks;

internal static class WorldInstanceEgressRevalidationChecks
{
    public const string CheckName =
        "B18B world-instance egress revalidation";

    private const byte SharedMapId = 40;
    private static readonly DateTimeOffset TestTime =
        new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);

    public static async Task RunAsync()
    {
        var blockerTransport =
            new ControlledLegacyByteTransport(blockWrites: true);
        var movingTransport =
            new ControlledLegacyByteTransport();
        await using var blockerSession =
            new ClientSession(blockerTransport);
        await using var movingSession =
            new ClientSession(movingTransport);
        await using var registry = new GameSessionRegistry(
            worldInstanceOptions: new WorldInstanceRuntimeOptions
            {
                RealmId = 1,
                MaximumRuntimes = 4,
                MaximumPlayerAssignments = 8,
                MaximumRetiredInstanceIds = 16,
                DefaultOpenWorldPlayerCapacity = 8,
                MailboxCapacity = 16,
                OwnerInvocationTimeoutMilliseconds = 2_000,
                ShutdownDrainTimeoutMilliseconds = 2_000,
                MaximumFanoutConcurrency = 1
            });

        var source = await CreateDungeonAsync(registry);
        var destination = await CreateDungeonAsync(registry);
        registry.JoinWorldInstance(
            session: blockerSession,
            accountId: 901,
            character:
                CreateCharacter(9_001, 901, "EgressBlocker"),
            objectId: 0x7101,
            instanceId: source,
            worldReady: true,
            joinedAt: TestTime);
        registry.JoinWorldInstance(
            session: movingSession,
            accountId: 902,
            character:
                CreateCharacter(9_002, 902, "EgressMover"),
            objectId: 0x7102,
            instanceId: source,
            worldReady: true,
            joinedAt: TestTime);

        var sourceOrder =
            registry.GetWorldInstanceSessions(source);
        Check.True(
            sourceOrder.Count == 2 &&
            ReferenceEquals(
                sourceOrder[0].Session,
                blockerSession) &&
            ReferenceEquals(
                sourceOrder[1].Session,
                movingSession),
            "fanout fixture has deterministic object-ID order");

        var broadcast = registry.BroadcastToWorldInstanceAsync(
            source,
            PacketBuilder.RemoveWorldObjects(0x71FF),
            CancellationToken.None,
            label: "EgressRevalidation");
        try
        {
            await blockerTransport.WriteStarted.WaitAsync(
                TimeSpan.FromSeconds(5));

            Check.True(
                registry.TryTransferWorldInstance(
                    movingSession,
                    source,
                    destination,
                    targetX: 10f,
                    targetZ: 11f),
                "recipient transfers after source fanout snapshot");
            Check.True(
                registry.TryMarkWorldReady(
                    movingSession,
                    new Dictionary<uint, long>(),
                    out var destinationPrerequisites,
                    TestTime.AddSeconds(1)) &&
                destinationPrerequisites.Count == 0,
                "recipient becomes ready in the empty destination");
            Check.True(
                registry.TryTransferWorldInstance(
                    movingSession,
                    destination,
                    source,
                    targetX: 12f,
                    targetZ: 13f),
                "recipient returns to the same source identity");
            var knownSourcePlayers =
                registry.GetWorldInstanceSessions(source)
                    .ToDictionary(
                        context => context.ObjectId,
                        context => context.WorldRevision);
            Check.True(
                registry.TryMarkWorldReady(
                    movingSession,
                    knownSourcePlayers,
                    out var sourcePrerequisites,
                    TestTime.AddSeconds(2)) &&
                sourcePrerequisites.Count == 0,
                "recipient is ready again with a newer world revision");
        }
        finally
        {
            blockerTransport.ReleaseWrites();
        }

        Check.Equal(
            1,
            await broadcast,
            "fanout sends only the recipient whose route stayed current");
        Check.Equal(
            1,
            blockerTransport.WriteCount,
            "stable recipient receives the source packet");
        Check.Equal(
            0,
            movingTransport.WriteCount,
            "round-tripped recipient rejects the stale source revision");
    }

    private static async Task<WorldInstanceId> CreateDungeonAsync(
        GameSessionRegistry registry)
    {
        var result = await registry.CreateLocalWorldInstanceAsync(
            RealmId.Tempest,
            new WorldMapId(SharedMapId),
            InstanceKind.Dungeon,
            playerCapacity: 8);
        return result.Runtime?.InstanceId ??
            throw new InvalidOperationException(
                "Dungeon creation returned no runtime.");
    }

    private static GameCharacter CreateCharacter(
        int characterId,
        int accountId,
        string name) =>
        new()
        {
            Id = characterId,
            AccountId = accountId,
            Name = name,
            CreatedUtc = TestTime.UtcDateTime,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = SharedMapId,
            PositionX = 5f,
            PositionZ = 6f,
            Level = 40,
            CurrentHp = 4_000,
            MaxHp = 4_000,
            CurrentMp = 2_000,
            MaxMp = 2_000,
            Equipment = string.Empty,
            KitBag = string.Empty
        };
}
