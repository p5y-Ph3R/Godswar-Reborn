using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PlayerOwnershipGameplayRaceChecks
{
    private const int AccountId = 7;
    private const int CharacterId = 13;
    private const uint MonsterObjectId = 10013;
    private const uint InitialMonsterHealth = 1_000;
    private const uint LocalPlayerObjectId = 0x00001448;
    private static readonly BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task RunAsync()
    {
        var registry = new GameSessionRegistry(
            store: null,
            zodiacEnergyOptions: null,
            MonsterRuntimeMode.Ecs,
            PlayerRuntimeMode.Legacy);
        var staleTransport = new ScriptedLegacyByteTransport();
        var currentTransport = new ScriptedLegacyByteTransport();
        var observerTransport = new ScriptedLegacyByteTransport();
        await using var staleSession = CreateSession(staleTransport);
        await using var currentSession = CreateSession(currentTransport);
        await using var observerSession = CreateSession(observerTransport);

        var staleOwnership =
            new PlayerOwnershipFence(Guid.NewGuid(), 1);
        var observerOwnership =
            new PlayerOwnershipFence(Guid.NewGuid(), 1);
        var staleCharacter = CreateCharacter(
            AccountId,
            CharacterId,
            "stale-gameplay",
            staleOwnership);
        var observerCharacter = CreateCharacter(
            AccountId + 1,
            CharacterId + 1,
            "observer-gameplay",
            observerOwnership);

        registry.InitializeMapMonsters(
            staleCharacter.CurrentMap,
            [CreateMonster()],
            DateTimeOffset.UtcNow);
        Register(
            registry,
            AccountId,
            staleSession,
            staleCharacter,
            staleOwnership);
        Register(
            registry,
            observerCharacter.AccountId,
            observerSession,
            observerCharacter,
            observerOwnership);
        await using (var visibility =
            await registry.BeginMonsterVisibilityTransitionAsync(
                staleSession,
                staleCharacter.CurrentMap,
                staleCharacter.PositionX,
                staleCharacter.PositionZ,
                CancellationToken.None) ??
            throw new InvalidOperationException(
                "Stale combat visibility fixture was unavailable."))
        {
            visibility.Commit();
        }

        var handler = CreateAuthenticatedWorldHandler(
            registry,
            staleSession,
            staleCharacter);
        Check.True(
            ReferenceEquals(
                staleSession,
                registry.ReplaceAccountSession(
                    AccountId,
                    currentSession)),
            "gameplay replacement identifies the stale session");

        var observerBytes = observerTransport.WrittenBytes.Length;
        await InvokePacketAsync(handler, CreateControlPacket(Opcodes.Talk));
        Check.Equal(
            observerBytes,
            observerTransport.WrittenBytes.Length,
            "top-level ownership gate rejects stale talk packet");
        Check.Equal(
            1,
            staleTransport.DisconnectCount,
            "top-level ownership gate disconnects stale packet source");

        await InvokeBroadcastAsync(
            handler,
            CreateControlPacket(Opcodes.Talk));
        Check.Equal(
            observerBytes,
            observerTransport.WrittenBytes.Length,
            "chat effect boundary rejects a delayed stale broadcast");

        Check.True(
            registry.TryGetMonsterSnapshot(
                staleCharacter.CurrentMap,
                MonsterObjectId,
                out var before),
            "stale combat fixture exposes its monster");
        await InvokeBasicAttackAsync(
            handler,
            CreateBasicAttackPacket(
                staleCharacter.PositionX,
                staleCharacter.PositionZ));
        Check.True(
            registry.TryGetMonsterSnapshot(
                staleCharacter.CurrentMap,
                MonsterObjectId,
                out var after),
            "stale combat fixture retains its monster");
        Check.Equal(
            before.CurrentHealth,
            after.CurrentHealth,
            "combat effect boundary rejects stale-session damage");
        Check.Equal(
            before.HealthRevision,
            after.HealthRevision,
            "stale-session combat cannot advance monster revision");
    }

    private static ClientSession CreateSession(
        ScriptedLegacyByteTransport transport) =>
        new(
            transport,
            endpointRole: NetworkEndpointRole.Game);

    private static GameCharacter CreateCharacter(
        int accountId,
        int characterId,
        string name,
        PlayerOwnershipFence ownership) =>
        new()
        {
            Id = characterId,
            AccountId = accountId,
            Name = name,
            Camp = GameDefaults.SpartaCamp,
            CurrentMap = GameDefaults.SpartaCapitalMap,
            PositionX = 0f,
            PositionZ = 0f,
            Profession = 1,
            Level = 80,
            CurrentHp = 500,
            MaxHp = 500,
            CurrentMp = 500,
            MaxMp = 500,
            CalculatedStats = new CharacterStats
            {
                PhysicalAttack = 100
            },
            CheckpointOwnerId = ownership.OwnerId,
            CheckpointOwnerGeneration = ownership.Generation
        };

    private static void Register(
        GameSessionRegistry registry,
        int accountId,
        ClientSession session,
        GameCharacter character,
        PlayerOwnershipFence ownership)
    {
        registry.ReplaceAccountSession(accountId, session);
        Check.True(
            registry.TryBindAccountSessionOwnership(
                accountId,
                session,
                ownership),
            "gameplay fixture binds its exact ownership fence");
        registry.JoinMap(
            session,
            accountId,
            character,
            WorldObjectIds.ForPlayer(character.Id),
            worldReady: true);
    }

    private static GameClientHandler CreateAuthenticatedWorldHandler(
        GameSessionRegistry registry,
        ClientSession session,
        GameCharacter character)
    {
        var handler = new GameClientHandler(
            session,
            new EmptyStore(),
            registry,
            CharacterSnapshotReaderTestFixtures.Unused,
            WorldContentReaderTestFixtures.Empty);
        RequiredField("_account").SetValue(
            handler,
            new GameAccount
            {
                Id = character.AccountId,
                Username = character.Name
            });
        RequiredField("_character").SetValue(handler, character);
        RequiredField("_accountSessionRegistered").SetValue(
            handler,
            true);
        RequiredField("_registered").SetValue(handler, true);
        RequiredField("_worldPresenceAnnounced").SetValue(
            handler,
            true);
        return handler;
    }

    private static GamePacket CreateControlPacket(ushort opcode)
    {
        var packet = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            opcode);
        return new GamePacket(packet);
    }

    private static GamePacket CreateBasicAttackPacket(
        float x,
        float z)
    {
        var packet = new byte[32];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 32);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.BasicAttack);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            LocalPlayerObjectId);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(8),
            x);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(12),
            0f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(16),
            z);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20),
            MonsterObjectId);
        return new GamePacket(packet);
    }

    private static CapturedMonsterSpawn CreateMonster()
    {
        const string templateKey = "A_normal_stub_001";
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
            MonsterObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20),
            InitialMonsterHealth);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24),
            InitialMonsterHealth);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(28),
            1f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(32),
            0f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(36),
            0f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(40),
            1f);
        Encoding.ASCII.GetBytes(templateKey)
            .CopyTo(packet.AsSpan(44));
        return new CapturedMonsterSpawn(
            MapId: GameDefaults.SpartaCapitalMap,
            SceneKey: "Sparta",
            templateKey,
            templateKey,
            MonsterObjectId,
            X: 1f,
            Z: 0f,
            packet);
    }

    private static Task InvokePacketAsync(
        GameClientHandler handler,
        GamePacket packet) =>
        InvokeTaskAsync(
            handler,
            "HandlePacketAsync",
            packet,
            CancellationToken.None);

    private static Task InvokeBroadcastAsync(
        GameClientHandler handler,
        GamePacket packet) =>
        InvokeTaskAsync(
            handler,
            "BroadcastToCurrentMapAsync",
            packet,
            CancellationToken.None);

    private static Task InvokeBasicAttackAsync(
        GameClientHandler handler,
        GamePacket packet) =>
        InvokeTaskAsync(
            handler,
            "HandleBasicAttackAsync",
            packet,
            CancellationToken.None);

    private static async Task InvokeTaskAsync(
        GameClientHandler handler,
        string methodName,
        params object[] arguments)
    {
        var method = typeof(GameClientHandler).GetMethod(
            methodName,
            PrivateInstance) ??
            throw new InvalidOperationException(
                $"GameClientHandler.{methodName} is missing.");
        try
        {
            await ((Task?)method.Invoke(handler, arguments) ??
                throw new InvalidOperationException(
                    $"{methodName} returned no task."));
        }
        catch (TargetInvocationException error)
            when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }

    private static FieldInfo RequiredField(string name) =>
        typeof(GameClientHandler).GetField(
            name,
            PrivateInstance) ??
        throw new InvalidOperationException(
            $"GameClientHandler.{name} was not found.");

    private sealed class EmptyStore : GameStoreTestStub
    {
    }
}
