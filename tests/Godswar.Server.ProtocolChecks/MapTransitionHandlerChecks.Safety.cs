using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MapTransitionHandlerChecks
{
    private static async Task RunSafetyChecksAsync()
    {
        await CheckPendingOpcodePolicyAsync();
        await CheckCompletionCancelsTimeoutAsync();
        await CheckRejectedTransferCompensatesOnceAsync();
    }

    private static async Task CheckPendingOpcodePolicyAsync()
    {
        await using var actorSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        await using var targetViewerSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var actor = CreateCharacter(
            CharacterId + 10,
            AccountId + 10,
            "MapOpcodeActor",
            SpartaMapId,
            x: 190f,
            z: -120f);
        var targetViewer = CreateCharacter(
            ViewerCharacterId + 10,
            ViewerAccountId + 10,
            "MapOpcodeViewer",
            SpartaSuburbMapId,
            x: 120f,
            z: -80f);
        var store = new MapTransitionStore(actor);
        var registry = CreateRegistry();
        GameHandlerOwnershipTestFences.Bind(
            registry,
            actorSocket.Session,
            actor.AccountId,
            actor);
        registry.JoinMap(
            actorSocket.Session,
            actor.AccountId,
            actor,
            WorldObjectIds.ForPlayer(actor.Id),
            worldReady: true,
            joinedAt: TestTime);
        registry.JoinMap(
            targetViewerSocket.Session,
            targetViewer.AccountId,
            targetViewer,
            WorldObjectIds.ForPlayer(targetViewer.Id),
            worldReady: true,
            joinedAt: TestTime);
        var handler = CreateEnteredHandler(
            actorSocket.Session,
            store,
            registry,
            actor);
        var outward = Resolve(
            MapTraversalCatalog.Default,
            SpartaMapId,
            SpartaSuburbMapId);

        await InvokePacketAsync(
            handler,
            CreateWalkPacket(
                outward.SourcePortal.X,
                outward.SourcePortal.Z));
        await AssertNextPacketAsync(
            actorSocket,
            PacketBuilder.SceneChange(
                LocalPlayerObjectId,
                outward.TargetArrival.X,
                y: 0f,
                outward.TargetArrival.Z,
                SpartaSuburbMapId),
            "opcode-policy scene change");

        var ping = CreateControlPacket(Opcodes.Ping);
        await InvokePacketAsync(handler, ping);
        await AssertNextPacketAsync(
            actorSocket,
            ping.Buffer,
            "pending transition ping");
        var heartbeat = CreateControlPacket(Opcodes.UiHeartbeat);
        await InvokePacketAsync(handler, heartbeat);
        await AssertNextPacketAsync(
            actorSocket,
            heartbeat.Buffer,
            "pending transition UI heartbeat");

        await InvokePacketAsync(
            handler,
            CreateControlPacket(Opcodes.Talk));
        await Task.Delay(TimeSpan.FromMilliseconds(50));
        Check.Equal(
            0,
            targetViewerSocket.Available,
            "pending transition blocks target-map talk");

        await InvokePacketAsync(
            handler,
            CreateControlPacket(Opcodes.ClientReady));
        Check.True(
            !registry.GetMapSessions(SpartaSuburbMapId)
                .Any(context =>
                    ReferenceEquals(
                        context.Session,
                        actorSocket.Session)),
            "pending policy preserves hidden state after ClientReady");

        await InvokePacketAsync(
            handler,
            CreatePlayerDetailRequest());
        Check.True(
            registry.GetMapSessions(SpartaSuburbMapId)
                .Any(context =>
                    ReferenceEquals(
                        context.Session,
                        actorSocket.Session) &&
                    context.WorldReady),
            "pending policy permits 10007 and 10200 completion");

        await StopHandlerAsync(handler);
        registry.Remove(actorSocket.Session);
        registry.Remove(targetViewerSocket.Session);
    }

    private static async Task CheckCompletionCancelsTimeoutAsync()
    {
        await using var actorSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var actor = CreateCharacter(
            CharacterId + 20,
            AccountId + 20,
            "MapTimeoutActor",
            SpartaMapId,
            x: 190f,
            z: -120f);
        var store = new MapTransitionStore(actor);
        store.BlockNpcSpawnReads();
        var registry = CreateRegistry();
        GameHandlerOwnershipTestFences.Bind(
            registry,
            actorSocket.Session,
            actor.AccountId,
            actor);
        registry.JoinMap(
            actorSocket.Session,
            actor.AccountId,
            actor,
            WorldObjectIds.ForPlayer(actor.Id),
            worldReady: true,
            joinedAt: TestTime);
        var readyTimeout = TimeSpan.FromMilliseconds(75);
        var handler = CreateEnteredHandler(
            actorSocket.Session,
            store,
            registry,
            actor,
            readyTimeout);
        var outward = Resolve(
            MapTraversalCatalog.Default,
            SpartaMapId,
            SpartaSuburbMapId);

        await InvokePacketAsync(
            handler,
            CreateWalkPacket(
                outward.SourcePortal.X,
                outward.SourcePortal.Z));
        await AssertNextPacketAsync(
            actorSocket,
            PacketBuilder.SceneChange(
                LocalPlayerObjectId,
                outward.TargetArrival.X,
                y: 0f,
                outward.TargetArrival.Z,
                SpartaSuburbMapId),
            "timeout-race scene change");
        await InvokePacketAsync(
            handler,
            CreateControlPacket(Opcodes.ClientReady));

        var completion = InvokePacketAsync(
            handler,
            CreatePlayerDetailRequest());
        using var readDeadline = new CancellationTokenSource(
            TimeSpan.FromSeconds(5));
        await store.WaitForNpcSpawnReadAsync(
            readDeadline.Token);
        try
        {
            await Task.Delay(readyTimeout * 3);
            Check.Equal(
                0,
                GetSessionDisconnected(actorSocket.Session),
                "completion owns transition before timeout deadline");
        }
        finally
        {
            store.ReleaseNpcSpawnReads();
        }

        await completion;
        Check.True(
            registry.GetMapSessions(SpartaSuburbMapId)
                .Any(context =>
                    ReferenceEquals(
                        context.Session,
                        actorSocket.Session) &&
                    context.WorldReady),
            "slow completion remains active after old deadline");

        await StopHandlerAsync(handler);
        registry.Remove(actorSocket.Session);
    }

    private static async Task CheckRejectedTransferCompensatesOnceAsync()
    {
        await using var actorSocket =
            await RuntimePolicySessionSocket.CreateAsync();
        var actor = CreateCharacter(
            CharacterId + 30,
            AccountId + 30,
            "MapCompensationActor",
            SpartaMapId,
            x: 190f,
            z: -120f);
        var store = new MapTransitionStore(actor)
        {
            FailPositionWriteAttempt = 2
        };
        var registry = CreateRegistry();
        GameHandlerOwnershipTestFences.Bind(
            registry,
            actorSocket.Session,
            actor.AccountId,
            actor);
        var handler = CreateEnteredHandler(
            actorSocket.Session,
            store,
            registry,
            actor);
        var outward = Resolve(
            MapTraversalCatalog.Default,
            SpartaMapId,
            SpartaSuburbMapId);

        var compensationFailed = false;
        try
        {
            await InvokePacketAsync(
                handler,
                CreateWalkPacket(
                    outward.SourcePortal.X,
                    outward.SourcePortal.Z));
        }
        catch (InvalidOperationException error)
            when (error.Message.Contains(
                "Injected map-position compensation failure",
                StringComparison.Ordinal))
        {
            compensationFailed = true;
        }

        Check.True(
            compensationFailed,
            "rejected transfer surfaces failed compensation");
        Check.Equal(
            2,
            store.PositionWriteAttempts,
            "rejected transfer attempts compensation exactly once");
        Check.Equal(
            1,
            GetSessionDisconnected(actorSocket.Session),
            "failed compensation disconnects the session");

        await StopHandlerAsync(handler);
    }

    private static GameSessionRegistry CreateRegistry() =>
        new(
            store: null,
            zodiacEnergyOptions: null,
            monsterRuntimeMode: MonsterRuntimeMode.Ecs,
            playerRuntimeMode: PlayerRuntimeMode.Ecs);

    private static int GetSessionDisconnected(
        ClientSession session)
    {
        var field = typeof(ClientSession).GetField(
            "_disconnected",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "ClientSession._disconnected was not found.");
        return (int)(field.GetValue(session) ??
            throw new InvalidOperationException(
                "ClientSession._disconnected returned null."));
    }
}
