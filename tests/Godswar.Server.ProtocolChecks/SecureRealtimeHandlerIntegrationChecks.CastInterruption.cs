using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureRealtimeHandlerIntegrationChecks
{
    private static readonly PropertyInfo
        RealtimeHasPendingSkillCastProperty =
            typeof(GameClientHandler).GetProperty(
                "HasPendingSkillCast",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "GameClientHandler.HasPendingSkillCast was not found.");

    private static readonly MethodInfo
        RealtimeStopPendingSkillCastsMethod =
            FindHandlerMethod("StopPendingSkillCastsAsync");

    private static async Task
        CheckAcceptedRealtimeMovementInterruptsCastAsync()
    {
        await using var transport =
            new RealtimeMovementControlTransport();
        await using var session =
            new ClientSession(transport);
        var character = CreateCharacter(
            CharacterId + 50,
            AccountId + 50,
            "RealtimeCastingActor");
        character.CurrentMap = 13;
        character.PositionX = -57f;
        character.PositionZ = 34f;
        var store = new RealtimeBackhaulStore();
        var registry = new GameSessionRegistry();
        registry.InitializeMapMonsters(
            character.CurrentMap,
            [],
            TestTime);
        registry.JoinMap(
            session,
            character.AccountId,
            character,
            WorldObjectIds.ForPlayer(character.Id));
        var handler = CreateRealtimeCastingHandler(
            session,
            store,
            registry,
            character);

        try
        {
            await ProcessTickAsync(handler);
            var initial = transport.Snapshots.Single();
            await InvokePacketAsync(
                handler,
                CreateRealtimeBackhaulCast(character));
            Check.True(
                HasRealtimePendingSkillCast(handler),
                "realtime fixture starts a pending backhaul cast");

            transport.EnqueueMovement(
                CreateIngress(
                    SecureRealtimeTransportSource.Udp,
                    SecureRealtimeMovementIngressKind.Input,
                    transportEpoch: 1,
                    inputId: 1,
                    initial.WorldGeneration,
                    legacyState: 0xCA57_0001,
                    x: -56.5f,
                    z: 34.25f,
                    TimeSpan.FromMilliseconds(100),
                    mapId: character.CurrentMap));
            var effects = await ProcessTickAsync(handler);

            var reliableStream =
                transport.TakeClearLegacyWrites();
            Check.Equal(
                48,
                reliableStream.Length,
                "cast visual and interruption share bounded reliable egress");
            Check.Equal(
                Opcodes.SkillCast,
                BinaryPrimitives.ReadUInt16LittleEndian(
                    reliableStream.AsSpan(2, 2)),
                "realtime cast publishes its visual before movement");
            Check.True(
                reliableStream.AsSpan(40, 8).SequenceEqual(
                    Convert.FromHexString(
                        "0800BB2748140000")),
                "accepted realtime movement emits native interruption");
            Check.True(
                GetEffectPacket(effects, "ViewerMovement") is not null,
                "movement which interrupts the cast remains accepted");
            Check.True(
                character.PositionX == -56.5f &&
                character.PositionZ == 34.25f,
                "accepted realtime movement updates authoritative position");
            Check.True(
                !HasRealtimePendingSkillCast(handler),
                "accepted realtime movement clears pending cast");
            Check.Equal(
                4_000,
                character.CurrentMp,
                "realtime movement interruption consumes no cast MP");
        }
        finally
        {
            await StopRealtimePendingSkillCastsAsync(handler);
            registry.Remove(session);
        }
    }

    private static GameClientHandler CreateRealtimeCastingHandler(
        ClientSession session,
        IGameStore store,
        GameSessionRegistry registry,
        GameCharacter character)
    {
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            backhaulSkillCastTime: TimeSpan.FromSeconds(30));
        SetField(
            handler,
            "_account",
            new GameAccount
            {
                Id = character.AccountId,
                Username = $"realtime-{character.AccountId}"
            });
        SetField(handler, "_character", character);
        SetField(handler, "_registered", true);
        SetField(handler, "_worldPresenceAnnounced", true);
        SetField(
            handler,
            "_npcVisibility",
            new WorldSectorVisibilityTracker<NpcSpawnDefinition>(
                [],
                static npc => npc.ObjectId,
                static npc => npc.X,
                static npc => npc.Z,
                "NPC"));
        return handler;
    }

    private static GamePacket CreateRealtimeBackhaulCast(
        GameCharacter character)
    {
        var packet = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            Opcodes.SkillCast);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4),
            LocalPlayerObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8),
            BackhaulSkillCatalog.CitySkillId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(16),
            LocalPlayerObjectId);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(24),
            character.PositionX);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(28),
            character.PositionZ);
        return new GamePacket(packet);
    }

    private static bool HasRealtimePendingSkillCast(
        GameClientHandler handler) =>
        (bool)(RealtimeHasPendingSkillCastProperty.GetValue(handler)
            ?? throw new InvalidOperationException(
                "GameClientHandler.HasPendingSkillCast returned null."));

    private static async Task StopRealtimePendingSkillCastsAsync(
        GameClientHandler handler)
    {
        var task = RealtimeStopPendingSkillCastsMethod.Invoke(
            handler,
            null) as Task
            ?? throw new InvalidOperationException(
                "GameClientHandler.StopPendingSkillCastsAsync returned no task.");
        await task;
    }

    private sealed class RealtimeBackhaulStore : GameStoreTestStub
    {
        public override Task<IReadOnlyList<SkillState>>
            GetSkillStatesAsync(
                int accountId,
                int characterId,
                CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SkillState>>(
                [new SkillState
                {
                    SkillId = checked((int)
                        BackhaulSkillCatalog.CitySkillId),
                    Level = 1
                }]);
    }
}
