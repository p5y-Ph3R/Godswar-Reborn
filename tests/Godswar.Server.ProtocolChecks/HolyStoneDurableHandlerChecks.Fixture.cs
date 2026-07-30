using System.Collections.Immutable;
using System.Reflection;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private const int StoneSlot = 0;
    private static readonly Guid OperationId =
        Guid.Parse("551a75f5-3908-4fd5-adba-431910e561b4");
    private static readonly Guid OutboxEventId =
        Guid.Parse("21a16189-28d4-4986-a4d4-e3ea03b8c3ab");
    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");
    private static readonly MethodInfo InstallNpcCatalogMethod =
        typeof(GameClientHandler).GetMethod(
            "InstallNpcCatalog",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.InstallNpcCatalog was not found.");
    private static readonly CompactItemEntry WeaponBefore =
        CompactItemEntry.Empty with
        {
            Id = 1_100,
            Quality = 3,
            Grade = 5,
            Bound = 1,
            Stack = 1,
            SocketCount = 1
        };
    private static readonly CompactItemEntry WeaponAfter =
        WeaponBefore with
        {
            Socket1EffectId = 200,
            Socket1Level = 1
        };
    private static readonly CompactItemEntry StoneBefore =
        CompactItemEntry.Empty with
        {
            Id = 9_030,
            Quality = 1,
            Grade = 1,
            Bound = 1,
            Stack = 1
        };

    private static async Task<HolyStoneFixture> CreateFixtureAsync(
        HolyStoneExecutionResult replayResult,
        HolyStoneExecutionResult? executeResult = null,
        bool liveAfterMutation = false,
        bool installNpcRoute = true,
        bool projectionFails = false,
        bool providerUnavailable = false,
        uint requestNpcId = HolyStoneProtocol.SpartaNpcId)
    {
        var baseSnapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var beforeSnapshot = WithHolyStoneState(
            baseSnapshot,
            WeaponBefore,
            StoneBefore,
            physicalAttack: 400);
        var afterSnapshot = WithHolyStoneState(
            baseSnapshot,
            WeaponAfter,
            CompactItemEntry.Empty,
            physicalAttack: 91_337);
        var liveSnapshot =
            liveAfterMutation ? afterSnapshot : beforeSnapshot;
        var hydrated =
            CharacterLoadSnapshotHydrator.Hydrate(liveSnapshot)
            ?? throw new InvalidOperationException(
                "Holy Stone fixture did not hydrate.");
        var live = hydrated.Character;
        live.PositionX = 12.5f;
        live.PositionZ = -33.25f;
        live.CurrentHp = 777;
        live.CurrentMp = 333;
        live.Gold = 98_765;

        var npc = CreateHolyStoneNpc(live, requestNpcId);
        var worldContent = CreateWorldContent(npc);
        var transport = new HolyStoneCaptureTransport();
        var session = new ClientSession(transport);
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session,
            baseSnapshot.AccountId,
            live);
        var executor = providerUnavailable
            ? null
            : new HolyStoneExecutor(
                replayResult,
                executeResult ?? replayResult);
        var snapshots = new HolyStoneSnapshotReader(
            afterSnapshot,
            projectionFails);
        var store = new HolyStoneStore();
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            snapshots,
            worldContent,
            holyStoneCommands: executor);
        SetField(
            handler,
            "_account",
            new GameAccount
            {
                Id = baseSnapshot.AccountId,
                Username = "durable-holy-stone-check"
            });
        SetField(handler, "_character", live);

        if (installNpcRoute)
        {
            var catalog =
                await registry.PublishMapNpcDefinitionsAsync(
                    live.CurrentMap,
                    [npc],
                    originSession: null,
                    CancellationToken.None);
            InstallNpcCatalogMethod.Invoke(handler, [catalog]);
            var tracker =
                GetField<WorldSectorVisibilityTracker<
                    NpcSpawnDefinition>>(
                    handler,
                    "_npcVisibility")
                ?? throw new InvalidOperationException(
                    "Holy Stone NPC visibility was not installed.");
            Check.True(
                tracker.TryCalculate(
                    live.PositionX,
                    live.PositionZ,
                    out var delta),
                "Holy Stone NPC visibility calculates");
            tracker.Commit(delta);
        }

        return new HolyStoneFixture(
            session,
            transport,
            handler,
            executor,
            snapshots,
            store,
            registry,
            live);
    }

    private static CharacterAccountSnapshot WithHolyStoneState(
        CharacterAccountSnapshot snapshot,
        CompactItemEntry weapon,
        CompactItemEntry stone,
        int physicalAttack)
    {
        var character = snapshot.Character ??
            throw new InvalidOperationException(
                "Holy Stone fixture requires a character.");
        var equipment = EquipmentSlots.SetSlot(
            character.Loadout.Equipment,
            character.Appearance.Profession,
            EquipmentSlots.Weapon,
            weapon.ToCompactString());
        var bag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            StoneSlot,
            stone.ToCompactString());
        return snapshot with
        {
            Character = character with
            {
                Loadout = character.Loadout with
                {
                    Equipment = equipment,
                    KitBag = bag
                },
                CalculatedStats = character.CalculatedStats with
                {
                    PhysicalAttack = physicalAttack
                }
            }
        };
    }

    private static NpcSpawnDefinition CreateHolyStoneNpc(
        GameCharacter character,
        uint interactionId)
    {
        var athens = interactionId == HolyStoneProtocol.AthensNpcId;
        var city = athens ? "Athens" : "Sparta";
        var key = athens ? "Athens_086" : "Sparta_086";
        return
        new(
            character.CurrentMap,
            city,
            key,
            $"{key}_Male35",
            interactionId,
            character.PositionX,
            character.PositionZ,
            interactionId,
            AppearanceType: 1,
            Facing: 0,
            Detail10077: [],
            Detail10080: []);
    }

    private static IWorldContentReader CreateWorldContent(
        NpcSpawnDefinition npc) =>
        PinnedWorldContentReader.Create(
            "holy-stone-handler-check-v1",
            [npc.MapId],
            [npc],
            [],
            [],
            new DateTimeOffset(
                2026,
                7,
                30,
                0,
                0,
                0,
                TimeSpan.Zero),
            npcTexts:
            [
                new NpcTextDefinition(
                    npc.NpcKey,
                    npc.SceneKey,
                    "Holy Stone Artisan",
                    "Test dialogue")
            ],
            npcDialogueRoutes:
            [
                new NpcDialogueRouteDefinition(
                    npc.NpcKey,
                    npc.NpcKey,
                    HolyStoneProtocol.DialogIndex,
                    NpcDialogueBehavior.HolyStone,
                    ImmutableArray.Create(
                        101,
                        201,
                        301,
                        401,
                        501,
                        601,
                        701))
            ]);

    private static HolyStoneExecutionReceipt CreateMountReceipt(
        int characterId = 19,
        int npcId = HolyStoneCommandEnvelope.SpartaNpcId) =>
        new(
            characterId,
            HolyStoneCommandOperation.Mount,
            npcId,
            HolyStoneCommandEnvelope.DialogIndex,
            HolyStoneCommandResultStatus.Mounted,
            HolyStoneNativeResults.MountedSubId,
            HolyStoneTargetLocation.Equipment,
            EquipmentSlots.Weapon,
            socketIndex: 0,
            targetItemInstanceId: 71,
            WeaponBefore.ToCompactString(),
            WeaponBefore.ToCompactString(),
            WeaponAfter.ToCompactString(),
            StoneSlot,
            stoneItemInstanceId: 72,
            StoneBefore.ToCompactString(),
            StoneBefore.ToCompactString(),
            CompactItemEntry.Empty.ToCompactString(),
            outputKitBagSlot: -1,
            outputItemInstanceId: null,
            outputBeforeCompactItemState: null,
            outputAfterCompactItemState: null,
            goldSpent: 0,
            goldBefore: 10,
            goldAfter: 10,
            walletRevision: 0,
            inventoryRevision: 13,
            auditReference: "audit:holy-stone:handler",
            OutboxEventId);

    private static GamePacket CreateMountPacket(
        Guid? operationId,
        uint npcId = HolyStoneProtocol.SpartaNpcId) =>
        HolyStoneCommandContractChecks.CreatePacket(
            npcId,
            HolyStoneProtocol.MountSubId,
            args =>
            {
                args[HolyStoneProtocol.MountScratchArgumentIndex] = 0;
                args[HolyStoneProtocol.TargetArgumentIndex] =
                    HolyStoneProtocol.ClientEquippedWeaponReference;
                args[HolyStoneProtocol.StoneArgumentIndex] =
                    HolyStoneProtocol.ClientKitBagReferenceBase +
                    StoneSlot;
            },
            operationId);

    private static Task InvokeMountAsync(
        HolyStoneFixture fixture,
        Guid? operationId,
        uint npcId = HolyStoneProtocol.SpartaNpcId) =>
        InvokeAsync(
            fixture.Handler,
            CreateMountPacket(operationId, npcId));

    private static async Task InvokeAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var invocation = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "Holy Stone handler did not return a task.");
        await invocation;
    }

    private static void SetField<T>(
        GameClientHandler handler,
        string name,
        T value)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        field.SetValue(handler, value);
    }

    private static T? GetField<T>(
        GameClientHandler handler,
        string name)
    {
        var field = typeof(GameClientHandler).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"GameClientHandler.{name} was not found.");
        return (T?)field.GetValue(handler);
    }

    private sealed record HolyStoneFixture(
        ClientSession Session,
        HolyStoneCaptureTransport Transport,
        GameClientHandler Handler,
        HolyStoneExecutor? Executor,
        HolyStoneSnapshotReader SnapshotReader,
        HolyStoneStore Store,
        GameSessionRegistry Registry,
        GameCharacter LiveCharacter) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Session);
            await Session.DisposeAsync();
        }
    }

    private sealed class HolyStoneExecutor(
        HolyStoneExecutionResult replayResult,
        HolyStoneExecutionResult executeResult) :
        IHolyStoneCommandExecutor
    {
        public int ReplayCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public HolyStoneCommand? ExecutedCommand { get; private set; }

        public Task<HolyStoneExecutionResult> TryReplayAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            HolyStoneCommandOperation operation,
            Guid clientOperationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplayCount++;
            Check.Equal(7, subject.AccountId, "Holy Stone replay account");
            Check.Equal(19, subject.CharacterId, "Holy Stone replay character");
            Check.Equal(
                (int)HolyStoneCommandOperation.Mount,
                (int)operation,
                "Holy Stone replay operation");
            Check.Equal(
                OperationId,
                clientOperationId,
                "Holy Stone replay UUID");
            return Task.FromResult(replayResult);
        }

        public Task<HolyStoneExecutionResult> ExecuteAsync(
            CommandEnvelope<HolyStoneCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            ExecutedCommand = envelope.Command;
            return Task.FromResult(executeResult);
        }
    }

    private sealed class HolyStoneSnapshotReader(
        CharacterAccountSnapshot snapshot,
        bool fails) : ICharacterSnapshotReader
    {
        public int ReadCount { get; private set; }

        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            if (fails)
            {
                throw new IOException(
                    "Injected Holy Stone projection failure.");
            }
            Check.Equal(snapshot.AccountId, accountId, "projection account");
            return Task.FromResult(snapshot);
        }
    }

    private sealed class HolyStoneStore : GameStoreTestStub
    {
        public int HolyStoneCount { get; private set; }
        public HolyStoneStoreCall? LastCall { get; private set; }
        public Func<GameCharacter?>? ResultFactory { get; set; }
        public GameCharacter? LastResult { get; private set; }

        public override Task<GameCharacter?> ApplyWeaponHolyStoneAsync(
            int accountId,
            int characterId,
            HolyStoneOperation operation,
            HolyStoneTargetMode targetMode,
            int targetKitBagSlot,
            int socketIndex,
            int stoneKitBagSlot,
            int destinationKitBagSlot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HolyStoneCount++;
            LastCall = new HolyStoneStoreCall(
                operation,
                targetMode,
                targetKitBagSlot,
                socketIndex,
                stoneKitBagSlot,
                destinationKitBagSlot);
            LastResult = ResultFactory?.Invoke();
            return Task.FromResult(LastResult);
        }

        public override Task<CharacterStats?> GetCharacterStatsAsync(
            int accountId,
            int characterId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                LastResult is null
                    ? null
                    : CharacterStats.FromCharacter(LastResult));
        }
    }

    private readonly record struct HolyStoneStoreCall(
        HolyStoneOperation Operation,
        HolyStoneTargetMode TargetMode,
        int TargetSlot,
        int SocketIndex,
        int StoneSlot,
        int DestinationSlot);
}
