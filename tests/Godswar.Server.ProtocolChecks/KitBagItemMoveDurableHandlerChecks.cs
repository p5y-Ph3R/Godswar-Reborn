using System.Buffers.Binary;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class KitBagItemMoveDurableHandlerChecks
{
    public static async Task RunAsync()
    {
        await CheckCompactMoveCommitAndProjectionAsync();
        await CheckDetailedSwapCommitAsync();
        await CheckReplayPrecedesReversedStateCaptureAsync();
        await CheckTerminalRejectionAsync();
        await CheckStaleDestinationRejectionAsync();
        await CheckRequestConflictIsTerminalAsync();
        await CheckSecureTokenlessMoveFailsClosedAsync();
        await CheckUuidUnsupportedLengthCannotDowngradeAsync();
        await CheckUuidSameSlotCannotDowngradeAsync();
        await CheckProviderOutageLeavesPendingAsync();
        await CheckExecutorFailureLeavesPendingAsync();
        await CheckProjectionFailureLeavesPendingAsync();
        await CheckMismatchedReceiptLeavesPendingAsync();
        await CheckCancellationLeavesPendingAsync();
    }

    private static async Task
        CheckCompactMoveCommitAndProjectionAsync()
    {
        var receipt = CreateReceipt(
            KitBagItemMoveResultStatus.Moved);
        await using var fixture = CreateFixture(
            KitBagItemMoveExecutionResult.ReplayNotFound(),
            KitBagItemMoveExecutionResult.Committed(receipt));

        await InvokeMoveAsync(fixture.Handler, OperationId);

        Check.Equal(
            1,
            fixture.Executor!.ReplayCount,
            "new kit-bag move checks the permanent inbox first");
        Check.Equal(
            1,
            fixture.Executor.ExecuteCount,
            "new kit-bag move executes exactly once");
        var command = fixture.Executor.ExecutedCommand ??
            throw new InvalidOperationException(
                "Kit-bag move executor did not capture its command.");
        Check.Equal(
            SourceSlot,
            command.SourceKitBagSlot,
            "kit-bag move preserves source slot");
        Check.Equal(
            DestinationSlot,
            command.DestinationKitBagSlot,
            "kit-bag move preserves destination slot");
        Check.Equal(
            SourceItem.ToCompactString(),
            command.ExpectedSourceCompactItemState,
            "kit-bag move captures full source state");
        Check.Equal(
            CompactItemEntry.Empty.ToCompactString(),
            command.ExpectedDestinationCompactItemState,
            "kit-bag move captures empty destination state");
        AssertProjectionIsolation(fixture, "committed compact move");
        AssertDurableResponse(
            fixture,
            receipt,
            SecureLegacyCommandDisposition.Applied,
            expectedMoveAcknowledgement: true,
            "committed compact move");
    }

    private static async Task CheckDetailedSwapCommitAsync()
    {
        var receipt = CreateReceipt(
            KitBagItemMoveResultStatus.Swapped);
        await using var fixture = CreateFixture(
            KitBagItemMoveExecutionResult.ReplayNotFound(),
            KitBagItemMoveExecutionResult.Committed(receipt),
            liveDestination: DestinationItem,
            persistedSource: DestinationItem,
            persistedDestination: SourceItem);

        await InvokeMoveAsync(
            fixture.Handler,
            OperationId,
            packetLength: 80);

        Check.Equal(
            DestinationItem,
            KitBagSlots.GetItem(
                fixture.LiveCharacter.KitBag,
                SourceSlot),
            "detailed swap reloads authoritative source");
        AssertDurableResponse(
            fixture,
            receipt,
            SecureLegacyCommandDisposition.Applied,
            expectedMoveAcknowledgement: true,
            "committed detailed swap");
    }

    private static async Task
        CheckReplayPrecedesReversedStateCaptureAsync()
    {
        var receipt = CreateReceipt(
            KitBagItemMoveResultStatus.Moved);
        await using var fixture = CreateFixture(
            KitBagItemMoveExecutionResult.Duplicate(receipt),
            liveSource: CompactItemEntry.Empty,
            liveDestination: SourceItem);

        await InvokeMoveAsync(fixture.Handler, OperationId);

        Check.Equal(
            1,
            fixture.Executor!.ReplayCount,
            "completed move retry checks its permanent inbox");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "move replay never recaptures reversed live slots");
        AssertDurableResponse(
            fixture,
            receipt,
            SecureLegacyCommandDisposition.Replayed,
            expectedMoveAcknowledgement: false,
            "replayed kit-bag move");
    }

    private static async Task CheckTerminalRejectionAsync()
    {
        var receipt = CreateReceipt(
            KitBagItemMoveResultStatus.EmptySource);
        await using var fixture = CreateFixture(
            KitBagItemMoveExecutionResult.ReplayNotFound(),
            KitBagItemMoveExecutionResult.TerminalRejected(receipt),
            liveSource: CompactItemEntry.Empty,
            persistedSource: CompactItemEntry.Empty,
            persistedDestination: CompactItemEntry.Empty);

        await InvokeMoveAsync(fixture.Handler, OperationId);

        AssertProjectionIsolation(fixture, "empty-source rejection");
        AssertDurableResponse(
            fixture,
            receipt,
            SecureLegacyCommandDisposition.Rejected,
            expectedMoveAcknowledgement: false,
            "empty-source rejection");
    }

    private static async Task CheckRequestConflictIsTerminalAsync()
    {
        await using var fixture = CreateFixture(
            KitBagItemMoveExecutionResult.RequestHashConflict());

        await InvokeMoveAsync(fixture.Handler, OperationId);

        Check.Equal(
            1,
            fixture.Executor!.ReplayCount,
            "move request conflict is found through replay");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "move request conflict cannot mutate");
        AssertRejectedResponse(
            fixture,
            SecureLegacyCommandDisposition.Conflict,
            "kit-bag move request conflict");
    }

    private static async Task
        CheckSecureTokenlessMoveFailsClosedAsync()
    {
        await using var fixture = CreateFixture(
            KitBagItemMoveExecutionResult.ReplayNotFound());
        fixture.Store.Result =
            CreateCompatibilityResult(fixture.PersistedBag);

        await InvokeMoveAsync(fixture.Handler, operationId: null);

        Check.Equal(
            0,
            fixture.Store.MoveCount,
            "secure tokenless move cannot use compatibility store");
        Check.Equal(
            0,
            fixture.Executor!.ReplayCount,
            "tokenless kit-bag move skips durable replay");
        Check.Equal(
            0,
            fixture.Transport.CommandResults.Count,
            "tokenless kit-bag move sends no secure result");
        var packets = fixture.Transport.ReadClearLegacyPackets();
        Check.True(
            packets.Any(packet => ReadOpcode(packet) == 0x2731),
            "secure tokenless move refreshes the bag");
        Check.True(
            packets.All(packet => !IsMoveAcknowledgement(packet)),
            "secure tokenless move sends no move acknowledgement");
    }

    private static async Task
        CheckUuidUnsupportedLengthCannotDowngradeAsync()
    {
        await using var fixture = CreateFixture(
            KitBagItemMoveExecutionResult.ReplayNotFound());
        fixture.Store.Result =
            CreateCompatibilityResult(fixture.PersistedBag);

        await InvokeMoveAsync(
            fixture.Handler,
            OperationId,
            packetLength: 28);

        AssertMalformedUuidRejection(
            fixture,
            "unsupported-length UUID move");
    }

    private static async Task
        CheckUuidSameSlotCannotDowngradeAsync()
    {
        await using var fixture = CreateFixture(
            KitBagItemMoveExecutionResult.ReplayNotFound());
        fixture.Store.Result =
            CreateCompatibilityResult(fixture.PersistedBag);

        await InvokeMoveAsync(
            fixture.Handler,
            OperationId,
            sourceSlot: SourceSlot,
            destinationSlot: SourceSlot);

        AssertMalformedUuidRejection(
            fixture,
            "same-slot UUID move");
    }

    private static async Task CheckProviderOutageLeavesPendingAsync()
    {
        await using var fixture = CreateFixture(
            KitBagItemMoveExecutionResult.ReplayNotFound(),
            providerUnavailable: true);

        await InvokeMoveAsync(fixture.Handler, OperationId);

        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "move provider outage emits no terminal result");
        Check.Equal(
            0,
            fixture.SnapshotReader.ReadCount,
            "move provider outage does not project");
    }

    private static async Task CheckProjectionFailureLeavesPendingAsync()
    {
        var receipt = CreateReceipt(
            KitBagItemMoveResultStatus.Moved);
        await using var fixture = CreateFixture(
            KitBagItemMoveExecutionResult.ReplayNotFound(),
            KitBagItemMoveExecutionResult.Committed(receipt),
            projectionFails: true);

        await InvokeMoveAsync(fixture.Handler, OperationId);

        Check.Equal(
            1,
            fixture.Executor!.ExecuteCount,
            "projection failure follows a durable move");
        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "uncertain move projection emits no terminal result");
    }

    private static async Task CheckMismatchedReceiptLeavesPendingAsync()
    {
        var receipt = CreateReceipt(
            KitBagItemMoveResultStatus.Moved,
            destinationSlot: DestinationSlot + 1);
        await using var fixture = CreateFixture(
            KitBagItemMoveExecutionResult.ReplayNotFound(),
            KitBagItemMoveExecutionResult.Committed(receipt));

        await InvokeMoveAsync(fixture.Handler, OperationId);

        Check.Equal(
            0,
            fixture.SnapshotReader.ReadCount,
            "mismatched move receipt is not projected");
        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "mismatched move receipt emits no terminal result");
    }

    private static async Task CheckCancellationLeavesPendingAsync()
    {
        await using var fixture = CreateFixture(
            KitBagItemMoveExecutionResult.ReplayNotFound());
        using var source = new CancellationTokenSource();
        source.Cancel();

        try
        {
            await InvokeMoveAsync(
                fixture.Handler,
                OperationId,
                cancellationToken: source.Token);
            throw new InvalidOperationException(
                "Cancelled kit-bag move unexpectedly completed.");
        }
        catch (OperationCanceledException)
        {
        }

        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "cancelled kit-bag move emits no terminal result");
    }

    private static GameCharacter CreateCompatibilityResult(
        string kitBag) =>
        CharacterLoadSnapshotHydrator.Hydrate(
            WithBag(
                CharacterSnapshotContractChecks.CreateValidSnapshot(),
                kitBag))?.Character
        ?? throw new InvalidOperationException(
            "Compatibility move result did not hydrate.");

    private static void AssertMalformedUuidRejection(
        MoveHandlerFixture fixture,
        string description)
    {
        Check.Equal(
            0,
            fixture.Store.MoveCount,
            $"{description} cannot downgrade to compatibility store");
        Check.Equal(
            0,
            fixture.Executor!.ReplayCount,
            $"{description} cannot reach durable replay");
        AssertRejectedResponse(
            fixture,
            SecureLegacyCommandDisposition.Rejected,
            description);
    }

    private static void AssertProjectionIsolation(
        MoveHandlerFixture fixture,
        string description)
    {
        Check.Equal(
            1,
            fixture.SnapshotReader.ReadCount,
            $"{description} reloads once");
        Check.Equal(
            fixture.PersistedBag,
            fixture.LiveCharacter.KitBag,
            $"{description} reloads authoritative bag");
        Check.Equal(
            fixture.OriginalEquipment,
            fixture.LiveCharacter.Equipment,
            $"{description} preserves live equipment");
        Check.Equal(
            456_789,
            fixture.LiveCharacter.Silver,
            $"{description} preserves live Silver");
        Check.Equal(
            98_765,
            fixture.LiveCharacter.Gold,
            $"{description} preserves live Gold");
        Check.Equal(
            82.5f,
            fixture.LiveCharacter.PositionX,
            $"{description} preserves live X");
        Check.Equal(
            -61.25f,
            fixture.LiveCharacter.PositionZ,
            $"{description} preserves live Z");
        Check.Equal(
            777,
            fixture.LiveCharacter.CurrentHp,
            $"{description} preserves live HP");
        Check.Equal(
            333,
            fixture.LiveCharacter.CurrentMp,
            $"{description} preserves live MP");
    }

    private static void AssertDurableResponse(
        MoveHandlerFixture fixture,
        KitBagItemMoveExecutionReceipt receipt,
        SecureLegacyCommandDisposition expectedDisposition,
        bool expectedMoveAcknowledgement,
        string description)
    {
        var packets = fixture.Transport.ReadClearLegacyPackets();
        Check.True(
            packets.Any(packet => ReadOpcode(packet) == 0x2731),
            $"{description} sends authoritative bag refresh");
        if (expectedMoveAcknowledgement)
        {
            AssertMoveAcknowledgement(
                packets[0],
                description);
        }
        else
        {
            Check.True(
                packets.All(packet => !IsMoveAcknowledgement(packet)),
                $"{description} sends no non-idempotent move ACK");
        }

        var result = fixture.Transport.CommandResults.Single();
        Check.True(
            result.Disposition == expectedDisposition,
            $"{description} secure disposition");
        Check.Equal(
            (ushort)CommandFamily.KitBagItemMove,
            result.CommandFamily,
            $"{description} secure command family");
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
        AssertCommandResultIsLast(
            fixture,
            packets.Count,
            description);
    }

    private static void AssertRejectedResponse(
        MoveHandlerFixture fixture,
        SecureLegacyCommandDisposition disposition,
        string description)
    {
        var packets = fixture.Transport.ReadClearLegacyPackets();
        Check.True(
            packets.Any(packet => ReadOpcode(packet) == 0x2731),
            $"{description} refreshes authoritative bag");
        Check.True(
            packets.All(packet => !IsMoveAcknowledgement(packet)),
            $"{description} sends no move ACK");
        var result = fixture.Transport.CommandResults.Single();
        Check.True(
            result.Disposition == disposition,
            $"{description} secure disposition");
        Check.Equal(
            (ushort)CommandFamily.KitBagItemMove,
            result.CommandFamily,
            $"{description} secure command family");
        Check.Equal(0u, result.ResultCode, $"{description} result code");
        Check.Equal(
            0ul,
            result.InventoryRevision,
            $"{description} inventory revision");
        Check.Equal(
            OperationId,
            result.OperationId,
            $"{description} operation UUID");
        AssertCommandResultIsLast(
            fixture,
            packets.Count,
            description);
    }

    private static void AssertMoveAcknowledgement(
        byte[] packet,
        string description)
    {
        Check.True(
            IsMoveAcknowledgement(packet),
            $"{description} uses stock move ACK");
        Check.Equal(
            (ushort)(SourceSlot / 24),
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(8, sizeof(ushort))),
            $"{description} ACK source page");
        Check.Equal(
            (ushort)(DestinationSlot / 24),
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(12, sizeof(ushort))),
            $"{description} ACK destination page");
    }

    private static bool IsMoveAcknowledgement(byte[] packet) =>
        packet.Length == 16 &&
        ReadOpcode(packet) == 0x2744 &&
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(12, sizeof(ushort))) != ushort.MaxValue;

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)));

    private static void AssertCommandResultIsLast(
        MoveHandlerFixture fixture,
        int packetCount,
        string description)
    {
        Check.Equal(
            "command-result",
            fixture.Transport.Events[^1],
            $"{description} sends family-14 0x0102 last");
        Check.Equal(
            packetCount,
            fixture.Transport.Events.Count(
                static value => value == "legacy"),
            $"{description} sends stock packets before 0x0102");
    }
}
