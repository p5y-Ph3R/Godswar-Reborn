using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class EquipmentBagTransferDurableHandlerChecks
{
    public static async Task RunAsync()
    {
        await CheckCommittedUnequipAsync();
        await CheckCommittedEquipAsync();
        await CheckReplayPrecedesDirectionInferenceAsync();
        await CheckDuplicateEquipReconcilesWithoutAckAsync();
        await CheckDurableTerminalRejectionsAsync();
        await CheckRequestHashConflictAsync();
        await CheckPendingRideBlocksMountTransferDurablyAsync();
        await CheckActiveRideBlocksMountTransferDurablyAsync();
        await CheckRideReplayPrecedesRuntimeObservationAsync();
        await CheckUnsupportedLengthCannotDowngradeAsync();
        await CheckTokenlessTransferUsesCompatibilityPathAsync();
        await CheckOpcode10051PathIsUnaffectedAsync();
        await CheckProviderOutageLeavesOperationPendingAsync();
        await CheckProjectionFailureLeavesOperationPendingAsync();
        await CheckCommittedProjectionMismatchLeavesOperationPendingAsync();
        await CheckMismatchedReceiptLeavesOperationPendingAsync();
        await CheckCancellationLeavesOperationPendingAsync();
        CheckProjectionPreservesUnrelatedLiveState();
    }

    private static async Task CheckCommittedUnequipAsync()
    {
        var receipt = CreateUnequipReceipt();
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound(),
            EquipmentBagTransferExecutionResult.Committed(receipt));

        await InvokeTransferAsync(fixture.Handler, OperationId);

        Check.Equal(
            1,
            fixture.Executor!.ReplayCount,
            "new equipment transfer checks permanent inbox first");
        Check.Equal(
            1,
            fixture.Executor.ExecuteCount,
            "new equipment transfer executes exactly once");
        var command = fixture.Executor.ExecutedCommand ??
            throw new InvalidOperationException(
                "Equipment transfer executor did not capture command.");
        Check.Equal(
            EquipmentSlot,
            command.EquipmentSlot,
            "equipment transfer preserves equipment slot");
        Check.Equal(
            KitBagSlot,
            command.KitBagSlot,
            "equipment transfer preserves bag slot");
        Check.Equal(
            EquipmentItem.ToCompactString(),
            command.ExpectedEquipmentCompactItemState,
            "equipment transfer captures full equipment state");
        Check.Equal(
            "[]",
            command.ExpectedKitBagCompactItemState,
            "equipment transfer captures empty bag state");
        AssertAuthoritativeProjection(fixture, "committed unequip");
        AssertDurableResult(
            fixture,
            SecureLegacyCommandDisposition.Applied,
            expectedTransferAcknowledgements: 1,
            "committed unequip");
        var pending = GetFieldValue(
            fixture.Handler,
            "_pendingUnequipFollowup");
        Check.True(
            pending is not null,
            "first committed unequip arms one client follow-up");
        Check.Equal(
            KitBagSlot,
            (int)pending!.GetType()
                .GetProperty("DestinationSlot")!.GetValue(pending)!,
            "pending unequip follow-up binds destination");
        Check.Equal(
            EquipmentItem.Id,
            (uint)pending.GetType()
                .GetProperty("ItemId")!.GetValue(pending)!,
            "pending unequip follow-up binds moved item");
    }

    private static async Task
        CheckReplayPrecedesDirectionInferenceAsync()
    {
        var receipt = CreateUnequipReceipt();
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.Duplicate(receipt),
            liveState: UnequipAfterState);

        await InvokeTransferAsync(fixture.Handler, OperationId);

        Check.Equal(
            1,
            fixture.Executor!.ReplayCount,
            "completed transfer retry checks permanent inbox");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "transfer replay does not infer the reversed direction");
        AssertDurableResult(
            fixture,
            SecureLegacyCommandDisposition.Replayed,
            expectedTransferAcknowledgements: 0,
            "replayed unequip");
        Check.True(
            GetFieldValue(
                fixture.Handler,
                "_pendingUnequipFollowup") is null,
            "replayed unequip does not arm another follow-up");
    }

    private static async Task
        CheckUnsupportedLengthCannotDowngradeAsync()
    {
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound());

        await InvokeTransferAsync(
            fixture.Handler,
            OperationId,
            packetLength: 42);

        Check.Equal(
            0,
            fixture.Executor!.ReplayCount,
            "unsupported UUID transfer never reaches executor");
        Check.Equal(
            0,
            fixture.Store.UnequipCount,
            "unsupported UUID transfer cannot use compatibility store");
        AssertDurableResult(
            fixture,
            SecureLegacyCommandDisposition.Rejected,
            expectedTransferAcknowledgements: 0,
            "unsupported-length UUID transfer");
    }

    private static async Task
        CheckTokenlessTransferUsesCompatibilityPathAsync()
    {
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound());
        fixture.Store.UnequipResult = fixture.PersistedCharacter;

        await InvokeTransferAsync(
            fixture.Handler,
            operationId: null);

        Check.Equal(
            1,
            fixture.Store.UnequipCount,
            "tokenless transfer uses compatibility store");
        Check.Equal(
            0,
            fixture.Executor!.ReplayCount,
            "tokenless transfer skips durable replay");
        Check.Equal(
            0,
            fixture.Transport.CommandResults.Count,
            "tokenless transfer emits no secure command result");
        Check.Equal(
            1,
            CountTransferAcknowledgements(
                fixture.Transport.ReadLegacyPackets()),
            "tokenless transfer keeps stock acknowledgement");
    }

    private static async Task
        CheckProviderOutageLeavesOperationPendingAsync()
    {
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound(),
            providerUnavailable: true);

        await InvokeTransferAsync(fixture.Handler, OperationId);

        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "missing transfer provider emits no terminal response");
    }

    private static async Task
        CheckProjectionFailureLeavesOperationPendingAsync()
    {
        await using var fixture = CreateFixture(
            EquipmentBagTransferExecutionResult.ReplayNotFound(),
            EquipmentBagTransferExecutionResult.Committed(
                CreateUnequipReceipt()),
            projectionFails: true);

        await InvokeTransferAsync(fixture.Handler, OperationId);

        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "projection uncertainty emits no terminal response");
    }

    private static void CheckProjectionPreservesUnrelatedLiveState()
    {
        var live = CreateProjectedCharacter(transferred: false);
        live.PositionX = 17.25f;
        live.PositionZ = -91.5f;
        live.CurrentHp = 777;
        live.CurrentMp = 333;
        live.VitalsRevision = 88;
        live.Silver = 123_456;
        live.Gold = 654_321;
        var persisted = CreateProjectedCharacter(transferred: true);
        persisted.CalculatedStats = CopyStats(
            persisted.CalculatedStats!,
            maxHp: 500,
            maxMp: 200,
            physicalAttack: 99_999);

        GameClientHandler.ApplyDurableEquipmentBagTransferProjection(
            live,
            persisted);

        Check.Equal(17.25f, live.PositionX, "projection preserves X");
        Check.Equal(-91.5f, live.PositionZ, "projection preserves Z");
        Check.Equal(123_456, live.Silver, "projection preserves Silver");
        Check.Equal(654_321, live.Gold, "projection preserves Gold");
        Check.Equal(500, live.CurrentHp, "HP clamps to recalculated max");
        Check.Equal(200, live.CurrentMp, "MP clamps to recalculated max");
        Check.Equal(
            89L,
            live.VitalsRevision,
            "projection advances live vitals exactly once");
        Check.Equal(
            99_999,
            live.CalculatedStats!.PhysicalAttack,
            "projection applies recalculated equipment stats");

        var zeroManaLive = CreateProjectedCharacter(transferred: false);
        zeroManaLive.CurrentMp = 100;
        var zeroManaPersisted =
            CreateProjectedCharacter(transferred: true);
        zeroManaPersisted.CalculatedStats = CopyStats(
            zeroManaPersisted.CalculatedStats!,
            maxHp: 500,
            maxMp: 0,
            physicalAttack: 99_999);

        GameClientHandler.ApplyDurableEquipmentBagTransferProjection(
            zeroManaLive,
            zeroManaPersisted);

        Check.Equal(0, zeroManaLive.MaxMp, "zero-MP projection preserves max");
        Check.Equal(
            0,
            zeroManaLive.CurrentMp,
            "zero-MP projection cannot retain one impossible mana point");
    }

    private static void AssertAuthoritativeProjection(
        TransferFixture fixture,
        string description)
    {
        Check.True(
            EquipmentSlots.GetItem(
                fixture.LiveCharacter.Equipment,
                fixture.LiveCharacter.Profession,
                EquipmentSlot).IsEmpty,
            $"{description} clears equipment slot");
        Check.Equal(
            EquipmentItem,
            KitBagSlots.GetItem(
                fixture.LiveCharacter.KitBag,
                KitBagSlot),
            $"{description} reloads bag item");
        Check.Equal(
            777,
            fixture.LiveCharacter.CurrentHp,
            $"{description} preserves live HP");
        Check.Equal(
            333,
            fixture.LiveCharacter.CurrentMp,
            $"{description} preserves live MP");
        Check.Equal(
            124L,
            fixture.LiveCharacter.VitalsRevision,
            $"{description} advances live vitals exactly once");
        Check.Equal(
            PersistedPhysicalAttack,
            fixture.LiveCharacter.CalculatedStats!.PhysicalAttack,
            $"{description} refreshes equipment-derived stats");
        Check.Equal(
            82.5f,
            fixture.LiveCharacter.PositionX,
            $"{description} preserves runtime position");
        Check.Equal(
            456_789,
            fixture.LiveCharacter.Silver,
            $"{description} preserves wallet projection");
    }

    private static void AssertDurableResult(
        TransferFixture fixture,
        SecureLegacyCommandDisposition expectedDisposition,
        int expectedTransferAcknowledgements,
        string description)
    {
        Check.Equal(
            1,
            fixture.Transport.CommandResults.Count,
            $"{description} emits one secure result");
        var result = fixture.Transport.CommandResults[0];
        Check.Equal(
            (int)expectedDisposition,
            (int)result.Disposition,
            $"{description} disposition");
        Check.Equal(
            15,
            result.CommandFamily,
            $"{description} command family");
        Check.Equal(
            OperationId,
            result.OperationId,
            $"{description} operation UUID");
        Check.True(
            fixture.Transport.Events[^1] == "command-result",
            $"{description} secure result is last");
        Check.Equal(
            expectedTransferAcknowledgements,
            CountTransferAcknowledgements(
                fixture.Transport.ReadLegacyPackets()),
            $"{description} non-idempotent transfer ACK count");
    }

    private static int CountTransferAcknowledgements(
        IReadOnlyList<byte[]> packets) =>
        packets.Count(static packet =>
            packet.Length == 42 &&
            BitConverter.ToUInt16(packet, 2) == Opcodes.StorageItem);
}
