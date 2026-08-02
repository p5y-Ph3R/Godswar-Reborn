using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class EquipmentForgeDurableHandlerChecks
{
    public static async Task RunAsync()
    {
        await CheckLegacyRawProjectionPreservesLiveVitalsAsync();
        await CheckCommitOrderingAndProjectionIsolationAsync();
        await CheckFailedRollUsesCommittedAttemptResultAsync();
        await CheckSelectionlessDuplicateReplaysAsync();
        await CheckSelectionlessReplayMissLeavesPendingAsync();
        await CheckDurableTerminalRejectionAsync();
        await CheckProviderOutageLeavesPendingAsync();
        await CheckProjectionFailureLeavesPendingAsync();
        await CheckMismatchedReceiptLeavesPendingAsync();
        await CheckRequestConflictIsTerminalAsync();
        await CheckSecureTokenlessRequestFailsClosedAsync();
    }

    private static async Task
        CheckCommitOrderingAndProjectionIsolationAsync()
    {
        var receipt = CreateReceipt(
            EquipmentForgeCommandResultStatus.Succeeded);
        await using var fixture = CreateFixture(
            EquipmentForgeExecutionResult.ReplayNotFound(),
            EquipmentForgeExecutionResult.Committed(receipt));

        await InvokeForgeStartAsync(fixture.Handler, OperationId);

        Check.Equal(1, fixture.Executor!.ReplayCount, "Forge checks inbox before execution");
        Check.Equal(1, fixture.Executor.ExecuteCount, "new Forge executes once");
        var command = fixture.Executor.ExecutedCommand ??
            throw new InvalidOperationException(
                "Forge executor did not capture its command.");
        Check.Equal(
            0,
            command.Equipment.KitBagSlot,
            "Forge command preserves equipment slot");
        Check.Equal(
            EquipmentBefore.ToCompactString(),
            command.Equipment.ExpectedCompactItemState,
            "Forge command preserves full equipment snapshot");
        Check.Equal(
            1,
            command.PrimaryMaterial.KitBagSlot,
            "Forge command preserves primary-material slot");
        Check.Equal(
            PrimaryBefore.ToCompactString(),
            command.PrimaryMaterial.ExpectedCompactItemState,
            "Forge command preserves full primary-material snapshot");
        Check.Equal(0, command.OddsMaterials.Length, "Forge command permits no odds crystals");

        Check.Equal(1, fixture.SnapshotReader.ReadCount, "committed Forge reloads once");
        Check.Equal(fixture.AfterBag, fixture.LiveCharacter.KitBag, "Forge reloads bag");
        Check.Equal(800, fixture.LiveCharacter.Silver, "Forge reloads Silver");
        Check.Equal(888, fixture.LiveCharacter.Gold, "Forge leaves Gold untouched");
        Check.Equal(321.25f, fixture.LiveCharacter.PositionX, "Forge preserves live X");
        Check.Equal(-222.5f, fixture.LiveCharacter.PositionZ, "Forge preserves live Z");
        Check.Equal(777, fixture.LiveCharacter.CurrentHp, "Forge preserves live HP");
        Check.Equal(333, fixture.LiveCharacter.CurrentMp, "Forge preserves live MP");

        AssertDurableResponse(
            fixture,
            receipt,
            SecureLegacyCommandDisposition.Applied,
            expectedSuccess: true,
            expectedResultKind: 1,
            "committed Forge");
    }

    private static async Task
        CheckFailedRollUsesCommittedAttemptResultAsync()
    {
        var receipt = CreateReceipt(
            EquipmentForgeCommandResultStatus.FailedRoll);
        await using var fixture = CreateFixture(
            EquipmentForgeExecutionResult.ReplayNotFound(),
            EquipmentForgeExecutionResult.Committed(receipt),
            persistedEquipment: EquipmentBefore);

        await InvokeForgeStartAsync(fixture.Handler, OperationId);

        AssertDurableResponse(
            fixture,
            receipt,
            SecureLegacyCommandDisposition.Applied,
            expectedSuccess: false,
            expectedResultKind: 1,
            "failed Forge roll");
        Check.Equal(
            EquipmentBefore,
            KitBagSlots.GetItem(fixture.LiveCharacter.KitBag, 0),
            "failed roll preserves equipment");
        Check.Equal(
            PrimaryAfter,
            KitBagSlots.GetItem(fixture.LiveCharacter.KitBag, 1),
            "failed roll still consumes primary material");
    }

    private static async Task CheckSelectionlessDuplicateReplaysAsync()
    {
        var receipt = CreateReceipt(
            EquipmentForgeCommandResultStatus.Succeeded);
        await using var fixture = CreateFixture(
            EquipmentForgeExecutionResult.Duplicate(receipt),
            stageSelections: false);

        await InvokeForgeStartAsync(fixture.Handler, OperationId);

        Check.Equal(1, fixture.Executor!.ReplayCount, "selectionless retry checks inbox");
        Check.Equal(0, fixture.Executor.ExecuteCount, "selectionless retry cannot mutate");
        AssertDurableResponse(
            fixture,
            receipt,
            SecureLegacyCommandDisposition.Replayed,
            expectedSuccess: true,
            expectedResultKind: 1,
            "duplicate Forge");
    }

    private static async Task
        CheckSelectionlessReplayMissLeavesPendingAsync()
    {
        await using var fixture = CreateFixture(
            EquipmentForgeExecutionResult.ReplayNotFound(),
            stageSelections: false);

        await InvokeForgeStartAsync(fixture.Handler, OperationId);

        Check.Equal(1, fixture.Executor!.ReplayCount, "selectionless miss checks inbox");
        Check.Equal(0, fixture.Executor.ExecuteCount, "selectionless miss cannot execute");
        Check.Equal(0, fixture.SnapshotReader.ReadCount, "selectionless miss does not project");
        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "selectionless replay miss emits no stock result or 0x0102");
    }

    private static async Task CheckDurableTerminalRejectionAsync()
    {
        var receipt = CreateReceipt(
            EquipmentForgeCommandResultStatus.InvalidSelection);
        await using var fixture = CreateFixture(
            EquipmentForgeExecutionResult.ReplayNotFound(),
            EquipmentForgeExecutionResult.TerminalRejected(receipt),
            persistedEquipment: EquipmentBefore,
            persistedPrimary: PrimaryBefore,
            persistedSilver: 1_000);

        await InvokeForgeStartAsync(fixture.Handler, OperationId);

        Check.Equal(1, fixture.SnapshotReader.ReadCount, "durable rejection reloads projection");
        AssertDurableResponse(
            fixture,
            receipt,
            SecureLegacyCommandDisposition.Rejected,
            expectedSuccess: false,
            expectedResultKind: 0,
            "durable Forge rejection",
            expectedSilver: 1_000);
    }

    private static async Task CheckProviderOutageLeavesPendingAsync()
    {
        await using var fixture = CreateFixture(
            EquipmentForgeExecutionResult.ReplayNotFound(),
            providerUnavailable: true);

        await InvokeForgeStartAsync(fixture.Handler, OperationId);

        Check.Equal(0, fixture.Transport.Events.Count, "Forge provider outage emits no result");
        Check.Equal(0, fixture.SnapshotReader.ReadCount, "provider outage does not project");
    }

    private static async Task CheckProjectionFailureLeavesPendingAsync()
    {
        var receipt = CreateReceipt(
            EquipmentForgeCommandResultStatus.Succeeded);
        await using var fixture = CreateFixture(
            EquipmentForgeExecutionResult.ReplayNotFound(),
            EquipmentForgeExecutionResult.Committed(receipt),
            projectionFails: true);

        await InvokeForgeStartAsync(fixture.Handler, OperationId);

        Check.Equal(1, fixture.Executor!.ExecuteCount, "projection failure follows durable commit");
        Check.Equal(1, fixture.SnapshotReader.ReadCount, "projection failure is observed");
        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "uncertain Forge projection emits no false terminal result");
    }

    private static async Task CheckMismatchedReceiptLeavesPendingAsync()
    {
        var receipt = CreateReceipt(
            EquipmentForgeCommandResultStatus.Succeeded,
            characterId: 20);
        await using var fixture = CreateFixture(
            EquipmentForgeExecutionResult.ReplayNotFound(),
            EquipmentForgeExecutionResult.Committed(receipt));

        await InvokeForgeStartAsync(fixture.Handler, OperationId);

        Check.Equal(1, fixture.Executor!.ExecuteCount, "mismatched receipt follows execution");
        Check.Equal(0, fixture.SnapshotReader.ReadCount, "invalid receipt is not projected");
        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "unknown Forge receipt identity emits no terminal result");
    }

    private static async Task CheckRequestConflictIsTerminalAsync()
    {
        await using var fixture = CreateFixture(
            EquipmentForgeExecutionResult.RequestHashConflict(),
            stageSelections: false);

        await InvokeForgeStartAsync(fixture.Handler, OperationId);

        var packets = fixture.Transport.ReadClearLegacyPackets();
        Check.Equal(1, packets.Count, "Forge conflict sends one stock rejection");
        AssertForgeResult(
            packets[0],
            expectedSuccess: false,
            expectedResultKind: 0,
            "Forge request conflict");
        var result = fixture.Transport.CommandResults.Single();
        Check.True(
            result.Disposition == SecureLegacyCommandDisposition.Conflict,
            "Forge request conflict preserves secure disposition");
        Check.Equal(
            (ushort)CommandFamily.EquipmentForge,
            result.CommandFamily,
            "Forge conflict family");
        Check.Equal(
            OperationId,
            result.OperationId,
            "Forge conflict operation UUID");
        Check.Equal(
            "command-result",
            fixture.Transport.Events[^1],
            "Forge conflict sends 0x0102 last");
    }

    private static async Task
        CheckSecureTokenlessRequestFailsClosedAsync()
    {
        await using var fixture = CreateFixture(
            EquipmentForgeExecutionResult.ReplayNotFound());

        await InvokeForgeStartAsync(fixture.Handler);

        Check.Equal(
            0,
            fixture.Store.ForgeCount,
            "secure tokenless Forge cannot use compatibility store");
        Check.Equal(
            0,
            fixture.Executor!.ReplayCount,
            "secure tokenless Forge does not query the inbox");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "secure tokenless Forge does not execute a durable command");
        Check.Equal(
            0,
            fixture.Transport.CommandResults.Count,
            "secure tokenless Forge has no UUID to settle");
        var packets = fixture.Transport.ReadClearLegacyPackets();
        AssertForgeResult(
            packets[0],
            expectedSuccess: false,
            expectedResultKind: 0,
            "secure tokenless Forge rejection");
    }

    private static void AssertDurableResponse(
        ForgeHandlerFixture fixture,
        EquipmentForgeExecutionReceipt receipt,
        SecureLegacyCommandDisposition expectedDisposition,
        bool expectedSuccess,
        int expectedResultKind,
        string description,
        int expectedSilver = 800)
    {
        var packets = fixture.Transport.ReadClearLegacyPackets();
        Check.True(packets.Count >= 3, $"{description} sends result/status/bag");
        AssertForgeResult(
            packets[0],
            expectedSuccess,
            expectedResultKind,
            description);
        Check.Equal(
            (ushort)0x27B6,
            ReadOpcode(packets[1]),
            $"{description} sends player status second");
        Check.Equal(
            expectedSilver,
            BinaryPrimitives.ReadInt32LittleEndian(
                packets[1].AsSpan(120, sizeof(int))),
            $"{description} status contains projected Silver");
        Check.True(
            packets.Skip(2).Any(
                packet => ReadOpcode(packet) == 0x2731),
            $"{description} sends bag detail after status");

        var result = fixture.Transport.CommandResults.Single();
        Check.True(
            result.Disposition == expectedDisposition,
            $"{description} secure disposition");
        Check.Equal(
            (ushort)CommandFamily.EquipmentForge,
            result.CommandFamily,
            $"{description} secure family");
        Check.Equal(
            checked((uint)receipt.Status),
            result.ResultCode,
            $"{description} result code");
        Check.Equal(
            checked((ulong)receipt.InventoryRevision),
            result.InventoryRevision,
            $"{description} inventory revision");
        Check.Equal(
            OperationId,
            result.OperationId,
            $"{description} operation UUID");
        Check.Equal(
            "command-result",
            fixture.Transport.Events[^1],
            $"{description} sends family-3 0x0102 last");
        Check.Equal(
            packets.Count,
            fixture.Transport.Events.Count(
                static value => value == "legacy"),
            $"{description} sends every stock packet before 0x0102");
    }

    private static void AssertForgeResult(
        byte[] packet,
        bool expectedSuccess,
        int expectedResultKind,
        string description)
    {
        Check.Equal(40, packet.Length, $"{description} stock result length");
        Check.Equal(
            Opcodes.ForgeStart,
            ReadOpcode(packet),
            $"{description} stock result opcode");
        Check.Equal(
            expectedSuccess ? (byte)1 : (byte)0,
            packet[4],
            $"{description} stock success flag");
        Check.Equal(
            expectedResultKind,
            BinaryPrimitives.ReadInt32LittleEndian(
                packet.AsSpan(8, sizeof(int))),
            $"{description} stock result kind");
    }

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)));
}
