using System.Buffers.Binary;
using System.Reflection;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class EquipmentBagTransferDurableHandlerChecks
{
    private static readonly MethodInfo TryBeginPendingSkillCastMethod =
        typeof(GameClientHandler).GetMethod(
            "TryBeginPendingSkillCastAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.TryBeginPendingSkillCastAsync was not found.");
    private static readonly MethodInfo StopPendingSkillCastsMethod =
        typeof(GameClientHandler).GetMethod(
            "StopPendingSkillCastsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(
            "GameClientHandler.StopPendingSkillCastsAsync was not found.");

    private static async Task CheckCommittedEquipAsync()
    {
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound(),
            EquipmentBagTransferExecutionResult.Committed(
                CreateEquipReceipt()),
            liveState: UnequipAfterState,
            persistedState: UnequipBeforeState);

        await InvokeTransferAsync(fixture.Handler, OperationId);

        Check.Equal(
            1,
            fixture.Executor!.ExecuteCount,
            "new equip executes once after replay miss");
        Check.Equal(
            "[]",
            fixture.Executor.ExecutedCommand!
                .Value.ExpectedEquipmentCompactItemState,
            "equip captures empty equipment state");
        Check.Equal(
            EquipmentItem.ToCompactString(),
            fixture.Executor.ExecutedCommand!
                .Value.ExpectedKitBagCompactItemState,
            "equip captures full bag item state");
        Check.Equal(
            EquipmentItem,
            EquipmentSlots.GetItem(
                fixture.LiveCharacter.Equipment,
                fixture.LiveCharacter.Profession,
                EquipmentSlot),
            "committed equip reloads equipment");
        Check.True(
            KitBagSlots.GetItem(
                fixture.LiveCharacter.KitBag,
                KitBagSlot).IsEmpty,
            "committed equip clears bag");
        Check.True(
            GetFieldValue(
                fixture.Handler,
                "_pendingUnequipFollowup") is null,
            "committed equip never arms unequip follow-up");
        AssertDurableResult(
            fixture,
            SecureLegacyCommandDisposition.Applied,
            expectedTransferAcknowledgements: 1,
            "committed equip");
    }

    private static async Task
        CheckDuplicateEquipReconcilesWithoutAckAsync()
    {
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.Duplicate(
                CreateEquipReceipt()),
            liveState: UnequipBeforeState,
            persistedState: UnequipBeforeState);

        await InvokeTransferAsync(fixture.Handler, OperationId);

        Check.Equal(
            0,
            fixture.Executor!.ExecuteCount,
            "replayed equip never executes inverse unequip");
        AssertDurableResult(
            fixture,
            SecureLegacyCommandDisposition.Replayed,
            expectedTransferAcknowledgements: 0,
            "replayed equip");
        var packets = fixture.Transport.ReadLegacyPackets();
        Check.True(
            packets.Any(static packet =>
                packet.Length == 16 &&
                BitConverter.ToUInt16(packet, 2) ==
                    Opcodes.StorageItem),
            "replayed equip explicitly evicts stale bag object");
    }

    private static async Task CheckDurableTerminalRejectionsAsync()
    {
        await CheckTerminalAsync(
            EquipmentBagTransferResultStatus.BothEmpty,
            BothEmptyState,
            BothEmptyState,
            BothEmptyState,
            "both-empty transfer");
        await CheckTerminalAsync(
            EquipmentBagTransferResultStatus.BothOccupied,
            BothOccupiedState,
            BothOccupiedState,
            BothOccupiedState,
            "both-occupied transfer");
        var staleEquipment = new TransferSlotState(
            OtherItem,
            CompactItemEntry.Empty);
        await CheckTerminalAsync(
            EquipmentBagTransferResultStatus.StaleEquipment,
            UnequipBeforeState,
            staleEquipment,
            staleEquipment,
            "stale-equipment transfer");
        var staleKitBag = new TransferSlotState(
            EquipmentItem,
            OtherItem);
        await CheckTerminalAsync(
            EquipmentBagTransferResultStatus.StaleKitBag,
            UnequipBeforeState,
            staleKitBag,
            staleKitBag,
            "stale-bag transfer");
    }

    private static async Task CheckTerminalAsync(
        EquipmentBagTransferResultStatus status,
        TransferSlotState expected,
        TransferSlotState authoritative,
        TransferSlotState persisted,
        string description)
    {
        var receipt = CreateReceipt(
            status,
            expected,
            authoritative,
            outboxEventId: null);
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound(),
            EquipmentBagTransferExecutionResult.TerminalRejected(
                receipt),
            liveState: expected,
            persistedState: persisted);

        await InvokeTransferAsync(fixture.Handler, OperationId);

        AssertDurableResult(
            fixture,
            SecureLegacyCommandDisposition.Rejected,
            expectedTransferAcknowledgements: 0,
            description);
        Check.Equal(
            (uint)status,
            fixture.Transport.CommandResults[0].ResultCode,
            $"{description} result code");
    }

    private static async Task CheckRequestHashConflictAsync()
    {
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult
                .RequestHashConflict());

        await InvokeTransferAsync(fixture.Handler, OperationId);

        Check.Equal(
            0,
            fixture.Executor!.ExecuteCount,
            "request-hash conflict cannot mutate");
        AssertDurableResult(
            fixture,
            SecureLegacyCommandDisposition.Conflict,
            expectedTransferAcknowledgements: 0,
            "request-hash conflict");
    }

    private static async Task
        CheckPendingRideBlocksMountTransferDurablyAsync()
    {
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound(),
            EquipmentBagTransferExecutionResult.TerminalRejected(
                CreateRideRuntimeBlockedReceipt()),
            liveState: MountBeforeState,
            persistedState: MountBeforeState,
            equipmentSlot: EquipmentSlots.Mount);
        Check.True(
            await BeginPendingRideAsync(fixture.Handler),
            "Ride cast begins before mount transfer");
        try
        {
            await InvokeTransferAsync(
                fixture.Handler,
                OperationId,
                equipmentSlot: EquipmentSlots.Mount);
        }
        finally
        {
            await StopPendingRideAsync(fixture.Handler);
        }

        AssertRideRuntimeBlocked(fixture, "pending Ride cast");
    }

    private static async Task
        CheckActiveRideBlocksMountTransferDurablyAsync()
    {
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound(),
            EquipmentBagTransferExecutionResult.TerminalRejected(
                CreateRideRuntimeBlockedReceipt()),
            liveState: MountBeforeState,
            persistedState: MountBeforeState,
            equipmentSlot: EquipmentSlots.Mount);
        await ActivateRideRuntimeStatusAsync(fixture);

        await InvokeTransferAsync(
            fixture.Handler,
            OperationId,
            equipmentSlot: EquipmentSlots.Mount);

        AssertRideRuntimeBlocked(fixture, "active Ride status");
    }

    private static async Task
        CheckRideReplayPrecedesRuntimeObservationAsync()
    {
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.Duplicate(
                CreateMountUnequipReceipt()),
            liveState: MountAfterState,
            persistedState: MountAfterState,
            equipmentSlot: EquipmentSlots.Mount);
        await ActivateRideRuntimeStatusAsync(fixture);

        await InvokeTransferAsync(
            fixture.Handler,
            OperationId,
            equipmentSlot: EquipmentSlots.Mount);

        Check.Equal(
            0,
            fixture.Executor!.ExecuteCount,
            "Ride runtime state cannot replace a completed replay");
        Check.True(
            fixture.Executor.ExecutedCommand is null,
            "replayed mount transfer never captures new runtime intent");
        AssertDurableResult(
            fixture,
            SecureLegacyCommandDisposition.Replayed,
            expectedTransferAcknowledgements: 0,
            "mount transfer replay during Ride");
    }

    private static async Task ActivateRideRuntimeStatusAsync(
        TransferFixture fixture)
    {
        Check.True(
            await fixture.Registry
                .SetPersistentRuntimeStatusAndPublishAsync(
                    fixture.Session,
                    MountCatalog.RuntimeStatusKind,
                    statusId: 7_001,
                    priority: 1,
                    beneficial: false,
                    movementSpeedBonus: 0.2f,
                    active: true,
                    DateTimeOffset.UtcNow,
                    "equipment-transfer-Ride-check",
                    CancellationToken.None),
            "Ride runtime status is active before mount transfer");
    }

    private static Task<bool> BeginPendingRideAsync(
        GameClientHandler handler) =>
        TryBeginPendingSkillCastMethod.Invoke(
            handler,
            [
                checked((uint)MountCatalog.RideSkillId),
                TimeSpan.FromMinutes(5),
                "equipment-transfer-Ride-check",
                new Func<CancellationToken, Task>(
                    _ => Task.CompletedTask),
                new Func<CancellationToken, Task>(
                    _ => Task.CompletedTask),
                CancellationToken.None,
                null
            ]) as Task<bool>
        ?? throw new InvalidOperationException(
            "TryBeginPendingSkillCastAsync returned no task.");

    private static Task StopPendingRideAsync(
        GameClientHandler handler) =>
        StopPendingSkillCastsMethod.Invoke(
            handler,
            null) as Task
        ?? throw new InvalidOperationException(
            "StopPendingSkillCastsAsync returned no task.");

    private static void AssertRideRuntimeBlocked(
        TransferFixture fixture,
        string description)
    {
        Check.Equal(
            1,
            fixture.Executor!.ExecuteCount,
            $"{description} persists one terminal decision");
        Check.True(
            fixture.Executor.ExecutedCommand?.MountRuntimeBlocked == true,
            $"{description} is bound into the durable command");
        AssertDurableResult(
            fixture,
            SecureLegacyCommandDisposition.Rejected,
            expectedTransferAcknowledgements: 0,
            description);
        Check.Equal(
            (uint)EquipmentBagTransferResultStatus.RideRuntimeBlocked,
            fixture.Transport.CommandResults[0].ResultCode,
            $"{description} returns the finite Ride rejection");
    }

    private static async Task
        CheckMismatchedReceiptLeavesOperationPendingAsync()
    {
        var mismatched = CreateReceipt(
            EquipmentBagTransferResultStatus.Unequipped,
            UnequipBeforeState,
            UnequipBeforeState,
            OutboxEventId,
            characterId: 20);
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound(),
            EquipmentBagTransferExecutionResult.Committed(mismatched));

        await InvokeTransferAsync(fixture.Handler, OperationId);

        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "mismatched durable receipt emits no terminal response");
    }

    private static async Task
        CheckCommittedProjectionMismatchLeavesOperationPendingAsync()
    {
        await using (var unequip = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound(),
            EquipmentBagTransferExecutionResult.Committed(
                CreateUnequipReceipt()),
            persistedState: UnequipBeforeState))
        {
            await InvokeTransferAsync(unequip.Handler, OperationId);

            Check.Equal(
                0,
                unequip.Transport.Events.Count,
                "committed unequip with stale projection emits no ACK " +
                "or secure terminal");
        }

        await using (var equip = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound(),
            EquipmentBagTransferExecutionResult.Committed(
                CreateEquipReceipt()),
            liveState: UnequipAfterState,
            persistedState: UnequipAfterState))
        {
            await InvokeTransferAsync(equip.Handler, OperationId);

            Check.Equal(
                0,
                equip.Transport.Events.Count,
                "committed equip with stale projection emits no ACK or " +
                "secure terminal");
        }
    }

    private static async Task
        CheckCancellationLeavesOperationPendingAsync()
    {
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound());
        using var source = new CancellationTokenSource();
        source.Cancel();
        var cancelled = false;
        try
        {
            await InvokeTransferAsync(
                fixture.Handler,
                OperationId,
                cancellationToken: source.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        Check.True(cancelled, "transfer cancellation propagates");
        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "transfer cancellation emits no terminal response");
    }

    private static async Task CheckOpcode10051PathIsUnaffectedAsync()
    {
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound(),
            liveState: UnequipAfterState,
            persistedState: UnequipBeforeState);
        fixture.Store.EquipResult = fixture.PersistedCharacter;

        await InvokePacketAsync(
            fixture.Handler,
            CreateBreakItemPacket());

        Check.Equal(
            1,
            fixture.Store.EquipCount,
            "opcode 10051 still uses inferred compatibility equip");
        Check.Equal(
            0,
            fixture.Executor!.ReplayCount,
            "opcode 10051 never enters family-15 replay");
        Check.Equal(
            0,
            fixture.Transport.CommandResults.Count,
            "opcode 10051 emits no family-15 secure result");
    }

    private static GamePacket CreateBreakItemPacket()
    {
        var packet = new byte[92];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            Opcodes.BreakItem);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, sizeof(uint)),
            0x5876_DBF0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(12, sizeof(ushort)),
            checked((ushort)(KitBagSlot / 24)));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(14, sizeof(ushort)),
            checked((ushort)(KitBagSlot % 24)));
        return new GamePacket(packet);
    }

    private static async Task InvokePacketAsync(
        GameClientHandler handler,
        GamePacket packet)
    {
        var invocation = HandlePacketMethod.Invoke(
            handler,
            [packet, CancellationToken.None]) as Task
            ?? throw new InvalidOperationException(
                "Equipment transfer packet handler returned no task.");
        await invocation;
    }
}
