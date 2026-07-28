using System.Buffers.Binary;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulSkillHandlerChecks
{
    private const uint LethalAttackMonsterObjectId = 94_013;

    private static readonly FieldInfo RegistryMapsField =
        typeof(GameSessionRegistry).GetField(
            "_maps",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameSessionRegistry._maps was not found.");

    private static readonly FieldInfo PlayerStatusStatesField =
        typeof(GameSessionRegistry).GetField(
            "_playerStatusStates",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameSessionRegistry._playerStatusStates was not found.");

    private static readonly FieldInfo PendingSkillCastField =
        typeof(GameClientHandler).GetField(
            "_pendingSkillCast",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler._pendingSkillCast was not found.");

    private static readonly MethodInfo ProcessMonsterAttackMethod =
        typeof(GameSessionRegistry).GetMethod(
            "ProcessMonsterAttackAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameSessionRegistry.ProcessMonsterAttackAsync was not found.");

    public static async Task
        RunAuthoritativeInterruptionBoundariesAsync()
    {
        await CheckLethalMonsterAttackInterruptionAsync(
            PlayerRuntimeMode.Legacy);
        await CheckLethalMonsterAttackInterruptionAsync(
            PlayerRuntimeMode.Ecs);
        await CheckFrozenStatusOrderingAndClaimAsync();
    }

    private static async Task
        CheckLethalMonsterAttackInterruptionAsync(
            PlayerRuntimeMode playerRuntimeMode)
    {
        await using var fixture =
            await BoundaryFixture.CreateAsync(
                playerRuntimeMode,
                $"Lethal{playerRuntimeMode}Backhaul",
                currentHp: 1);
        var lifeBeforeAttack =
            fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session);
        var vitalsBeforeAttack = fixture.Character.VitalsRevision;
        var interruptionSinkCalls = 0;
        fixture.RegisterInterruptionSink(
            (reason, cancellationToken, notificationBarrier) =>
            {
                Check.True(
                    reason == SkillCastInterruptionReason.Death,
                    $"{playerRuntimeMode} lethal attack requests death interruption");
                var interruption = InvokeInterruptionSinkAsync(
                    fixture.Handler,
                    reason,
                    cancellationToken,
                    notificationBarrier);
                Interlocked.Increment(ref interruptionSinkCalls);
                Check.True(
                    IsPendingInterruptionClaimed(fixture.Handler),
                    $"{playerRuntimeMode} lethal attack synchronously claims the cast");
                Check.Equal(
                    1,
                    fixture.Character.CurrentHp,
                    $"{playerRuntimeMode} claims before lethal HP commit");
                Check.Equal(
                    vitalsBeforeAttack,
                    fixture.Character.VitalsRevision,
                    $"{playerRuntimeMode} claims before lethal vitals revision");
                Check.Equal(
                    lifeBeforeAttack,
                    fixture.Registry.GetPlayerLifeRevision(
                        fixture.Socket.Session),
                    $"{playerRuntimeMode} claims before death life revision");
                return interruption;
            });

        await fixture.BeginCastAsync();
        await AssertBoundaryCastStartedAsync(
            fixture,
            $"{playerRuntimeMode} lethal attack");

        await InvokeLethalMonsterAttackAsync(fixture);

        var packets = new[]
        {
            await fixture.Socket.ReadPacketAsync(),
            await fixture.Socket.ReadPacketAsync(),
            await fixture.Socket.ReadPacketAsync(),
            await fixture.Socket.ReadPacketAsync()
        };
        Check.Equal(
            "0800BB2748140000",
            Convert.ToHexString(packets[0]),
            $"{playerRuntimeMode} lethal attack emits interruption before combat");
        Check.Equal(
            1,
            packets.Count(packet =>
                ReadUInt16(packet, 2) ==
                Opcodes.SkillCastInterrupt),
            $"{playerRuntimeMode} lethal attack emits one native interruption");
        Check.True(
            packets.Skip(1).All(packet =>
                ReadUInt16(packet, 2) !=
                Opcodes.SkillCastInterrupt),
            $"{playerRuntimeMode} combat frames contain no duplicate interruption");
        Check.Equal(
            1,
            interruptionSinkCalls,
            $"{playerRuntimeMode} lethal attack claims one interruption");
        Check.Equal(
            0,
            fixture.Character.CurrentHp,
            $"{playerRuntimeMode} lethal attack commits zero HP");
        Check.Equal(
            lifeBeforeAttack + 1,
            fixture.Registry.GetPlayerLifeRevision(
                fixture.Socket.Session),
            $"{playerRuntimeMode} lethal attack advances life once");
        Check.Equal(
            150,
            fixture.Character.CurrentMp,
            $"{playerRuntimeMode} interrupted cast consumes no MP");
        Check.Equal(
            0,
            fixture.Store.VitalsWrites.Count,
            $"{playerRuntimeMode} interrupted cast persists no MP");
        Check.Equal(
            0,
            fixture.Store.PositionWrites.Count,
            $"{playerRuntimeMode} interrupted cast applies no backhaul");
        Check.Equal(
            PeloponneseMapId,
            fixture.Character.CurrentMap,
            $"{playerRuntimeMode} interrupted cast remains on source map");
        Check.True(
            !HasPendingSkillCast(fixture.Handler),
            $"{playerRuntimeMode} lethal attack clears the pending cast");
        await Task.Delay(25);
        Check.Equal(
            0,
            fixture.Socket.Available,
            $"{playerRuntimeMode} lethal attack leaves no delayed cast effect");
    }

    private static async Task CheckFrozenStatusOrderingAndClaimAsync()
    {
        await using var fixture =
            await BoundaryFixture.CreateAsync(
                PlayerRuntimeMode.Ecs,
                "FrozenBoundaryBackhaul");
        const int frozenKind = 27;
        var sinkCalls = 0;
        fixture.RegisterInterruptionSink(
            (reason, cancellationToken, notificationBarrier) =>
            {
                Check.True(
                    reason == SkillCastInterruptionReason.Stunned,
                    "Frozen 299 requests a stun interruption");
                var interruption = InvokeInterruptionSinkAsync(
                    fixture.Handler,
                    reason,
                    cancellationToken,
                    notificationBarrier);
                Interlocked.Increment(ref sinkCalls);
                Check.True(
                    IsPendingInterruptionClaimed(fixture.Handler),
                    "Frozen 299 synchronously claims the pending cast");
                Check.True(
                    !HasRawRuntimeStatus(
                        fixture.Registry,
                        fixture.Socket.Session,
                        frozenKind),
                    "Frozen 299 claims before its runtime-status mutation");
                Check.Equal(
                    0,
                    fixture.Socket.Available,
                    "Frozen 299 claims before status publication");
                return interruption;
            });

        await fixture.BeginCastAsync();
        await AssertBoundaryCastStartedAsync(
            fixture,
            "Frozen 299 boundary");

        var definition = new SkillStatusEffectDefinition(
            SkillId: 9_998,
            StatusId: 299,
            Kind: frozenKind,
            Priority: 1,
            Beneficial: false,
            Duration: TimeSpan.FromSeconds(30),
            Cooldown: TimeSpan.Zero,
            HitBonus: 0,
            CriticalAppendBonus: 0);
        Check.True(
            await fixture.Registry.ApplyRuntimeStatusAndPublishAsync(
                fixture.Socket.Session,
                definition,
                TestTime,
                "frozen-boundary",
                CancellationToken.None),
            "Frozen 299 application succeeds");

        var status = await fixture.Socket.ReadPacketAsync();
        var interruption = await fixture.Socket.ReadPacketAsync();
        Check.Equal(
            (ushort)10167,
            ReadUInt16(status, 2),
            "Frozen 299 publishes status before interruption");
        Check.Equal(
            "0800BB2748140000",
            Convert.ToHexString(interruption),
            "Frozen 299 follows status with native interruption");
        Check.Equal(
            1,
            sinkCalls,
            "Frozen 299 claims exactly one interruption");
        Check.True(
            HasRawRuntimeStatus(
                fixture.Registry,
                fixture.Socket.Session,
                frozenKind),
            "Frozen 299 mutation is committed after the claim");
        Check.True(
            fixture.Registry.GetPlayerSkillCastControl(
                fixture.Socket.Session,
                TestTime) == PlayerSkillCastControl.None,
            "one-shot Frozen 299 does not block a later cast");
        Check.Equal(
            150,
            fixture.Character.CurrentMp,
            "Frozen 299 interruption consumes no cast MP");
        Check.Equal(
            0,
            fixture.Store.PositionWrites.Count,
            "Frozen 299 interruption applies no backhaul");
        Check.True(
            !HasPendingSkillCast(fixture.Handler),
            "Frozen 299 clears the claimed cast");
        await Task.Delay(25);
        Check.Equal(
            0,
            fixture.Socket.Available,
            "Frozen 299 emits one interruption notification");
    }

    private static async Task InvokeLethalMonsterAttackAsync(
        BoundaryFixture fixture)
    {
        var monster =
            fixture.Registry
                .GetMapMonsterSnapshots(PeloponneseMapId)
                .Single(snapshot =>
                    snapshot.ObjectId ==
                    LethalAttackMonsterObjectId);
        var attack = new MonsterRuntimeUpdate(
            MonsterRuntimeUpdateKind.Attacked,
            monster,
            TargetCharacterId: fixture.Character.Id,
            TargetX: fixture.Character.PositionX,
            TargetZ: fixture.Character.PositionZ,
            TargetObjectId:
                WorldObjectIds.ForPlayer(fixture.Character.Id),
            TargetLifeRevision:
                fixture.Registry.GetPlayerLifeRevision(
                    fixture.Socket.Session),
            TargetVitalsRevision:
                fixture.Character.VitalsRevision,
            AttackEventId: 1);
        var task = ProcessMonsterAttackMethod.Invoke(
            fixture.Registry,
            [
                GetRegistryMap(
                    fixture.Registry,
                    PeloponneseMapId),
                attack,
                CancellationToken.None
            ]) as Task
            ?? throw new InvalidOperationException(
                "ProcessMonsterAttackAsync returned no task.");
        await task;
    }

    private static async Task AssertBoundaryCastStartedAsync(
        BoundaryFixture fixture,
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

    private static MapInstance GetRegistryMap(
        GameSessionRegistry registry,
        byte mapId)
    {
        var maps =
            (ConcurrentDictionary<byte, MapInstance>)
            (RegistryMapsField.GetValue(registry)
             ?? throw new InvalidOperationException(
                 "GameSessionRegistry._maps returned null."));
        return maps.TryGetValue(mapId, out var map)
            ? map
            : throw new InvalidOperationException(
                $"Registry map {mapId} was not found.");
    }

    private static bool IsPendingInterruptionClaimed(
        GameClientHandler handler)
    {
        var pending = PendingSkillCastField.GetValue(handler)
            ?? throw new InvalidOperationException(
                "Expected a pending cast at the interruption boundary.");
        return (bool)(pending.GetType().GetProperty(
                "InterruptionClaimed",
                BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(pending)
            ?? throw new InvalidOperationException(
                "PendingSkillCast.InterruptionClaimed was not found."));
    }

    private static bool HasRawRuntimeStatus(
        GameSessionRegistry registry,
        Networking.ClientSession session,
        int kind)
    {
        var states = (IEnumerable)(
            PlayerStatusStatesField.GetValue(registry)
            ?? throw new InvalidOperationException(
                "GameSessionRegistry._playerStatusStates returned null."));
        foreach (var entry in states)
        {
            var entryType = entry!.GetType();
            if (!ReferenceEquals(
                    entryType.GetProperty("Key")?.GetValue(entry),
                    session))
            {
                continue;
            }

            var state = entryType.GetProperty("Value")?.GetValue(entry)
                ?? throw new InvalidOperationException(
                    "Player status state entry has no value.");
            var statuses = state.GetType().GetProperty(
                    "RuntimeStatuses",
                    BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(state) as IDictionary
                ?? throw new InvalidOperationException(
                    "PlayerStatusState.RuntimeStatuses was not found.");
            return statuses.Contains(kind);
        }

        return false;
    }

    private static CapturedMonsterSpawn CreateLethalAttackMonster()
    {
        const string templateKey = "CastBoundaryMonster";
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            10020);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            0x00000212);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            LethalAttackMonsterObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, 4),
            1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20, 4),
            237);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24, 4),
            237);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(28, 4),
            -56f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(36, 4),
            34f);
        BinaryPrimitives.WriteSingleLittleEndian(
            packet.AsSpan(40, 4),
            1f);
        Encoding.ASCII.GetBytes(templateKey)
            .CopyTo(packet.AsSpan(44));
        return new CapturedMonsterSpawn(
            PeloponneseMapId,
            "Peloponnese",
            templateKey,
            templateKey,
            LethalAttackMonsterObjectId,
            -56f,
            34f,
            packet);
    }

    private sealed class BoundaryFixture : IAsyncDisposable
    {
        private BoundaryFixture(
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

        public static async Task<BoundaryFixture> CreateAsync(
            PlayerRuntimeMode playerRuntimeMode,
            string characterName,
            int currentHp = 2_000)
        {
            var socket = await BackhaulSessionSocket.CreateAsync();
            var character = CreateCharacter(characterName);
            character.CurrentHp = currentHp;
            var store = new BackhaulStore(
                character,
                [new SkillState
                {
                    SkillId = checked((int)
                        BackhaulSkillCatalog.CitySkillId),
                    Level = 1
                }]);
            var registry = new GameSessionRegistry(
                store: null,
                zodiacEnergyOptions: null,
                MonsterRuntimeMode.Ecs,
                playerRuntimeMode);
            registry.InitializeMapMonsters(
                PeloponneseMapId,
                [CreateLethalAttackMonster()],
                TestTime);
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
                backhaulSkillCastTime:
                    TimeSpan.FromSeconds(30));
            return new BoundaryFixture(
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

        public void RegisterInterruptionSink(
            Func<
                SkillCastInterruptionReason,
                CancellationToken,
                Task?,
                Task> sink) =>
            Registry.RegisterSkillCastInterruptionSink(
                Socket.Session,
                sink);

        public async ValueTask DisposeAsync()
        {
            await StopHandlerAsync(Handler);
            Registry.UnregisterSkillCastInterruptionSink(
                Socket.Session);
            Registry.Remove(Socket.Session);
            await Socket.DisposeAsync();
        }
    }
}
