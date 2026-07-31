using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class KitBagItemDeleteDurableHandlerChecks
{
    private const int DeleteSlot = 25;
    private static readonly Guid OperationId =
        Guid.Parse("71df6b36-a4e0-4a5f-9162-022546c02f80");
    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");
    private static readonly CompactItemEntry DeletedItem =
        CompactItemEntry.Empty with
        {
            Id = 4_215,
            Quality = 3,
            Grade = 5,
            Bound = 1,
            Stack = 9
        };
    private static readonly CompactItemEntry ReplacementItem =
        CompactItemEntry.Empty with
        {
            Id = 4_230,
            Quality = 1,
            Grade = 1,
            Stack = 2
        };

    private static DeleteHandlerFixture CreateFixture(
        KitBagItemDeleteExecutionResult replayResult,
        KitBagItemDeleteExecutionResult? executeResult = null,
        CompactItemEntry? liveItem = null,
        CompactItemEntry? persistedItem = null,
        bool projectionFails = false,
        bool providerUnavailable = false,
        IKitBagItemDeleteCommandExecutor? executorOverride = null,
        DeleteStore? store = null)
    {
        var snapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var liveBag = CreateBag(liveItem ?? DeletedItem);
        var persistedBag = CreateBag(
            persistedItem ?? CompactItemEntry.Empty);
        var hydrated = CharacterLoadSnapshotHydrator.Hydrate(
            WithBag(snapshot, liveBag))
            ?? throw new InvalidOperationException(
                "Kit-bag delete fixture did not hydrate.");
        var character = hydrated.Character;
        character.PositionX = 82.5f;
        character.PositionZ = -61.25f;
        character.CurrentHp = 777;
        character.CurrentMp = 333;
        character.Silver = 456_789;
        character.Gold = 98_765;

        var transport = new DeleteCaptureTransport();
        var session = new ClientSession(transport);
        var executor = providerUnavailable
            ? null
            : executorOverride ??
                new DeleteExecutor(
                    replayResult,
                    executeResult ?? replayResult);
        var snapshotReader = new DeleteSnapshotReader(
            WithBag(snapshot, persistedBag),
            projectionFails);
        store ??= new DeleteStore();
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session,
            snapshot.AccountId,
            character);
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            snapshotReader,
            WorldContentReaderTestFixtures.Empty,
            kitBagItemDeleteCommands: executor);
        SetField(
            handler,
            "_account",
            new AccountIdentity(snapshot.AccountId, "durable-delete-check"));
        SetField(handler, "_character", character);
        return new DeleteHandlerFixture(
            session,
            transport,
            handler,
            executor as DeleteExecutor,
            snapshotReader,
            store,
            character,
            liveBag,
            persistedBag);
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

    private static string CreateBag(CompactItemEntry item) =>
        KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            DeleteSlot,
            item.ToCompactString());

    private static KitBagItemDeleteExecutionReceipt CreateReceipt(
        KitBagItemDeleteResultStatus status,
        int characterId = 19,
        int kitBagSlot = DeleteSlot,
        long inventoryRevision = 12)
    {
        var expected = status ==
            KitBagItemDeleteResultStatus.EmptySlot
            ? CompactItemEntry.Empty
            : DeletedItem;
        var authoritative = status switch
        {
            KitBagItemDeleteResultStatus.EmptySlot =>
                CompactItemEntry.Empty,
            KitBagItemDeleteResultStatus.StaleSelection =>
                ReplacementItem,
            _ => DeletedItem
        };
        return new KitBagItemDeleteExecutionReceipt(
            characterId,
            kitBagSlot,
            status,
            expected.ToCompactString(),
            authoritative.ToCompactString(),
            inventoryRevision,
            "audit:kit-bag-delete:handler-check",
            status == KitBagItemDeleteResultStatus.Deleted
                ? Guid.Parse(
                    "4e49b95d-6481-416b-915b-f2373b25b7b4")
                : null);
    }

    private static async Task InvokeDeleteAsync(
        GameClientHandler handler,
        Guid? operationId,
        int packetLength = 28,
        CancellationToken cancellationToken = default)
    {
        var invocation = HandlePacketMethod.Invoke(
            handler,
            [
                CreateDeletePacket(operationId, packetLength),
                cancellationToken
            ]) as Task
            ?? throw new InvalidOperationException(
                "Kit-bag delete handler did not return a task.");
        await invocation;
    }

    private static GamePacket CreateDeletePacket(
        Guid? operationId,
        int packetLength)
    {
        var packet = new byte[packetLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            Opcodes.StorageItem);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, sizeof(uint)),
            0x001A_F948);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(8, sizeof(ushort)),
            DeleteSlot / 24);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(10, sizeof(ushort)),
            DeleteSlot % 24);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(12, sizeof(ushort)),
            ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(14, sizeof(ushort)),
            ushort.MaxValue);
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

    private sealed record DeleteHandlerFixture(
        ClientSession Session,
        DeleteCaptureTransport Transport,
        GameClientHandler Handler,
        DeleteExecutor? Executor,
        DeleteSnapshotReader SnapshotReader,
        DeleteStore Store,
        GameCharacter LiveCharacter,
        string LiveBag,
        string PersistedBag) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() =>
            Session.DisposeAsync();
    }

    private sealed class DeleteExecutor(
        KitBagItemDeleteExecutionResult replayResult,
        KitBagItemDeleteExecutionResult executeResult) :
        IKitBagItemDeleteCommandExecutor
    {
        public int ReplayCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public KitBagItemDeleteCommand? ExecutedCommand
        { get; private set; }

        public Task<KitBagItemDeleteExecutionResult> TryReplayAsync(
            CommandSubject subject,
            PlayerOwnershipFence ownership,
            Guid clientOperationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplayCount++;
            Check.Equal(7, subject.AccountId, "delete replay account");
            Check.Equal(19, subject.CharacterId, "delete replay character");
            Check.Equal(
                OperationId,
                clientOperationId,
                "delete replay operation UUID");
            return Task.FromResult(replayResult);
        }

        public Task<KitBagItemDeleteExecutionResult> ExecuteAsync(
            CommandEnvelope<KitBagItemDeleteCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            ExecutedCommand = envelope.Command;
            return Task.FromResult(executeResult);
        }
    }

    private sealed class DeleteSnapshotReader(
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
                    "Injected kit-bag delete projection failure.");
            }
            Check.Equal(
                snapshot.AccountId,
                accountId,
                "delete projection account");
            return Task.FromResult(snapshot);
        }
    }

    private sealed class DeleteStore : GameStoreTestStub
    {
        public int DeleteCount { get; private set; }
        public GameCharacter? Result { get; set; }

        public override Task<GameCharacter?> DeleteKitBagItemAsync(
            int accountId,
            int characterId,
            int kitBagSlot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeleteCount++;
            return Task.FromResult(Result);
        }
    }
}
