using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class KitBagItemMoveDurableHandlerChecks
{
    private const int SourceSlot = 25;
    private const int DestinationSlot = 50;
    private static readonly Guid OperationId =
        Guid.Parse("5f478587-99e7-47da-964d-2d769c9f4717");
    private static readonly Guid OutboxEventId =
        Guid.Parse("c3e69876-eaee-4532-8063-fb2365ca3790");
    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");
    private static readonly CompactItemEntry SourceItem =
        CompactItemEntry.Empty with
        {
            Id = 4_215,
            Quality = 3,
            Grade = 5,
            Bound = 1,
            Stack = 9
        };
    private static readonly CompactItemEntry DestinationItem =
        CompactItemEntry.Empty with
        {
            Id = 4_230,
            Quality = 2,
            Grade = 4,
            Bound = 1,
            Stack = 2
        };
    private static readonly CompactItemEntry ReplacementItem =
        CompactItemEntry.Empty with
        {
            Id = 9_950,
            Quality = 1,
            Grade = 1,
            Stack = 1
        };

    private static MoveHandlerFixture CreateFixture(
        KitBagItemMoveExecutionResult replayResult,
        KitBagItemMoveExecutionResult? executeResult = null,
        CompactItemEntry? liveSource = null,
        CompactItemEntry? liveDestination = null,
        CompactItemEntry? persistedSource = null,
        CompactItemEntry? persistedDestination = null,
        bool projectionFails = false,
        bool providerUnavailable = false,
        bool executionFails = false,
        MoveStore? store = null)
    {
        var snapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var liveBag = CreateBag(
            liveSource ?? SourceItem,
            liveDestination ?? CompactItemEntry.Empty);
        var persistedBag = CreateBag(
            persistedSource ?? CompactItemEntry.Empty,
            persistedDestination ?? SourceItem);
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(
            WithBag(snapshot, liveBag))
            ?? throw new InvalidOperationException(
                "Kit-bag move fixture did not hydrate.");
        var character = hydrated.Character;
        character.PositionX = 82.5f;
        character.PositionZ = -61.25f;
        character.CurrentHp = 777;
        character.CurrentMp = 333;
        character.Silver = 456_789;
        character.Gold = 98_765;
        var originalEquipment = character.Equipment;

        var transport = new MoveCaptureTransport();
        var session = new ClientSession(transport);
        var executor = providerUnavailable
            ? null
            : new MoveExecutor(
                replayResult,
                executeResult ?? replayResult,
                executionFails);
        var snapshotReader = new MoveSnapshotReader(
            WithBag(snapshot, persistedBag),
            projectionFails);
        store ??= new MoveStore();
        var handler = new GameClientHandler(
            session,
            store,
            new GameSessionRegistry(),
            snapshotReader,
            WorldContentReaderTestFixtures.Empty,
            kitBagItemMoveCommands: executor);
        SetField(
            handler,
            "_account",
            new GameAccount
            {
                Id = snapshot.AccountId,
                Username = "durable-move-check"
            });
        SetField(handler, "_character", character);
        return new MoveHandlerFixture(
            session,
            transport,
            handler,
            executor,
            snapshotReader,
            store,
            character,
            persistedBag,
            originalEquipment);
    }

    private static CharacterAccountSnapshot WithBag(
        CharacterAccountSnapshot snapshot,
        string kitBag) =>
        snapshot with
        {
            Character = snapshot.Character! with
            {
                Loadout = snapshot.Character.Loadout with
                {
                    KitBag = kitBag
                }
            }
        };

    private static string CreateBag(
        CompactItemEntry source,
        CompactItemEntry destination)
    {
        var bag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            SourceSlot,
            source.ToCompactString());
        return KitBagSlots.SetSlot(
            bag,
            DestinationSlot,
            destination.ToCompactString());
    }

    private static KitBagItemMoveExecutionReceipt CreateReceipt(
        KitBagItemMoveResultStatus status,
        int characterId = 19,
        int sourceSlot = SourceSlot,
        int destinationSlot = DestinationSlot,
        long inventoryRevision = 13)
    {
        var expectedSource = status ==
            KitBagItemMoveResultStatus.EmptySource
            ? CompactItemEntry.Empty
            : SourceItem;
        var expectedDestination = status ==
            KitBagItemMoveResultStatus.Swapped
            ? DestinationItem
            : CompactItemEntry.Empty;
        var authoritativeSource = status ==
            KitBagItemMoveResultStatus.StaleSource
            ? ReplacementItem
            : expectedSource;
        var authoritativeDestination = status ==
            KitBagItemMoveResultStatus.StaleDestination
            ? DestinationItem
            : expectedDestination;
        var success = status is
            KitBagItemMoveResultStatus.Moved or
            KitBagItemMoveResultStatus.Swapped;
        return new KitBagItemMoveExecutionReceipt(
            characterId,
            sourceSlot,
            destinationSlot,
            status,
            expectedSource.ToCompactString(),
            expectedDestination.ToCompactString(),
            authoritativeSource.ToCompactString(),
            authoritativeDestination.ToCompactString(),
            inventoryRevision,
            "audit:kit-bag-move:handler-check",
            success ? OutboxEventId : null);
    }

    private static async Task InvokeMoveAsync(
        GameClientHandler handler,
        Guid? operationId,
        int packetLength = 20,
        int sourceSlot = SourceSlot,
        int destinationSlot = DestinationSlot,
        CancellationToken cancellationToken = default)
    {
        var invocation = HandlePacketMethod.Invoke(
            handler,
            [
                CreateMovePacket(
                    operationId,
                    packetLength,
                    sourceSlot,
                    destinationSlot),
                cancellationToken
            ]) as Task
            ?? throw new InvalidOperationException(
                "Kit-bag move handler did not return a task.");
        await invocation;
    }

    private static GamePacket CreateMovePacket(
        Guid? operationId,
        int packetLength,
        int sourceSlot,
        int destinationSlot)
    {
        if (packetLength < 20)
        {
            throw new ArgumentOutOfRangeException(nameof(packetLength));
        }

        var packet = new byte[packetLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            Opcodes.StorageItem);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, sizeof(uint)),
            0x5876_DBF0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(8, sizeof(ushort)),
            checked((ushort)(sourceSlot / 24)));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(10, sizeof(ushort)),
            checked((ushort)(sourceSlot % 24)));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(12, sizeof(ushort)),
            checked((ushort)(destinationSlot / 24)));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(14, sizeof(ushort)),
            checked((ushort)(destinationSlot % 24)));
        if (packetLength == 80)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(16, sizeof(ushort)),
                0xAC74);
            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(18, sizeof(ushort)),
                0x673E);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(16, sizeof(ushort)),
                ushort.MaxValue);
            BinaryPrimitives.WriteUInt16LittleEndian(
                packet.AsSpan(18, sizeof(ushort)),
                ushort.MaxValue);
        }
        return new GamePacket(packet, operationId);
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

    private sealed record MoveHandlerFixture(
        ClientSession Session,
        MoveCaptureTransport Transport,
        GameClientHandler Handler,
        MoveExecutor? Executor,
        MoveSnapshotReader SnapshotReader,
        MoveStore Store,
        GameCharacter LiveCharacter,
        string PersistedBag,
        string OriginalEquipment) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() =>
            Session.DisposeAsync();
    }

    private sealed class MoveExecutor(
        KitBagItemMoveExecutionResult replayResult,
        KitBagItemMoveExecutionResult executeResult,
        bool executeFails) :
        IKitBagItemMoveCommandExecutor
    {
        public int ReplayCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public KitBagItemMoveCommand? ExecutedCommand
        { get; private set; }

        public Task<KitBagItemMoveExecutionResult> TryReplayAsync(
            CommandSubject subject,
            Guid clientOperationId,
            int sourceKitBagSlot,
            int destinationKitBagSlot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplayCount++;
            Check.Equal(7, subject.AccountId, "move replay account");
            Check.Equal(19, subject.CharacterId, "move replay character");
            Check.Equal(
                OperationId,
                clientOperationId,
                "move replay operation UUID");
            Check.Equal(
                SourceSlot,
                sourceKitBagSlot,
                "move replay source slot");
            Check.Equal(
                DestinationSlot,
                destinationKitBagSlot,
                "move replay destination slot");
            return Task.FromResult(replayResult);
        }

        public Task<KitBagItemMoveExecutionResult> ExecuteAsync(
            CommandEnvelope<KitBagItemMoveCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            ExecutedCommand = envelope.Command;
            if (executeFails)
            {
                throw new IOException(
                    "Injected uncertain kit-bag move commit.");
            }
            return Task.FromResult(executeResult);
        }
    }

    private sealed class MoveSnapshotReader(
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
                    "Injected kit-bag move projection failure.");
            }
            Check.Equal(
                snapshot.AccountId,
                accountId,
                "move projection account");
            return Task.FromResult(snapshot);
        }
    }

    private sealed class MoveStore : GameStoreTestStub
    {
        public int MoveCount { get; private set; }
        public GameCharacter? Result { get; set; }

        public override Task<GameCharacter?> MoveKitBagItemAsync(
            int accountId,
            int characterId,
            int sourceSlot,
            int destinationSlot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MoveCount++;
            return Task.FromResult(Result);
        }
    }
}
