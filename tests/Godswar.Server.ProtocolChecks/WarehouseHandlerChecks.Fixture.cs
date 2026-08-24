using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WarehouseHandlerChecks
{
    private const int AccountId = 7;
    private const int CharacterId = 19;
    private const int WarehouseSlot = 0;
    private const int BagFourWarehouseSlot = 120;
    private const int KitBagSlot = 0;
    private const long BeforeInventoryRevision = 20;
    private const long AfterInventoryRevision = 21;
    private static readonly Guid OperationId =
        Guid.Parse("c8fd3b21-95ab-4c2b-83d4-4740d2a25b93");
    private static readonly Guid OutboxEventId =
        Guid.Parse("b0ef851e-8716-4c71-b797-cc996bd506be");
    private static readonly MethodInfo HandlePacketMethod =
        FindHandlerMethod("HandlePacketAsync");
    private static readonly MethodInfo InstallNpcCatalogMethod =
        FindHandlerMethod("InstallNpcCatalog");

    private static readonly CompactItemEntry StorageKey =
        CompactItemEntry.Empty with
        {
            Id = 4102,
            Quality = 1,
            Grade = 1,
            Stack = 1
        };

    private static async Task<WarehouseFixture> CreateFixtureAsync(
        CharacterAccountSnapshot initialCharacter,
        IEnumerable<CharacterAccountSnapshot> characterReads,
        IEnumerable<WarehouseSnapshot> warehouseReads,
        WarehouseTransferExecutor executor)
    {
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(
            initialCharacter) ?? throw new InvalidOperationException(
                "Warehouse handler fixture did not hydrate.");
        var character = hydrated.Character;
        var npc = CreateWarehouseNpc(character);
        var managerNpc = CreateManagerNpc(character);
        var worldContent = PinnedWorldContentReader.Create(
            "warehouse-handler-v1",
            [npc.MapId],
            [npc, managerNpc],
            [],
            [],
            new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero));
        var transport = new WarehouseCaptureTransport();
        var session = new ClientSession(transport);
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session,
            initialCharacter.AccountId,
            character);
        var characters = new WarehouseCharacterSnapshotReader(
            characterReads);
        var warehouses = new WarehouseSnapshotReader(warehouseReads);
        var handler = new GameClientHandler(
            session,
            new WarehouseGameStore(),
            registry,
            characters,
            worldContent,
            warehouseSnapshots: warehouses,
            warehouseTransferCommands: executor);
        SetHandlerField(
            handler,
            "_account",
            new AccountIdentity(AccountId, "warehouse-handler-check"));
        SetHandlerField(handler, "_character", character);

        var catalog = await registry.PublishMapNpcDefinitionsAsync(
            character.CurrentMap,
            [npc, managerNpc],
            originSession: null,
            CancellationToken.None);
        InstallNpcCatalogMethod.Invoke(handler, [catalog]);
        var visibility = GetHandlerField<WorldSectorVisibilityTracker<
            NpcSpawnDefinition>>(handler, "_npcVisibility") ??
            throw new InvalidOperationException(
                "Warehouse NPC visibility was not installed.");
        Check.True(
            visibility.TryCalculate(
                character.PositionX,
                character.PositionZ,
                out var delta),
            "warehouse NPC visibility calculates");
        visibility.Commit(delta);

        return new WarehouseFixture(
            session,
            transport,
            handler,
            executor,
            characters,
            warehouses,
            registry,
            character);
    }

    private static CharacterAccountSnapshot CharacterSnapshot(
        string kitBag,
        long inventoryRevision,
        string token)
    {
        var snapshot = CharacterSnapshotContractChecks.CreateValidSnapshot();
        return snapshot with
        {
            ProviderSnapshotToken = token,
            Character = snapshot.Character! with
            {
                Loadout = snapshot.Character.Loadout with
                {
                    KitBag = kitBag,
                    InventoryRevision = inventoryRevision
                }
            }
        };
    }

    private static WarehouseSnapshot WarehouseSnapshot(
        long inventoryRevision,
        bool containsKey,
        int capacity = WarehouseCapacityPolicy.DefaultCapacity,
        int itemSlot = WarehouseSlot) => new(
        AccountId,
        CharacterId,
        capacity,
        WarehouseRevision: 0,
        inventoryRevision,
        containsKey
            ? [new WarehouseItemSnapshot(
                itemSlot,
                StorageKey.ToCompactString())]
            : []);

    private static WarehouseTransferExecutionReceipt DepositReceipt(
        int warehouseSlot = WarehouseSlot,
        int capacity = WarehouseCapacityPolicy.DefaultCapacity) => new(
        CharacterId,
        WarehouseTransferOperation.Deposit,
        warehouseSlot,
        KitBagSlot,
        DestinationWarehouseSlot: -1,
        ActualWarehouseSlot: warehouseSlot,
        ActualKitBagSlot: KitBagSlot,
        WarehouseTransferResultStatus.Deposited,
        MovedQuantity: 1,
        capacity,
        WarehouseRevision: 0,
        AfterInventoryRevision,
        [new WarehouseItemMutation(
            ItemInstanceId: 7001,
            ItemId: 4102,
            WarehouseInventoryLocation.KitBag,
            BeforeSlot: KitBagSlot,
            BeforeStack: 1,
            WarehouseInventoryLocation.Warehouse,
            AfterSlot: warehouseSlot,
            AfterStack: 1)],
        AuditReference: "warehouse-handler-deposit",
        OutboxEventId);

    private static NpcSpawnDefinition CreateWarehouseNpc(
        GameCharacter character) => new(
        character.CurrentMap,
        "Athens",
        "Athens_025",
        "Athens_025_Female1",
        WarehouseNpcProtocol.AthensWarehouseNpcId,
        character.PositionX,
        character.PositionZ,
        WarehouseNpcProtocol.AthensWarehouseNpcId,
        AppearanceType: 1,
        Facing: 1.7f,
        Detail10077: [],
        Detail10080: []);

    private static GamePacket CreateWarehouseClick()
        => CreateNpcClick(WarehouseNpcProtocol.AthensWarehouseNpcId);

    private static GamePacket CreateManagerClick()
        => CreateNpcClick(WarehouseNpcProtocol.AthensManagerNpcId);

    private static GamePacket CreateNpcClick(uint npcId)
    {
        var bytes = new byte[48];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 48);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.NpcDialogOpen);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4),
            npcId);
        for (var offset = 8; offset < bytes.Length; offset++)
        {
            bytes[offset] = checked((byte)(0x40 + offset));
        }
        return new GamePacket(bytes);
    }

    private static GamePacket CreateWarehousePageRequest()
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.NpcDialogPageRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4),
            WarehouseNpcProtocol.AthensWarehouseNpcId);
        return new GamePacket(bytes);
    }

    private static GamePacket CreateWarehousePageRequest(int page)
    {
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 12);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.NpcDialogPageRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(4),
            WarehouseNpcProtocol.AthensWarehouseNpcId);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), page);
        return new GamePacket(bytes);
    }

    private static async Task OpenWarehouseAsync(WarehouseFixture fixture)
    {
        await InvokeAsync(fixture.Handler, CreateWarehouseClick());
        await InvokeAsync(fixture.Handler, CreateWarehousePageRequest());
    }

    private static GamePacket CreateDepositRequest(
        int warehouseSlot = WarehouseSlot)
    {
        var bytes = new byte[WarehouseWireProtocol.TransferPacketBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            WarehouseWireProtocol.TransferPacketBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(2),
            Opcodes.WarehouseTransfer);
        BinaryPrimitives.WriteInt16LittleEndian(
            bytes.AsSpan(4),
            checked((short)warehouseSlot));
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(6), 0);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8), 0);
        bytes[16] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(18),
            (ushort)WarehouseStorageType.Normal);
        return new GamePacket(bytes, OperationId);
    }

    private static async Task InvokeAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var task = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task ??
            throw new InvalidOperationException(
                "Warehouse handler did not return a task.");
        await task;
    }

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

    private sealed class WarehouseTransferExecutor :
        IWarehouseTransferCommandExecutor
    {
        public WarehouseTransferExecutionResult ReplayResult { get; init; } =
            WarehouseTransferExecutionResult.Terminal(
                WarehouseTransferExecutionDisposition.ReplayNotFound);

        public WarehouseTransferExecutionResult? ExecuteResult { get; init; }

        public int ExpectedWarehouseSlot { get; init; } = WarehouseSlot;

        public int ReplayCount { get; private set; }

        public int ExecuteCount { get; private set; }

        public CommandEnvelope<WarehouseTransferCommand>? Envelope
        {
            get;
            private set;
        }

        public Task<WarehouseTransferExecutionResult> TryReplayAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            WarehouseTransferReplayIntent intent,
            WarehouseOperationIdentity identity,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplayCount++;
            Check.True(
                subject == new CommandSubject(AccountId, CharacterId) &&
                intent.RealmId == 1 &&
                intent.Operation == WarehouseTransferOperation.Deposit &&
                intent.WarehouseSlot == ExpectedWarehouseSlot &&
                intent.KitBagSlot == KitBagSlot &&
                identity.OperationId == OperationId,
                "warehouse replay identity is server-bound and wire-stable");
            return Task.FromResult(ReplayResult);
        }

        public Task<WarehouseTransferExecutionResult> ExecuteAsync(
            CommandEnvelope<WarehouseTransferCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            Envelope = envelope;
            return Task.FromResult(
                ExecuteResult ?? throw new InvalidOperationException(
                    "Warehouse execution was not configured."));
        }
    }

    private sealed class WarehouseSnapshotReader(
        IEnumerable<WarehouseSnapshot> snapshots) : IWarehouseSnapshotReader
    {
        private readonly Queue<WarehouseSnapshot> _snapshots = new(snapshots);

        public int ReadCount { get; private set; }

        public Task<WarehouseSnapshot?> ReadAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            Check.True(
                subject == new CommandSubject(AccountId, CharacterId),
                "warehouse snapshot subject is authenticated");
            return Task.FromResult<WarehouseSnapshot?>(
                _snapshots.Count > 0
                    ? _snapshots.Dequeue()
                    : throw new InvalidOperationException(
                        "Warehouse snapshot fixture was exhausted."));
        }
    }

    private sealed class WarehouseCharacterSnapshotReader(
        IEnumerable<CharacterAccountSnapshot> snapshots) :
        ICharacterSnapshotReader
    {
        private readonly Queue<CharacterAccountSnapshot> _snapshots =
            new(snapshots);

        public int ReadCount { get; private set; }

        public Task<CharacterAccountSnapshot> ReadAsync(
            int accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            Check.Equal(AccountId, accountId,
                "warehouse character projection account");
            return Task.FromResult(
                _snapshots.Count > 0
                    ? _snapshots.Dequeue()
                    : throw new InvalidOperationException(
                        "Warehouse character snapshot fixture was exhausted."));
        }
    }

    private sealed class WarehouseGameStore : GameStoreTestStub;

    private sealed record WarehouseFixture(
        ClientSession Session,
        WarehouseCaptureTransport Transport,
        GameClientHandler Handler,
        WarehouseTransferExecutor Executor,
        WarehouseCharacterSnapshotReader Characters,
        WarehouseSnapshotReader Warehouses,
        GameSessionRegistry Registry,
        GameCharacter Character) : IAsyncDisposable
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
