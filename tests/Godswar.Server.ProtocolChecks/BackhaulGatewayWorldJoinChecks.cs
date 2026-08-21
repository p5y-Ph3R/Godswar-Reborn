using System.Buffers.Binary;
using System.Net;
using System.Text;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Backhaul;
using Godswar.Server.Networking.Secure;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulProtocolChecks
{
    private static readonly RealmId CrossServerRealm = new(2);
    private static readonly MapId CrossServerMap = new(40);
    private static readonly WorldInstanceId CrossServerWorld =
        new(Guid.Parse("4f5ac548-3210-4eb5-a422-98f8408f43ce"));
    private static readonly ServerNodeId CrossServerNode =
        new("worker-cross-server");
    private static readonly DateTimeOffset CrossServerTestTime =
        new(2026, 7, 31, 8, 0, 0, TimeSpan.Zero);

    private static async Task CheckGatewayExactWorldJoinAsync()
    {
        await using var registry = new GameSessionRegistry(
            worldInstanceOptions: ExactWorkerOptions());

        var firstCharacter =
            CrossServerCharacter(31_001, 3_001, "CrossOne");
        var firstAdmission = CrossServerAdmission(
            firstCharacter,
            Guid.Parse("00000000-0000-0000-0000-000000003001"));
        await using var firstSession = new ClientSession(
            new GatewayAdmissionTestTransport(firstAdmission));
        Check.True(
            registry.AcceptsGatewayAdmission(firstAdmission),
            "worker accepts its configured exact cross-server route");
        var monster = CrossServerMonster();
        Check.Equal(
            1,
            registry.InitializeMapMonsters(
                firstSession,
                firstCharacter.CurrentMap,
                [monster],
                CrossServerTestTime),
            "pre-join monster bootstrap resolves the admitted exact world");
        registry.JoinGatewayWorld(
            firstSession,
            firstCharacter.AccountId,
            firstCharacter,
            0x8101,
            firstAdmission,
            worldReady: true,
            CrossServerTestTime);

        Check.True(
            registry.TryGetSessionWorldInstanceId(
                firstSession,
                out var firstWorld) &&
            firstWorld == CrossServerWorld &&
            registry.TryGetWorldInstance(
                firstWorld,
                out var descriptor) &&
            descriptor.RealmId == CrossServerRealm &&
            descriptor.MapId == CrossServerMap,
            "gateway join consumes the admitted realm, map, and world ID");
        var directory = registry.GetWorldInstanceDirectorySnapshot();
        Check.True(
            directory.RuntimeCount == 1 &&
            directory.OpenWorldCount == 1,
            "cross-server admission creates no fallback runtime");
        Check.Equal(
            1,
            registry.GetWorldInstancePopulation(CrossServerWorld),
            "exact admitted world contains its first player");
        Check.True(
            registry.GetMapMonsterSnapshots(
                    firstSession,
                    firstCharacter.CurrentMap)
                .Single()
                .ObjectId == monster.ObjectId &&
            registry.GetMapMonsterSnapshots(
                    firstCharacter.CurrentMap)
                .Single()
                .ObjectId == monster.ObjectId,
            "session and process-realm readers resolve the admitted world");

        var secondCharacter =
            CrossServerCharacter(31_002, 3_002, "CrossTwo");
        var secondAdmission = CrossServerAdmission(
            secondCharacter,
            Guid.Parse("00000000-0000-0000-0000-000000003002"));
        await using var secondSession = new ClientSession(
            new GatewayAdmissionTestTransport(secondAdmission));
        registry.JoinGatewayWorld(
            secondSession,
            secondCharacter.AccountId,
            secondCharacter,
            0x8102,
            secondAdmission,
            worldReady: true,
            CrossServerTestTime);
        Check.Equal(
            2,
            registry.GetWorldInstancePopulation(CrossServerWorld),
            "the assigned non-Tempest runtime is reused exactly");

        var wrongRealmCharacter = CrossServerCharacter(
            31_004,
            3_004,
            "WrongRealm",
            RealmId.Tempest);
        var wrongRealmAdmission = CrossServerAdmission(
            wrongRealmCharacter,
            Guid.Parse("00000000-0000-0000-0000-000000003004"));
        await using var wrongRealmSession = new ClientSession(
            new GatewayAdmissionTestTransport(wrongRealmAdmission));
        Check.Throws<InvalidOperationException>(
            () => registry.JoinGatewayWorld(
                wrongRealmSession,
                wrongRealmCharacter.AccountId,
                wrongRealmCharacter,
                0x8104,
                wrongRealmAdmission),
            "gateway world join rejects a cross-realm character");

        var wrongWorldAdmission = CrossServerAdmission(
            CrossServerCharacter(
                31_003,
                3_003,
                "Rejected"),
            Guid.Parse("00000000-0000-0000-0000-000000003003"),
            WorldInstanceId.New());
        await using var rejectedSession = new ClientSession(
            new GatewayAdmissionTestTransport(
                wrongWorldAdmission));
        Check.True(
            !registry.AcceptsGatewayAdmission(wrongWorldAdmission),
            "an unowned world ID is rejected before join");
        Check.Throws<InvalidOperationException>(
            () => registry.JoinGatewayWorld(
                rejectedSession,
                wrongWorldAdmission.AccountId,
                CrossServerCharacter(
                    wrongWorldAdmission.CharacterId,
                    wrongWorldAdmission.AccountId,
                    "Rejected"),
                0x8103,
                wrongWorldAdmission),
            "gateway world join fails closed without map fallback");

        registry.Remove(firstSession);
        registry.Remove(secondSession);

        await CheckLegacyJoinStillUsesTempestAsync();
    }

    private static async Task CheckLegacyJoinStillUsesTempestAsync()
    {
        await using var socket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var registry = new GameSessionRegistry();
        var character =
            CrossServerCharacter(
                31_010,
                3_010,
                "LegacyLocal",
                RealmId.Tempest);

        registry.JoinMap(
            socket.Session,
            character.AccountId,
            character,
            0x8110,
            worldReady: true,
            CrossServerTestTime);
        var context = registry
            .GetMapSessions(character.CurrentMap)
            .Single();
        Check.True(
            context.RealmId == RealmId.Tempest &&
            context.MapId == character.CurrentMap,
            "direct legacy JoinMap preserves its Tempest compatibility route");
        registry.Remove(socket.Session);
    }

    private static WorldInstanceRuntimeOptions ExactWorkerOptions() =>
        new()
        {
            RealmId = CrossServerRealm.Value,
            ServerNodeId = CrossServerNode.ToString(),
            MaximumRuntimes = 4,
            MaximumPlayerAssignments = 8,
            MaximumRetiredInstanceIds = 16,
            DefaultOpenWorldPlayerCapacity = 4,
            MailboxCapacity = 16,
            OwnerInvocationTimeoutMilliseconds = 2_000,
            ShutdownDrainTimeoutMilliseconds = 2_000,
            MaximumFanoutConcurrency = 2,
            StaticOpenWorldInstances =
            [
                new StaticOpenWorldInstanceOptions
                {
                    RealmId = CrossServerRealm.Value,
                    MapId = CrossServerMap.Value,
                    WorldInstanceId =
                        CrossServerWorld.Value.ToString("D")
                }
            ],
            RequireStaticOpenWorldOwnership = true
        };

    private static GatewayWorldAdmission CrossServerAdmission(
        GameCharacter character,
        Guid connectionId,
        WorldInstanceId? world = null) =>
        new(
            Guid.Parse("ac5198ea-d7ae-498c-b65a-15462b224d24"),
            connectionId,
            Guid.Parse("e934642c-c919-4aec-a612-ea42d8589b3b"),
            character.AccountId,
            character.Id,
            character.Name,
            CrossServerRealm,
            CrossServerMap,
            world ?? CrossServerWorld,
            CrossServerNode,
            CrossServerTestTime,
            CrossServerTestTime.AddSeconds(30),
            new IPEndPoint(
                IPAddress.Parse("192.0.2.44"),
                44_000));

    private static GameCharacter CrossServerCharacter(
        int characterId,
        int accountId,
        string name,
        RealmId? realmId = null) =>
        new()
        {
            Id = characterId,
            AccountId = accountId,
            RealmId = realmId ?? CrossServerRealm,
            Name = name,
            CreatedUtc = CrossServerTestTime.UtcDateTime,
            CurrentMap = checked((byte)CrossServerMap.Value),
            PositionX = 5,
            PositionZ = 6,
            Level = 40,
            CurrentHp = 4_000,
            MaxHp = 4_000,
            CurrentMp = 2_000,
            MaxMp = 2_000,
            Equipment = string.Empty,
            KitBag = string.Empty
        };

    private static CapturedMonsterSpawn CrossServerMonster()
    {
        const uint objectId = 0x8201;
        const string template = "CrossRealmMob";
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            10020);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            0x00000212);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20),
            500);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24),
            500);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(28),
            8);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(36),
            9);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(40),
            1);
        Encoding.ASCII.GetBytes(template)
            .CopyTo(packet.AsSpan(44));
        return new CapturedMonsterSpawn(
            CrossServerMap.Value,
            "CrossRealmScene",
            template,
            template,
            objectId,
            8,
            9,
            packet);
    }

    private sealed class GatewayAdmissionTestTransport(
        GatewayWorldAdmission admission) :
        ILegacyByteTransport,
        IAuthenticatedGameTransport
    {
        public string RemoteEndPoint => "authenticated-gateway-test";

        public SecureBoundGamePrincipal BoundGamePrincipal { get; } =
            admission.CreatePrincipal();

        public GatewayWorldAdmission WorldAdmission { get; } =
            admission;

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
