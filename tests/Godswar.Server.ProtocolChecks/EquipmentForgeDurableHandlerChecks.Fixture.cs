using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
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

internal static partial class EquipmentForgeDurableHandlerChecks
{
    private static readonly Guid OperationId =
        Guid.Parse("2ce5dfd4-d8e8-4ed3-a497-0197a6210f42");
    private static readonly MethodInfo HandlePacketMethod =
        typeof(GameClientHandler).GetMethod(
            "HandlePacketAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.HandlePacketAsync was not found.");

    private static readonly CompactItemEntry EquipmentBefore =
        CompactItemEntry.Empty with
        {
            Id = 10_001,
            Quality = 1,
            Grade = 1,
            Stack = 1
        };
    private static readonly CompactItemEntry EquipmentAfter =
        EquipmentBefore with { Quality = 2 };
    private static readonly CompactItemEntry PrimaryBefore =
        CompactItemEntry.Empty with
        {
            Id = 4_210,
            Quality = 1,
            Grade = 1,
            Stack = 2
        };
    private static readonly CompactItemEntry PrimaryAfter =
        PrimaryBefore with { Stack = 1 };

    private static ForgeHandlerFixture CreateFixture(
        EquipmentForgeExecutionResult replayResult,
        EquipmentForgeExecutionResult? executeResult = null,
        bool stageSelections = true,
        bool projectionFails = false,
        bool providerUnavailable = false,
        IEquipmentForgeCommandExecutor? executorOverride = null,
        ForgeStore? store = null,
        CompactItemEntry? persistedEquipment = null,
        CompactItemEntry? persistedPrimary = null,
        int persistedSilver = 800)
    {
        var baseSnapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var beforeBag = CreateBag(
            EquipmentBefore,
            PrimaryBefore);
        var afterBag = CreateBag(
            persistedEquipment ?? EquipmentAfter,
            persistedPrimary ?? PrimaryAfter);
        var liveSnapshot = WithWalletAndBag(
            baseSnapshot,
            silver: 1_000,
            beforeBag);
        var persistedSnapshot = WithWalletAndBag(
            baseSnapshot,
            persistedSilver,
            afterBag);
        var hydrated =
            CharacterLoadSnapshotHydrator.Hydrate(liveSnapshot)
            ?? throw new InvalidOperationException(
                "Forge handler fixture did not hydrate.");
        var character = hydrated.Character;
        character.PositionX = 321.25f;
        character.PositionZ = -222.5f;
        character.CurrentHp = 777;
        character.CurrentMp = 333;
        character.Gold = 888;

        var transport = new ForgeCaptureTransport();
        var session = new ClientSession(transport);
        var executor = providerUnavailable
            ? null
            : executorOverride ??
                new ForgeExecutor(
                    replayResult,
                    executeResult ?? replayResult);
        var snapshotReader = new ForgeSnapshotReader(
            persistedSnapshot,
            projectionFails);
        store ??= new ForgeStore();
        var handler = new GameClientHandler(
            session,
            store,
            new GameSessionRegistry(),
            snapshotReader,
            WorldContentReaderTestFixtures.Empty,
            equipmentForgeCommands: executor);
        SetField(
            handler,
            "_account",
            new GameAccount
            {
                Id = baseSnapshot.AccountId,
                Username = "durable-forge-check"
            });
        SetField(handler, "_character", character);
        if (stageSelections)
        {
            StageForgeSelections(
                handler,
                baseSnapshot.AccountId,
                character.Id);
        }

        return new ForgeHandlerFixture(
            session,
            transport,
            handler,
            executor as ForgeExecutor,
            snapshotReader,
            store,
            character,
            beforeBag,
            afterBag);
    }

    private static CharacterAccountSnapshot WithWalletAndBag(
        CharacterAccountSnapshot snapshot,
        int silver,
        string kitBag) =>
        snapshot with
        {
            Character = snapshot.Character! with
            {
                Wallet = snapshot.Character!.Wallet with
                {
                    Silver = silver
                },
                Loadout = snapshot.Character.Loadout with
                {
                    KitBag = kitBag
                }
            }
        };

