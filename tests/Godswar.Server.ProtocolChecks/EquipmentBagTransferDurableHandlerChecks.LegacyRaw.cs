using System.Buffers.Binary;
using Godswar.Server.Application.Accounts;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Pets;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class EquipmentBagTransferDurableHandlerChecks
{
    private static async Task
        CheckLocalRawTokenlessUnequipRetainsCompatibilityAsync()
    {
        await using var fixture = CreateLegacyRawFixture(
            hasLocalLegacyAuthenticationAccess: true);

        await InvokeTransferAsync(
            fixture.Handler,
            operationId: null);

        Check.Equal(
            1,
            fixture.Store.UnequipCount,
            "validated local raw transfer invokes compatibility store once");
        Check.True(
            !fixture.Transport.Disconnected,
            "validated local raw transfer keeps the session connected");
        Check.Equal(
            1,
            CountTransferAcknowledgements(
                fixture.Transport.ReadLegacyPackets()),
            "validated local raw transfer emits one native acknowledgement");

        var projected = GetFieldValue(
                fixture.Handler,
                "_character") as GameCharacter
            ?? throw new InvalidOperationException(
                "Local raw transfer lost its character projection.");
        Check.Equal(
            0u,
            EquipmentSlots.GetItemId(
                projected.Equipment,
                projected.Profession,
                EquipmentSlot),
            "validated local raw transfer clears the weapon slot");
        Check.Equal(
            EquipmentItem,
            KitBagSlots.GetItem(projected.KitBag, KitBagSlot),
            "validated local raw transfer preserves the complete weapon item");
    }

    private static async Task
        CheckRawTokenlessUnequipWithoutLocalAccessFailsClosedAsync()
    {
        await using var fixture = CreateLegacyRawFixture(
            hasLocalLegacyAuthenticationAccess: false);

        await InvokeTransferAsync(
            fixture.Handler,
            operationId: null);

        Check.Equal(
            0,
            fixture.Store.UnequipCount,
            "raw transfer without local access never reaches compatibility store");
        Check.True(
            fixture.Transport.Disconnected,
            "raw transfer without local access disconnects");
        Check.Equal(
            0,
            CountTransferAcknowledgements(
                fixture.Transport.ReadLegacyPackets()),
            "raw transfer without local access emits no mutation acknowledgement");

        var projected = GetFieldValue(
                fixture.Handler,
                "_character") as GameCharacter
            ?? throw new InvalidOperationException(
                "Rejected raw transfer lost its character projection.");
        Check.Equal(
            EquipmentItem,
            EquipmentSlots.GetItem(
                projected.Equipment,
                projected.Profession,
                EquipmentSlot),
            "rejected raw transfer leaves the weapon equipped");
        Check.True(
            KitBagSlots.GetItem(projected.KitBag, KitBagSlot).IsEmpty,
            "rejected raw transfer leaves the destination empty");
    }

    private static async Task
        CheckLocalRawTokenlessRightClickEquipRetainsCompatibilityAsync()
    {
        var petExecutor = new PetActivationExecutor();
        await using var fixture = CreateLegacyRawFixture(
            hasLocalLegacyAuthenticationAccess: true,
            equipFromBag: true,
            petDurableCommands: petExecutor);

        await InvokePacketAsync(
            fixture.Handler,
            CreateBreakItemPacket());

        Check.Equal(
            1,
            fixture.Store.EquipCount,
            "validated local raw right-click invokes compatibility equip once");
        Check.Equal(
            0,
            petExecutor.ExecuteCount,
            "tokenless local raw gear is not misclassified as a pet command");
        Check.True(
            !fixture.Transport.Disconnected,
            "validated local raw right-click keeps the session connected");

        var projected = GetFieldValue(
                fixture.Handler,
                "_character") as GameCharacter
            ?? throw new InvalidOperationException(
                "Local raw right-click lost its character projection.");
        Check.Equal(
            EquipmentItem,
            EquipmentSlots.GetItem(
                projected.Equipment,
                projected.Profession,
                EquipmentSlot),
            "validated local raw right-click equips the complete weapon item");
        Check.True(
            KitBagSlots.GetItem(projected.KitBag, KitBagSlot).IsEmpty,
            "validated local raw right-click clears the source bag slot");
    }

    private static async Task
        CheckRawTokenlessRightClickEquipWithoutLocalAccessFailsClosedAsync()
    {
        var petExecutor = new PetActivationExecutor();
        await using var fixture = CreateLegacyRawFixture(
            hasLocalLegacyAuthenticationAccess: false,
            equipFromBag: true,
            petDurableCommands: petExecutor);

        await InvokePacketAsync(
            fixture.Handler,
            CreateBreakItemPacket());

        Check.Equal(
            0,
            fixture.Store.EquipCount,
            "raw right-click without local access never reaches compatibility equip");
        Check.Equal(
            0,
            petExecutor.ExecuteCount,
            "tokenless raw gear never enters the identified pet command path");
        Check.True(
            fixture.Transport.Disconnected,
            "raw right-click without local access disconnects");

        var projected = GetFieldValue(
                fixture.Handler,
                "_character") as GameCharacter
            ?? throw new InvalidOperationException(
                "Rejected raw right-click lost its character projection.");
        Check.Equal(
            0u,
            EquipmentSlots.GetItemId(
                projected.Equipment,
                projected.Profession,
                EquipmentSlot),
            "rejected raw right-click leaves the equipment slot empty");
        Check.Equal(
            EquipmentItem,
            KitBagSlots.GetItem(projected.KitBag, KitBagSlot),
            "rejected raw right-click leaves the weapon in the bag");
    }

    private static LegacyRawTransferFixture CreateLegacyRawFixture(
        bool hasLocalLegacyAuthenticationAccess,
        bool equipFromBag = false,
        IPetDurableCommandExecutor? petDurableCommands = null)
    {
        var baseSnapshot =
            CharacterSnapshotContractChecks.CreateValidSnapshot();
        var liveSnapshot = WithTransferState(
            baseSnapshot,
            equipFromBag ? UnequipAfterState : UnequipBeforeState,
            physicalAttack: 400);
        var movedSnapshot = WithTransferState(
            baseSnapshot,
            equipFromBag ? UnequipBeforeState : UnequipAfterState,
            PersistedPhysicalAttack);
        var live = CharacterLoadSnapshotHydrator
            .Hydrate(liveSnapshot)?.Character
            ?? throw new InvalidOperationException(
                "Local raw transfer live fixture did not hydrate.");
        var moved = CharacterLoadSnapshotHydrator
            .Hydrate(movedSnapshot)?.Character
            ?? throw new InvalidOperationException(
                "Local raw transfer result fixture did not hydrate.");

        var transport = new LegacyRawTransferCaptureTransport();
        var session = new ClientSession(transport);
        var registry = GameHandlerOwnershipTestFences.CreateRegistry(
            session,
            baseSnapshot.AccountId,
            live);
        registry.JoinMap(
            session,
            baseSnapshot.AccountId,
            live,
            objectId: 0x0000_1448);
        var store = new TransferStore
        {
            UnequipResult = moved,
            EquipResult = moved
        };
        var snapshotReader = new TransferSnapshotReader(
            movedSnapshot,
            fails: false);
        var localAccess = hasLocalLegacyAuthenticationAccess
            ? LegacyAuthenticationAccess.Create(
                new ValidatedServerRuntimeProfile(
                    ServerRuntimeProfileKind.LocalDevelopment,
                    GameStorageProviderKind.Postgres,
                    ServerListenerTransport.RawTcp,
                    AllowsLegacyAuthentication: true))
            : null;
        var handler = new GameClientHandler(
            session,
            store,
            registry,
            snapshotReader,
            WorldContentReaderTestFixtures.Empty,
            legacyAuthenticationAccess: localAccess,
            itemContent: TestItemContent.Content,
            petContent: PetContentTestCatalog.Instance,
            petDurableCommands: petDurableCommands);
        SetField(
            handler,
            "_account",
            new AccountIdentity(
                baseSnapshot.AccountId,
                "raw-transfer-check"));
        SetField(handler, "_character", live);

        // Production PostgreSQL composition sets this constructor policy.
        // The focused fixture enables it after construction so it need not
        // provide unrelated durable command-family test doubles.
        SetField(handler, "_requiresDurablePlayerCommands", true);
        return new LegacyRawTransferFixture(
            session,
            transport,
            handler,
            store,
            registry);
    }

    private sealed record LegacyRawTransferFixture(
        ClientSession Session,
        LegacyRawTransferCaptureTransport Transport,
        GameClientHandler Handler,
        TransferStore Store,
        GameSessionRegistry Registry) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Registry.Remove(Session);
            await Session.DisposeAsync();
        }
    }

    private sealed class LegacyRawTransferCaptureTransport :
        ILegacyByteTransport
    {
        private readonly object _gate = new();
        private readonly List<byte[]> _writes = [];

        public string RemoteEndPoint =>
            "legacy-raw-equipment-transfer-handler-check";
        public bool Disconnected { get; private set; }

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
                _writes.Add(source.ToArray());
            }
            return ValueTask.CompletedTask;
        }

        public void Disconnect()
        {
            Disconnected = true;
        }

        public IReadOnlyList<byte[]> ReadLegacyPackets()
        {
            byte[] encrypted;
            lock (_gate)
            {
                encrypted = _writes
                    .SelectMany(static value => value)
                    .ToArray();
            }
            new PacketCipher().Transform(encrypted);

            var packets = new List<byte[]>();
            var offset = 0;
            while (offset < encrypted.Length)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(
                    encrypted.AsSpan(offset, sizeof(ushort)));
                if (length < 4 || length > encrypted.Length - offset)
                {
                    throw new InvalidDataException(
                        "Captured raw transfer stream has an invalid frame.");
                }
                packets.Add(
                    encrypted.AsSpan(offset, length).ToArray());
                offset += length;
            }
            return packets;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
