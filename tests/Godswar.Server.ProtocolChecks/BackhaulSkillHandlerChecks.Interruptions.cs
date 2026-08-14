using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulSkillHandlerChecks
{
    private static readonly PropertyInfo HasPendingSkillCastProperty =
        typeof(GameClientHandler).GetProperty(
            "HasPendingSkillCast",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HasPendingSkillCast was not found.");

    private static readonly MethodInfo InterruptPendingSkillCastMethod =
        typeof(GameClientHandler).GetMethod(
            "InterruptPendingSkillCastAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.InterruptPendingSkillCastAsync was not found.");

    private static async Task CheckNativeCastInterruptionAsync()
    {
        await using var fixture = await InterruptFixture.CreateAsync(
            "NativeInterruptedBackhaul");

        await fixture.BeginCastAsync();
        await AssertCastStartedAsync(
            fixture,
            "native interruption");

        await InvokePacketAsync(
            fixture.Handler,
            new GamePacket(
                Convert.FromHexString("0800BB2748140000")));

        await AssertInterruptedAsync(
            fixture,
            "native interruption");
    }

    private static async Task CheckMovementCastInterruptionAsync()
    {
        await using var fixture = await InterruptFixture.CreateAsync(
            "MovementInterruptedBackhaul");

        await fixture.BeginCastAsync();
        await AssertCastStartedAsync(
            fixture,
            "movement interruption");

        await InvokePacketAsync(
            fixture.Handler,
            CreateControlPacket(Opcodes.WalkBegin));

        await AssertInterruptedAsync(
            fixture,
            "movement interruption");

        var movementEcho = await fixture.Socket.ReadPacketAsync();
        Check.Equal(
            Opcodes.WalkBegin,
            ReadUInt16(movementEcho, 2),
            "movement interruption preserves ordinary movement broadcast");

        // The stock client also sends 10171 after locally cancelling on
        // movement. The cast has already been claimed, so the echo must not
        // produce a duplicate notification.
        await InvokePacketAsync(
            fixture.Handler,
            new GamePacket(
                Convert.FromHexString("0800BB2748140000")));
        await Task.Delay(50);
        Check.Equal(
            0,
            fixture.Socket.Available,
            "movement followed by native cancel emits one interruption");
    }

    private static async Task CheckControlStatusCastInterruptionsAsync()
    {
        foreach (var statusId in Enumerable.Range(299, 7)
                     .Select(static value => checked((uint)value)))
        {
            Check.True(
                PlayerSkillCastControlCatalog.ResolveActiveBlock(
                    statusId) == PlayerSkillCastControl.None,
                $"HaltIntonate-only Frozen {statusId} is one-shot");
            Check.True(
                PlayerSkillCastControlCatalog.ResolveAppliedInterruption(
                    statusId) ==
                SkillCastInterruptionReason.Stunned,
                $"HaltIntonate-only Frozen {statusId} interrupts on apply");
        }

        var stunnedStatuses = new uint[]
        {
            330, 331, 400, 401, 402, 407, 408, 564,
            1433, 1436, 1444, 1446, 1447
        };
        foreach (var statusId in stunnedStatuses)
        {
            Check.True(
                PlayerSkillCastControlCatalog.ResolveActiveBlock(
                    statusId) == PlayerSkillCastControl.Stunned,
                $"Status.ini stun control {statusId} blocks casting");
            Check.True(
                PlayerSkillCastControlCatalog.ResolveAppliedInterruption(
                    statusId) ==
                SkillCastInterruptionReason.Stunned,
                $"Status.ini stun control {statusId} interrupts on apply");
        }

        var silencedStatuses = new uint[]
        {
            360, 361, 362, 363, 364, 404, 1448, 1449
        };
        foreach (var statusId in silencedStatuses)
        {
            Check.True(
                PlayerSkillCastControlCatalog.ResolveActiveBlock(
                    statusId) == PlayerSkillCastControl.Silenced,
                $"Status.ini silence control {statusId} blocks casting");
            Check.True(
                PlayerSkillCastControlCatalog.ResolveAppliedInterruption(
                    statusId) ==
                SkillCastInterruptionReason.Silenced,
                $"Status.ini silence control {statusId} interrupts on apply");
        }

        Check.True(
            PlayerSkillCastControlCatalog.ResolveActiveBlock(201) ==
                PlayerSkillCastControl.None,
            "ordinary beneficial status does not block casting");
        Check.True(
            PlayerSkillCastControlCatalog.ResolveAppliedInterruption(201) is
                null,
            "ordinary beneficial status does not interrupt casting");

        await CheckAppliedControlStatusAsync(
            statusId: 299,
            kind: 10,
            expectedControl: PlayerSkillCastControl.None,
            "Frozen");
        await CheckAppliedControlStatusAsync(
            statusId: 331,
            kind: 11,
            expectedControl: PlayerSkillCastControl.Stunned,
            "Stunned");
        await CheckAppliedControlStatusAsync(
            statusId: 360,
            kind: 12,
            expectedControl: PlayerSkillCastControl.Silenced,
            "Silenced");
    }

    private static async Task CheckAppliedControlStatusAsync(
        uint statusId,
        int kind,
        PlayerSkillCastControl expectedControl,
        string description)
    {
        await using var fixture = await InterruptFixture.CreateAsync(
            $"{description}Backhaul");

        await fixture.BeginCastAsync();
        await AssertCastStartedAsync(
            fixture,
            $"{description} status");

        var definition = new SkillStatusEffectDefinition(
            SkillId: 9_999,
            StatusId: statusId,
            Kind: kind,
            Priority: 1,
            Beneficial: false,
            Duration: TimeSpan.FromSeconds(30),
            Cooldown: TimeSpan.Zero,
            HitBonus: 0,
            CriticalAppendBonus: 0);
        var statusNow = DateTimeOffset.UtcNow;
        Check.True(
            await fixture.Registry.ApplyRuntimeStatusAndPublishAsync(
                fixture.Socket.Session,
                definition,
                statusNow,
                $"test-{description.ToLowerInvariant()}",
                CancellationToken.None),
            $"{description} status applies");

        var statusSnapshot = await fixture.Socket.ReadPacketAsync();
        Check.Equal(
            (ushort)10167,
            ReadUInt16(statusSnapshot, 2),
            $"{description} publishes its status snapshot");
        await AssertInterruptedAsync(
            fixture,
            $"{description} status");
        Check.True(
            fixture.Registry.GetPlayerSkillCastControl(
                fixture.Socket.Session,
                statusNow) == expectedControl,
            $"{description} resolves active cast control");

        await fixture.BeginCastAsync();
        var response = await fixture.Socket.ReadPacketAsync();
        if (expectedControl == PlayerSkillCastControl.None)
        {
            Check.Equal(
                Opcodes.SkillCast,
                ReadUInt16(response, 2),
                "HaltIntonate-only Frozen permits a later cast");
            await InvokePacketAsync(
                fixture.Handler,
                new GamePacket(
                    Convert.FromHexString("0800BB2748140000")));
            var interrupted = await fixture.Socket.ReadPacketAsync();
            Check.Equal(
                "0800BB2748140000",
                Convert.ToHexString(interrupted),
                "later Frozen cast remains normally interruptible");
        }
        else
        {
            Check.Equal(
                "0800BB2748140000",
                Convert.ToHexString(response),
                $"{description} blocks a new cast with native notice");
            Check.True(
                !HasPendingSkillCast(fixture.Handler),
                $"{description} creates no pending cast while active");
        }
    }

    private static async Task AssertCastStartedAsync(
        InterruptFixture fixture,
        string description)
    {
        var visual = await fixture.Socket.ReadPacketAsync();
        Check.Equal(
            Opcodes.SkillCast,
            ReadUInt16(visual, 2),
            $"{description} publishes cast visual first");
        Check.Equal(
            BackhaulSkillCatalog.CitySkillId,
            ReadUInt32(visual, 8),
            $"{description} publishes selected skill");
        Check.True(
            HasPendingSkillCast(fixture.Handler),
            $"{description} reserves an authoritative pending cast");
    }

    private static async Task AssertInterruptedAsync(
        InterruptFixture fixture,
        string description)
    {
        var interrupted = await fixture.Socket.ReadPacketAsync();
        Check.Equal(
            "0800BB2748140000",
            Convert.ToHexString(interrupted),
            $"{description} emits native Skill09 interruption frame");
        Check.True(
            !HasPendingSkillCast(fixture.Handler),
            $"{description} clears authoritative pending cast");
        Check.Equal(
            150,
            fixture.Character.CurrentMp,
            $"{description} consumes no MP");
        Check.Equal(
            0,
            fixture.Store.VitalsWrites.Count,
            $"{description} persists no vitals");
        Check.Equal(
            0,
            fixture.Store.PositionWrites.Count,
            $"{description} persists no destination");
        Check.Equal(
            PeloponneseMapId,
            fixture.Character.CurrentMap,
            $"{description} remains on source map");
        Check.True(
            fixture.Registry.GetMapSessions(PeloponneseMapId)
                .Any(context =>
                    ReferenceEquals(
                        context.Session,
                        fixture.Socket.Session) &&
                    context.WorldReady),
            $"{description} preserves ready source membership");
    }

    private static bool HasPendingSkillCast(
        GameClientHandler handler) =>
        (bool)(HasPendingSkillCastProperty.GetValue(handler)
            ?? throw new InvalidOperationException(
                "GameClientHandler.HasPendingSkillCast returned null."));

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

    private sealed class InterruptFixture : IAsyncDisposable
    {
        private InterruptFixture(
            BackhaulSessionSocket socket,
            GameCharacter character,
            BackhaulStore store,
            GameSessionRegistry registry,
            GameClientHandler handler)
        {
            Socket = socket;
            Character = character;
            Store = store;
            Registry = registry;
            Handler = handler;
        }

        public BackhaulSessionSocket Socket { get; }

        public GameCharacter Character { get; }

        public BackhaulStore Store { get; }

        public GameSessionRegistry Registry { get; }

        public GameClientHandler Handler { get; }

        public static async Task<InterruptFixture> CreateAsync(
            string characterName,
            PlayerRuntimeMode playerRuntimeMode =
                PlayerRuntimeMode.Ecs)
        {
            var socket = await BackhaulSessionSocket.CreateAsync();
            var character = CreateCharacter(characterName);
            var store = new BackhaulStore(
                character,
                [new SkillState
                {
                    SkillId = checked((int)
                        BackhaulSkillCatalog.CitySkillId),
                    Level = 1
                }]);
            var registry = CreateRegistry(playerRuntimeMode);
            GameHandlerOwnershipTestFences.Bind(
                registry,
                socket.Session,
                AccountId,
                character);
            registry.JoinMap(
                socket.Session,
                AccountId,
                character,
                WorldObjectIds.ForPlayer(CharacterId),
                worldReady: true,
                joinedAt: TestTime);
            var handler = CreateEnteredHandler(
                socket.Session,
                store,
                registry,
                character,
                backhaulSkillCastTime: TimeSpan.FromSeconds(30));
            registry.RegisterSkillCastInterruptionSink(
                socket.Session,
                (reason, cancellationToken, notificationBarrier) =>
                    InvokeInterruptionSinkAsync(
                        handler,
                        reason,
                        cancellationToken,
                        notificationBarrier));
            return new InterruptFixture(
                socket,
                character,
                store,
                registry,
                handler);
        }

        public Task BeginCastAsync() =>
            InvokePacketAsync(
                Handler,
                CreateSkillCastPacket(
                    BackhaulSkillCatalog.CitySkillId,
                    Character.PositionX,
                    Character.PositionZ,
                    targetX: 1_234f,
                    targetZ: -5_678f));

        public async ValueTask DisposeAsync()
        {
            await StopHandlerAsync(Handler);
            Registry.UnregisterSkillCastInterruptionSink(
                Socket.Session);
            Registry.Remove(Socket.Session);
            await Socket.DisposeAsync();
        }
    }

    private static Task InvokeInterruptionSinkAsync(
        GameClientHandler handler,
        SkillCastInterruptionReason reason,
        CancellationToken cancellationToken,
        Task? notificationBarrier = null) =>
        InterruptPendingSkillCastMethod.Invoke(
            handler,
            [reason, cancellationToken, notificationBarrier]) as Task
        ?? throw new InvalidOperationException(
            "GameClientHandler.InterruptPendingSkillCastAsync returned no task.");
}