    private static string CreateBag(
        CompactItemEntry equipment,
        CompactItemEntry primary)
    {
        var bag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            0,
            equipment.ToCompactString());
        return KitBagSlots.SetSlot(
            bag,
            1,
            primary.ToCompactString());
    }

    private static void StageForgeSelections(
        GameClientHandler handler,
        int accountId,
        int characterId)
    {
        SetField(
            handler,
            "_forgeEquipment",
            new ForgeSlotSelection(0, EquipmentBefore, 1));
        SetField(
            handler,
            "_forgePrimaryMaterial",
            new ForgeSlotSelection(1, PrimaryBefore, 1));
        SetField<int?>(handler, "_forgeAccountId", accountId);
        SetField<int?>(handler, "_forgeCharacterId", characterId);
        SetField(
            handler,
            "_forgeSelectionStartedTimestamp",
            Stopwatch.GetTimestamp());
    }

    private static GamePacket CreateForgeStartPacket(
        Guid? operationId)
    {
        var packet = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            Opcodes.ForgeStart);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, sizeof(uint)),
            ForgeItemSelectionPacket.OrdinaryForgeMode);
        return new GamePacket(packet, operationId);
    }

    private static EquipmentForgeExecutionReceipt CreateReceipt(
        EquipmentForgeCommandResultStatus status,
        int characterId = 19)
    {
        var committed = status is
            EquipmentForgeCommandResultStatus.Succeeded or
            EquipmentForgeCommandResultStatus.FailedRoll;
        return new EquipmentForgeExecutionReceipt(
            characterId,
            status,
            materialType: committed ? 2 : 0,
            roll: committed ? 31 : -1,
            successProbability: committed ? 75 : 0,
            silverSpent: committed ? 200 : 0,
            equipmentBeforeCompactItemState:
                committed ? EquipmentBefore.ToCompactString() : string.Empty,
            equipmentAfterCompactItemState:
                committed
                    ? (status ==
                            EquipmentForgeCommandResultStatus.Succeeded
                        ? EquipmentAfter
                        : EquipmentBefore).ToCompactString()
                    : string.Empty,
            materials: committed
                ?
                [
                    new EquipmentForgeReceiptMaterial(
                        EquipmentForgeCommandItemRole.PrimaryMaterial,
                        KitBagSlot: 1,
                        PrimaryBefore.Id,
                        Quantity: 1,
                        StackBefore: 2,
                        StackAfter: 1)
                ]
                : [],
            walletRevision: committed ? 8 : 0,
            inventoryRevision: committed ? 12 : 0,
            auditReference: "audit:equipment-forge:handler-check",
            outboxEventId: committed
                ? Guid.Parse("eb3ed1d6-39f8-4146-9f05-ea585b63363d")
                : null);
    }

    private static async Task InvokeForgeStartAsync(
        GameClientHandler handler,
        Guid? operationId = null)
    {
        var invocation = HandlePacketMethod.Invoke(
            handler,
            [CreateForgeStartPacket(operationId), CancellationToken.None])
            as Task
            ?? throw new InvalidOperationException(
                "Forge packet handler did not return a task.");
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

    private sealed record ForgeHandlerFixture(
        ClientSession Session,
        ForgeCaptureTransport Transport,
        GameClientHandler Handler,
        ForgeExecutor? Executor,
        ForgeSnapshotReader SnapshotReader,
        ForgeStore Store,
        GameCharacter LiveCharacter,
        string BeforeBag,
        string AfterBag) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() =>
            Session.DisposeAsync();
    }

    private sealed class ForgeExecutor(
        EquipmentForgeExecutionResult replayResult,
        EquipmentForgeExecutionResult executeResult) :
        IEquipmentForgeCommandExecutor
    {
        public int ReplayCount { get; private set; }
        public int ExecuteCount { get; private set; }
        public EquipmentForgeCommand? ExecutedCommand { get; private set; }

        public Task<EquipmentForgeExecutionResult> TryReplayAsync(
            CommandSubject subject,
            Guid clientOperationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplayCount++;
            Check.Equal(7, subject.AccountId, "Forge replay account");
            Check.Equal(19, subject.CharacterId, "Forge replay character");
            Check.Equal(
                OperationId,
                clientOperationId,
                "Forge replay operation UUID");
            return Task.FromResult(replayResult);
        }

        public Task<EquipmentForgeExecutionResult> ExecuteAsync(
            CommandEnvelope<EquipmentForgeCommand> envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            ExecutedCommand = envelope.Command;
            return Task.FromResult(executeResult);
        }
    }

    private sealed class ForgeSnapshotReader(
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
                    "Injected Forge projection read failure.");
            }

            Check.Equal(
                snapshot.AccountId,
                accountId,
                "Forge projection account");
            return Task.FromResult(snapshot);
        }
    }

    private sealed class ForgeStore : GameStoreTestStub
    {
        public int ForgeCount { get; private set; }

        public ForgeTransactionResult? Result { get; set; }

        public override Task<ForgeTransactionResult>
            ForgeEquipmentAsync(
                int accountId,
                int characterId,
                ForgeTransactionRequest request,
                CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ForgeCount++;
            return Task.FromResult(
                Result ?? throw new InvalidOperationException(
                    "A legacy Forge result was not configured."));
        }
    }

    private sealed class ForgeCaptureTransport :
        ILegacyByteTransport,
        ISecureControlChannel,
        ISecureCommandResultTransport
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _legacyWrites = [];
        private readonly List<SecureLegacyCommandResult> _results = [];
        private readonly List<string> _events = [];

        public ForgeCaptureTransport()
        {
            var connectionId = Enumerable.Repeat(
                (byte)0x31,
                SecureProtocolConstants.ConnectionIdBytes).ToArray();
            var clientInstanceId = Enumerable.Repeat(
                (byte)0x42,
                SecureProtocolConstants.ClientInstanceIdBytes).ToArray();
            var originHash = Enumerable.Repeat(
                (byte)0x53,
                SecureProtocolConstants.BuildHashBytes).ToArray();
            try
            {
                ConnectionContext = new SecureConnectionContext(
                    SecureEndpointRole.Game,
                    SecureProtocolConstants.ProtocolMajor,
                    SecureProtocolConstants.ProtocolMinor,
                    connectionId,
                    clientInstanceId,
                    originHash);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(connectionId);
                CryptographicOperations.ZeroMemory(clientInstanceId);
                CryptographicOperations.ZeroMemory(originHash);
            }
        }

        public string RemoteEndPoint => "secure-forge-handler-check";
        public SecureConnectionContext ConnectionContext { get; }
        public SecureBoundGamePrincipal? BoundGamePrincipal => null;
        public bool SupportsRealtimeMovement => false;
        public bool IsRealtimeMovementActive => false;

        public IReadOnlyList<SecureLegacyCommandResult> CommandResults
        {
            get
            {
                lock (_gate)
                {
                    return _results.ToArray();
                }
            }
        }

        public IReadOnlyList<string> Events
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToArray();
                }
            }
        }

        public ValueTask<int> ReadAsync(
            Memory<byte> destination,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(0);
        }

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _legacyWrites.Add(source.ToArray());
                _events.Add("legacy");
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask SendLegacyCommandResultAsync(
            SecureLegacyCommandResult result,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _results.Add(result);
                _events.Add("command-result");
            }
            return ValueTask.CompletedTask;
        }

        public IReadOnlyList<byte[]> ReadClearLegacyPackets()
        {
            byte[] encrypted;
            lock (_gate)
            {
                encrypted = _legacyWrites
                    .SelectMany(static value => value)
                    .ToArray();
            }
            new PacketCipher().Transform(encrypted);

            var packets = new List<byte[]>();
            var offset = 0;
            while (offset < encrypted.Length)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(
                    encrypted.AsSpan(offset, 2));
                if (length < 4 || length > encrypted.Length - offset)
                {
                    throw new InvalidDataException(
                        "Captured Forge stream contains an invalid frame.");
                }
                packets.Add(
                    encrypted.AsSpan(offset, length).ToArray());
                offset += length;
            }
            return packets;
        }

        public bool TryTakeRealtimeMovement(
            out SecureRealtimeMovementIngress ingress)
        {
            ingress = default;
            return false;
        }

        public bool TryPublishRealtimeSnapshot(
            in SecureRealtimePositionSnapshot snapshot) => false;

        public ValueTask SendGameGrantAsync(
            SecureGameGrant grant,
            CancellationToken cancellationToken) =>
            ValueTask.FromException(
                new InvalidOperationException(
                    "Forge checks cannot issue login grants."));

        public void MarkAuthenticated()
        {
        }

        public void Disconnect()
        {
        }

        public ValueTask DisposeAsync() =>
            ValueTask.CompletedTask;
    }
}
