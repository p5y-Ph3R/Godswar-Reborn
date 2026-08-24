using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WarehouseHandlerChecks
{
    public const string CheckName =
        "Warehouse open lease and durable transfer handler";

    public static async Task RunAsync()
    {
        await CheckCommittedDepositAsync();
        await CheckManagerClickPreservesOpenAccessAsync();
        await CheckDuplicateDepositAsync();
        await CheckExpiredAccessAsync();
        await CheckManagerFreshAndDuplicateAsync();
    }

    private static async Task CheckCommittedDepositAsync()
    {
        var beforeBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            KitBagSlot,
            StorageKey.ToCompactString());
        var beforeCharacter = CharacterSnapshot(
            beforeBag,
            BeforeInventoryRevision,
            "warehouse-handler-before");
        var afterCharacter = CharacterSnapshot(
            GameDefaults.EmptyKitBag,
            AfterInventoryRevision,
            "warehouse-handler-after");
        var beforeWarehouse = WarehouseSnapshot(
            BeforeInventoryRevision,
            containsKey: false);
        var afterWarehouse = WarehouseSnapshot(
            AfterInventoryRevision,
            containsKey: true);
        var executor = new WarehouseTransferExecutor
        {
            ExecuteResult = WarehouseTransferExecutionResult.Terminal(
                WarehouseTransferExecutionDisposition.Committed,
                DepositReceipt())
        };
        await using var fixture = await CreateFixtureAsync(
            beforeCharacter,
            [beforeCharacter, afterCharacter],
            [beforeWarehouse, beforeWarehouse, afterWarehouse],
            executor);
        fixture.Character.MaxHp = 77_700;
        fixture.Character.CurrentHp = 70_007;

        await InvokeAsync(fixture.Handler, CreateWarehouseClick());

        var advertised = fixture.ReadPackets();
        Check.True(
            advertised.Count == 1 &&
            advertised[0].SequenceEqual(
                Godswar.Server.Packets.PacketBuilder
                    .WarehouseDialogOpenAck(
                        WarehouseNpcProtocol.AthensWarehouseNpcId,
                        "Athens_025")) &&
            fixture.Warehouses.ReadCount == 0 &&
            GetHandlerField<WarehouseAccessContext>(
                fixture.Handler,
                "_warehouseAccessContext") is null,
            "ordinary click advertises the captured warehouse mode before reading state");

        await InvokeAsync(fixture.Handler, CreateWarehousePageRequest());
        var opened = fixture.ReadPackets();
        AssertOrdinaryOpen(opened, fixture.Handler);
        var openPacketCount = opened.Count;

        await InvokeAsync(fixture.Handler, CreateDepositRequest());

        var packets = fixture.ReadPackets().Skip(openPacketCount).ToArray();
        var clearIndex = FindOpcode(packets, 0x2744);
        var bagIndex = FindOpcode(packets, 0x2731);
        var snapshotIndex = FindOpcode(packets, Opcodes.WarehouseSnapshot);
        Check.True(
            packets.Length > 1 &&
            !packets.Any(packet =>
                ReadOpcode(packet) == Opcodes.WarehouseTransfer) &&
            packets.Count(packet => ReadOpcode(packet) == 0x2744) == 1 &&
            IsKitBagClear(packets[clearIndex], KitBagSlot) &&
            packets.Count(packet =>
                ReadOpcode(packet) == Opcodes.WarehouseSnapshot) == 4,
            "fresh commit converges through one authoritative page snapshot");
        Check.True(
            clearIndex >= 0 &&
            bagIndex > clearIndex &&
            snapshotIndex > bagIndex,
            "fresh transfer clears the old bag icon before projecting both inventories");
        Check.True(
            executor.ReplayCount == 1 &&
            executor.ExecuteCount == 1 &&
            executor.Envelope?.Command is { } command &&
            command.ExpectedInventoryRevision == BeforeInventoryRevision &&
            command.ExpectedWarehouseRevision == 0 &&
            command.ExpectedSourceCompactItemState ==
                StorageKey.ToCompactString() &&
            command.ExpectedDestinationCompactItemState == "[]",
            "fresh transfer is replay-first then executes from coherent state");
        Check.True(
            fixture.Warehouses.ReadCount == 3 &&
            fixture.Characters.ReadCount == 2 &&
            fixture.Character.KitBag == GameDefaults.EmptyKitBag &&
            fixture.Character.MaxHp == 77_700 &&
            fixture.Character.CurrentHp == 70_007,
            "transfer reloads only authoritative inventory and warehouse state");

        var secure = fixture.Transport.CommandResults.Single();
        Check.True(
            secure.CommandFamily == (ushort)CommandFamily.WarehouseTransfer &&
            secure.ResultCode ==
                (int)WarehouseTransferResultStatus.Deposited &&
            secure.Disposition == SecureLegacyCommandDisposition.Applied &&
            secure.AuthoritativeRevision == AfterInventoryRevision &&
            secure.OperationId == OperationId &&
            fixture.Transport.Events[^1] == "secure",
            "fresh transfer settles securely after authoritative projections");
    }

    private static async Task CheckDuplicateDepositAsync()
    {
        var afterCharacter = CharacterSnapshot(
            GameDefaults.EmptyKitBag,
            AfterInventoryRevision,
            "warehouse-handler-duplicate");
        var afterWarehouse = WarehouseSnapshot(
            AfterInventoryRevision,
            containsKey: true);
        var executor = new WarehouseTransferExecutor
        {
            ReplayResult = WarehouseTransferExecutionResult.Terminal(
                WarehouseTransferExecutionDisposition.Duplicate,
                DepositReceipt())
        };
        await using var fixture = await CreateFixtureAsync(
            afterCharacter,
            [afterCharacter],
            [afterWarehouse, afterWarehouse],
            executor);

        await OpenWarehouseAsync(fixture);
        var openPacketCount = fixture.ReadPackets().Count;
        await InvokeAsync(fixture.Handler, CreateDepositRequest());

        var packets = fixture.ReadPackets().Skip(openPacketCount).ToArray();
        var clearIndex = FindOpcode(packets, 0x2744);
        Check.True(
            executor.ReplayCount == 1 &&
            executor.ExecuteCount == 0 &&
            !packets.Any(packet =>
                ReadOpcode(packet) == Opcodes.WarehouseTransfer),
            "duplicate transfer never replays the non-idempotent native ACK");
        Check.True(
            clearIndex >= 0 &&
            packets.Count(packet => ReadOpcode(packet) == 0x2744) == 1 &&
            IsKitBagClear(packets[clearIndex], KitBagSlot) &&
            FindOpcode(packets, 0x2731) > clearIndex &&
            packets.Count(packet =>
                ReadOpcode(packet) == Opcodes.WarehouseSnapshot) == 4 &&
            fixture.Warehouses.ReadCount == 2 &&
            fixture.Characters.ReadCount == 1,
            "duplicate converges through bag and occupied warehouse projections");
        var secure = fixture.Transport.CommandResults.Single();
        Check.True(
            secure.CommandFamily == (ushort)CommandFamily.WarehouseTransfer &&
            secure.ResultCode ==
                (int)WarehouseTransferResultStatus.Deposited &&
            secure.Disposition == SecureLegacyCommandDisposition.Replayed &&
            secure.AuthoritativeRevision == AfterInventoryRevision,
            "duplicate transfer settles as a durable replay");
    }

    private static async Task CheckManagerClickPreservesOpenAccessAsync()
    {
        var beforeBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            KitBagSlot,
            StorageKey.ToCompactString());
        var beforeCharacter = CharacterSnapshot(
            beforeBag,
            BeforeInventoryRevision,
            "warehouse-handler-manager-before");
        var afterCharacter = CharacterSnapshot(
            GameDefaults.EmptyKitBag,
            AfterInventoryRevision,
            "warehouse-handler-manager-after");
        var beforeWarehouse = WarehouseSnapshot(
            BeforeInventoryRevision,
            containsKey: false,
            capacity: WarehouseCapacityPolicy.MaximumSupportedCapacity);
        var afterWarehouse = WarehouseSnapshot(
            AfterInventoryRevision,
            containsKey: true,
            capacity: WarehouseCapacityPolicy.MaximumSupportedCapacity,
            itemSlot: BagFourWarehouseSlot);
        var executor = new WarehouseTransferExecutor
        {
            ExpectedWarehouseSlot = BagFourWarehouseSlot,
            ExecuteResult = WarehouseTransferExecutionResult.Terminal(
                WarehouseTransferExecutionDisposition.Committed,
                DepositReceipt(
                    BagFourWarehouseSlot,
                    WarehouseCapacityPolicy.MaximumSupportedCapacity))
        };
        await using var fixture = await CreateFixtureAsync(
            beforeCharacter,
            [beforeCharacter, afterCharacter],
            [
                beforeWarehouse,
                beforeWarehouse,
                beforeWarehouse,
                afterWarehouse
            ],
            executor);

        await OpenWarehouseAsync(fixture);
        var access = GetHandlerField<WarehouseAccessContext>(
            fixture.Handler,
            "_warehouseAccessContext");

        await InvokeAsync(fixture.Handler, CreateManagerClick());

        Check.True(
            access is not null &&
            ReferenceEquals(
                access,
                GetHandlerField<WarehouseAccessContext>(
                    fixture.Handler,
                    "_warehouseAccessContext")),
            "related Warehouse Manager click preserves an existing access lease");

        var beforePageRequest = fixture.ReadPackets().Count;
        await InvokeAsync(
            fixture.Handler,
            CreateWarehousePageRequest(page: 3));
        var pagePackets = fixture.ReadPackets()
            .Skip(beforePageRequest)
            .Where(packet =>
                ReadOpcode(packet) == Opcodes.WarehouseSnapshot)
            .ToArray();
        Check.True(
            pagePackets.Length == 4 &&
            pagePackets.All(packet =>
                BinaryPrimitives.ReadUInt32LittleEndian(
                    packet.AsSpan(8)) ==
                    Godswar.Server.Packets.PacketBuilder
                        .WarehousePageProjectionUserMarker + 0x93) &&
            GetHandlerField<int>(
                fixture.Handler,
                "_warehouseSelectedPage") == 3,
            "the database capacity admits page four without changing its logical selection");

        var beforeTransfer = fixture.ReadPackets().Count;
        await InvokeAsync(
            fixture.Handler,
            CreateDepositRequest(BagFourWarehouseSlot));

        var transferPackets = fixture.ReadPackets()
            .Skip(beforeTransfer)
            .Where(packet =>
                ReadOpcode(packet) == Opcodes.WarehouseSnapshot)
            .ToArray();
        Check.True(
            executor.ExecuteCount == 1 &&
            executor.Envelope?.Command.WarehouseSlot ==
                BagFourWarehouseSlot &&
            fixture.Transport.CommandResults.Single().Disposition ==
                SecureLegacyCommandDisposition.Applied &&
            transferPackets.Length == 4 &&
            transferPackets.All(packet =>
                BinaryPrimitives.ReadUInt32LittleEndian(
                    packet.AsSpan(8)) ==
                    Godswar.Server.Packets.PacketBuilder
                        .WarehousePageProjectionUserMarker + 0x93),
            "bag-four transfer commits and refreshes page four after using the manager");
    }

    private static async Task CheckExpiredAccessAsync()
    {
        var beforeBag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            KitBagSlot,
            StorageKey.ToCompactString());
        var beforeCharacter = CharacterSnapshot(
            beforeBag,
            BeforeInventoryRevision,
            "warehouse-handler-expired");
        var beforeWarehouse = WarehouseSnapshot(
            BeforeInventoryRevision,
            containsKey: false);
        var executor = new WarehouseTransferExecutor();
        await using var fixture = await CreateFixtureAsync(
            beforeCharacter,
            [],
            [beforeWarehouse],
            executor);

        await OpenWarehouseAsync(fixture);
        var beforeTransfer = fixture.ReadPackets().Count;
        var access = GetHandlerField<WarehouseAccessContext>(
            fixture.Handler,
            "_warehouseAccessContext") ??
            throw new InvalidOperationException(
                "Warehouse access was not issued.");
        SetHandlerField(
            fixture.Handler,
            "_warehouseAccessContext",
            access with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) });

        await InvokeAsync(fixture.Handler, CreateDepositRequest());

        Check.True(
            fixture.ReadPackets().Count == beforeTransfer &&
            executor.ReplayCount == 0 &&
            executor.ExecuteCount == 0 &&
            fixture.Warehouses.ReadCount == 1 &&
            fixture.Characters.ReadCount == 0 &&
            GetHandlerField<WarehouseAccessContext>(
                fixture.Handler,
                "_warehouseAccessContext") is null,
            "expired warehouse access fails closed before durable providers");
        var secure = fixture.Transport.CommandResults.Single();
        Check.True(
            secure.CommandFamily == (ushort)CommandFamily.WarehouseTransfer &&
            secure.ResultCode ==
                (int)WarehouseTransferResultStatus.ConcurrentConflict &&
            secure.Disposition == SecureLegacyCommandDisposition.Rejected &&
            secure.AuthoritativeRevision == 0,
            "expired access produces a finite secure rejection");
    }

    private static void AssertOrdinaryOpen(
        IReadOnlyList<byte[]> packets,
        GameClientHandler handler)
    {
        Check.True(
            packets.Count == 5 &&
            packets[0].Length == 48 &&
            ReadOpcode(packets[0]) == Opcodes.NpcDialogOpen &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                packets[0].AsSpan(4)) ==
                    WarehouseNpcProtocol.AthensWarehouseNpcId &&
            BinaryPrimitives.ReadInt32LittleEndian(
                packets[0].AsSpan(8)) == 0x20,
            "exact ordinary click emits the captured special 10067 acknowledgement");
        Check.True(
            packets.Skip(1).All(packet =>
                ReadOpcode(packet) == Opcodes.WarehouseSnapshot) &&
            packets.Skip(1).Select(packet => packet[14])
                .SequenceEqual(new byte[] { 0, 2, 4, 6 }),
            "ordinary open emits every 10034 capacity chunk in order");
        var access = GetHandlerField<WarehouseAccessContext>(
            handler,
            "_warehouseAccessContext");
        Check.True(
            access is not null &&
            access.NpcInteractionId ==
                WarehouseNpcProtocol.AthensWarehouseNpcId &&
            access.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(14),
            "ordinary open issues a 15-minute server-bound access lease");
    }

    private static int FindOpcode(
        IReadOnlyList<byte[]> packets,
        ushort opcode)
    {
        for (var index = 0; index < packets.Count; index++)
        {
            if (ReadOpcode(packets[index]) == opcode)
            {
                return index;
            }
        }
        return -1;
    }

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));

    private static bool IsKitBagClear(byte[] packet, int slot)
    {
        var page = Math.DivRem(slot, 24, out var cell);
        return packet.Length == 16 &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(8)) == page &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(10)) == cell &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(12)) ==
                ushort.MaxValue &&
            BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(14)) ==
                ushort.MaxValue;
    }
}
