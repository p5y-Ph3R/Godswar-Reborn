using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class InstanceCallerHandlerChecks
{
    private static async Task<InstanceCallerFixture> CreateFixtureAsync(
        int? level = null,
        bool transitionReady = false)
    {
        var snapshot = CharacterSnapshotContractChecks.CreateValidSnapshot();
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(snapshot) ??
            throw new InvalidOperationException(
                "Instance Caller fixture did not hydrate.");
        var character = hydrated.Character;
        if (level.HasValue)
        {
            character.Level = level.Value;
        }
        var npc = new NpcSpawnDefinition(
            character.CurrentMap,
            "Athens",
            "Athens_060",
            "Athens_060_FemMale17",
            InstanceCallerProtocol.AthensNpcId,
            character.PositionX,
            character.PositionZ,
            InstanceCallerProtocol.AthensNpcId,
            AppearanceType: 1,
            Facing: 1.7f,
            Detail10077: [],
            Detail10080: []);
        var route = new NpcDialogueRouteDefinition(
            npc.NpcKey,
            npc.NpcKey,
            InstanceCallerProtocol.DialogIndex,
            NpcDialogueBehavior.InstanceCaller,
            ImmutableArray.CreateRange(
                InstanceCallerProtocol.InitialMenuSubIds));
        var worldContent = PinnedWorldContentReader.Create(
            "instance-caller-handler-v1",
            [npc.MapId, 200, 204],
            [npc],
            [],
            [],
            new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero),
            npcTexts:
            [
                new NpcTextDefinition(
                    npc.NpcKey,
                    npc.SceneKey,
                    "Instance Caller",
                    "Medusa handler test dialogue")
            ],
            npcDialogueRoutes: [route]);
        var transport = new FactionCrierCaptureTransport();
        var session = new ClientSession(transport);
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session,
            snapshot.AccountId,
            character);
        registry.JoinMap(
            session,
            snapshot.AccountId,
            character,
            objectId: 700019);
        var handler = new GameClientHandler(
            session,
            new InstanceCallerGameStore(),
            registry,
            new InstanceCallerSnapshotReader(snapshot),
            worldContent);
        SetHandlerField(
            handler,
            "_account",
            new AccountIdentity(snapshot.AccountId, "instance-caller-check"));
        SetHandlerField(handler, "_character", character);
        if (transitionReady)
        {
            SetHandlerField(handler, "_registered", true);
            SetHandlerField(handler, "_worldPresenceAnnounced", true);
        }

        var catalog = await registry.PublishMapNpcDefinitionsAsync(
            character.CurrentMap,
            [npc],
            originSession: null,
            CancellationToken.None);
        InstallNpcCatalogMethod.Invoke(handler, [catalog]);
        var visibility = GetHandlerField<WorldSectorVisibilityTracker<
            NpcSpawnDefinition>>(handler, "_npcVisibility") ??
            throw new InvalidOperationException(
                "Instance Caller visibility was not installed.");
        Check.True(
            visibility.TryCalculate(
                character.PositionX,
                character.PositionZ,
                out var delta),
            "Instance Caller visibility calculates");
        visibility.Commit(delta);

        return new InstanceCallerFixture(
            session,
            transport,
            handler,
            registry,
            character,
            character.CurrentMap);
    }

    private static GamePacket CreateActionPacket(
        int subId,
        int? pathChoice = null,
        int? duplicateDialogIndex = null,
        int? declaredLength = null,
        int? bufferLength = null)
    {
        var bytes = new byte[InstanceCallerProtocol.ActionPacketBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.NpcFunctionAction);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4),
            InstanceCallerProtocol.AthensNpcId);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(8),
            InstanceCallerProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(12),
            duplicateDialogIndex ?? InstanceCallerProtocol.DialogIndex);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), subId);
        for (var index = 0;
             index < InstanceCallerProtocol.FunctionArgumentCount;
             index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(20 + (index * sizeof(int))),
                index == 0 && pathChoice.HasValue ? pathChoice.Value : -1);
        }
        Array.Resize(
            ref bytes,
            bufferLength ?? InstanceCallerProtocol.ActionPacketBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            checked((ushort)(declaredLength ?? bytes.Length)));
        return new GamePacket(bytes);
    }

    private static GamePacket CreateRepetitionResponse(
        int repetitionId,
        int invitationId,
        bool accepted)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            checked((ushort)bytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.RepetitionResponse);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(4),
            repetitionId);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(8),
            invitationId);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(12),
            accepted ? 1 : 0);
        return new GamePacket(bytes);
    }

    private static GamePacket CreateControlPacket(ushort opcode)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            checked((ushort)bytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            opcode);
        return new GamePacket(bytes);
    }

    private static GamePacket CreatePlayerDetailRequest()
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            checked((ushort)bytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.PlayerDetailRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4),
            0x1448);
        return new GamePacket(bytes);
    }

    private static (int RepetitionId, int InvitationId)
        ReadRepetitionInvitation(byte[] packet)
    {
        Check.True(
            packet.Length ==
                12 + CharacterSnapshotLimits.CharacterNameLength &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet) == packet.Length &&
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(2)) == Opcodes.RepetitionInvitation,
            "Medusa confirmation uses the native invitation packet");
        return (
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)),
            BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(8)));
    }

    private static async Task InvokeAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var task = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task ??
            throw new InvalidOperationException(
                "Instance Caller handler did not return a task.");
        await task;
    }

    private static WorldInstanceId GetSourceInstanceId(
        InstanceCallerFixture fixture)
    {
        Check.True(
            fixture.Registry.TryGetSessionWorldInstanceId(
                fixture.Session,
                out var instanceId),
            "fixture session has a source world instance");
        return instanceId;
    }

    private static InstanceCallerPageContext? GetPageContext(
        GameClientHandler handler) =>
        GetHandlerField<InstanceCallerPageContext>(
            handler,
            "_instanceCallerPageContext");

    private static void SetPageContext(
        GameClientHandler handler,
        InstanceCallerPageContext context) =>
        SetHandlerField(handler, "_instanceCallerPageContext", context);

    private static MethodInfo FindHandlerMethod(string name) =>
        typeof(GameClientHandler).GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            $"GameClientHandler.{name} was not found.");

    private static void SetHandlerField<T>(
        GameClientHandler handler,
        string name,
        T value) =>
        (typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
         throw new InvalidOperationException(
             $"GameClientHandler.{name} was not found."))
        .SetValue(handler, value);

    private static T? GetHandlerField<T>(
        GameClientHandler handler,
        string name) => (T?)(typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
        throw new InvalidOperationException(
            $"GameClientHandler.{name} was not found.")).GetValue(handler);

    private sealed class InstanceCallerSnapshotReader(
        CharacterAccountSnapshot snapshot) : ICharacterSnapshotReader
    {
        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check.Equal(snapshot.AccountId, accountId,
                "Instance Caller snapshot account");
            return Task.FromResult(snapshot);
        }
    }

    private sealed class InstanceCallerGameStore : GameStoreTestStub
    {
        public override Task<CharacterStats?> GetCharacterStatsAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<CharacterStats?>(null);
        }

        public override Task SaveCharacterPositionAsync(
            int accountId,
            int characterId,
            byte currentMap,
            float positionX,
            float positionZ,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed record InstanceCallerFixture(
        ClientSession Session,
        FactionCrierCaptureTransport Transport,
        GameClientHandler Handler,
        GameSessionRegistry Registry,
        GameCharacter Character,
        byte SourceMapId) : IAsyncDisposable
    {
        public IReadOnlyList<byte[]> ReadPackets() =>
            Transport.ReadLegacyPackets();

        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Session);
            await Session.DisposeAsync();
        }
    }
}
