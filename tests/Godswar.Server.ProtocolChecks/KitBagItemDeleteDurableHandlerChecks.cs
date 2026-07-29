using System.Buffers.Binary;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class KitBagItemDeleteDurableHandlerChecks
{
    public static async Task RunAsync()
    {
        await CheckCommitOrderingAndProjectionIsolationAsync();
        await CheckReplayPrecedesEmptySlotCaptureAsync();
        await CheckEmptySlotRejectionAsync();
        await CheckStaleSelectionRejectionAsync();
        await CheckProviderOutageLeavesPendingAsync();
        await CheckProjectionFailureLeavesPendingAsync();
        await CheckMismatchedReceiptLeavesPendingAsync();
        await CheckRequestConflictIsTerminalAsync();
        await CheckCancellationLeavesPendingAsync();
        await CheckTokenlessRequestUsesCompatibilityPathAsync();
        await CheckNonCanonicalLengthUsesCompatibilityPathAsync();
    }

    private static async Task
        CheckCommitOrderingAndProjectionIsolationAsync()
    {
        var receipt = CreateReceipt(
            KitBagItemDeleteResultStatus.Deleted);
        await using var fixture = CreateFixture(
            KitBagItemDeleteExecutionResult.ReplayNotFound(),
            KitBagItemDeleteExecutionResult.Committed(receipt));

        await InvokeDeleteAsync(fixture.Handler, OperationId);

        Check.Equal(
            1,
            fixture.Executor!.ReplayCount,
            "new kit-bag deletion checks the permanent inbox first");
        Check.Equal(
            1,
            fixture.Executor.ExecuteCount,
            "new kit-bag deletion executes exactly once");
        var command = fixture.Executor.ExecutedCommand ??
            throw new InvalidOperationException(
                "Kit-bag delete executor did not capture its command.");
        Check.Equal(
            OperationId,
            command.ClientOperationId,
            "kit-bag deletion preserves the client operation UUID");
        Check.Equal(
            DeleteSlot,
            command.KitBagSlot,
            "kit-bag deletion preserves the source slot");
        Check.Equal(
            DeletedItem.ToCompactString(),
            command.ExpectedCompactItemState,
            "kit-bag deletion preserves the full selected item state");

        Check.Equal(
            1,
            fixture.SnapshotReader.ReadCount,
            "committed kit-bag deletion reloads once");
        Check.Equal(
            fixture.PersistedBag,
            fixture.LiveCharacter.KitBag,
            "kit-bag deletion reloads the authoritative bag");
        AssertRuntimeStatePreserved(
            fixture.LiveCharacter,
            "committed kit-bag deletion");
        AssertDurableResponse(
            fixture,
            receipt,
            SecureLegacyCommandDisposition.Applied,
            expectedDeleteAcknowledgement: true,
            "committed kit-bag deletion");
    }

    private static async Task
        CheckReplayPrecedesEmptySlotCaptureAsync()
    {
        var receipt = CreateReceipt(
            KitBagItemDeleteResultStatus.Deleted);
        await using var fixture = CreateFixture(
            KitBagItemDeleteExecutionResult.Duplicate(receipt),
            liveItem: CompactItemEntry.Empty);

        await InvokeDeleteAsync(fixture.Handler, OperationId);

        Check.Equal(
            1,
            fixture.Executor!.ReplayCount,
            "empty-slot retry checks its permanent inbox");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "durable replay does not recapture the now-empty slot");
        AssertDurableResponse(
            fixture,
            receipt,
            SecureLegacyCommandDisposition.Replayed,
            expectedDeleteAcknowledgement: true,
            "replayed kit-bag deletion");
    }

    private static async Task CheckEmptySlotRejectionAsync()
    {
        var receipt = CreateReceipt(
            KitBagItemDeleteResultStatus.EmptySlot);
        await using var fixture = CreateFixture(
            KitBagItemDeleteExecutionResult.ReplayNotFound(),
            KitBagItemDeleteExecutionResult.TerminalRejected(receipt),
            liveItem: CompactItemEntry.Empty);

        await InvokeDeleteAsync(fixture.Handler, OperationId);

        AssertDurableResponse(
            fixture,
            receipt,
            SecureLegacyCommandDisposition.Rejected,
            expectedDeleteAcknowledgement: false,
            "empty-slot kit-bag deletion");
    }

    private static async Task CheckStaleSelectionRejectionAsync()
    {
        var receipt = CreateReceipt(
            KitBagItemDeleteResultStatus.StaleSelection);
        await using var fixture = CreateFixture(
            KitBagItemDeleteExecutionResult.ReplayNotFound(),
            KitBagItemDeleteExecutionResult.TerminalRejected(receipt),
            persistedItem: ReplacementItem);

        await InvokeDeleteAsync(fixture.Handler, OperationId);

        Check.Equal(
            ReplacementItem,
            KitBagSlots.GetItem(
                fixture.LiveCharacter.KitBag,
                DeleteSlot),
            "stale delete refreshes the authoritative replacement");
        AssertRuntimeStatePreserved(
            fixture.LiveCharacter,
            "stale kit-bag deletion");
        AssertDurableResponse(
            fixture,
            receipt,
            SecureLegacyCommandDisposition.Rejected,
            expectedDeleteAcknowledgement: false,
            "stale-selection kit-bag deletion");
    }

    private static async Task CheckProviderOutageLeavesPendingAsync()
    {
        await using var fixture = CreateFixture(
            KitBagItemDeleteExecutionResult.ReplayNotFound(),
            providerUnavailable: true);

        await InvokeDeleteAsync(fixture.Handler, OperationId);

        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "delete provider outage emits no terminal result");
        Check.Equal(
            0,
            fixture.SnapshotReader.ReadCount,
            "delete provider outage does not project");
    }

    private static async Task CheckProjectionFailureLeavesPendingAsync()
    {
        var receipt = CreateReceipt(
            KitBagItemDeleteResultStatus.Deleted);
        await using var fixture = CreateFixture(
            KitBagItemDeleteExecutionResult.ReplayNotFound(),
            KitBagItemDeleteExecutionResult.Committed(receipt),
            projectionFails: true);

        await InvokeDeleteAsync(fixture.Handler, OperationId);

        Check.Equal(
            1,
            fixture.Executor!.ExecuteCount,
            "projection failure follows a durable deletion");
        Check.Equal(
            1,
            fixture.SnapshotReader.ReadCount,
            "delete projection failure is observed");
        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "uncertain delete projection emits no terminal result");
    }

    private static async Task CheckMismatchedReceiptLeavesPendingAsync()
    {
        var receipt = CreateReceipt(
            KitBagItemDeleteResultStatus.Deleted,
            characterId: 20);
        await using var fixture = CreateFixture(
            KitBagItemDeleteExecutionResult.ReplayNotFound(),
            KitBagItemDeleteExecutionResult.Committed(receipt));

        await InvokeDeleteAsync(fixture.Handler, OperationId);

        Check.Equal(
            1,
            fixture.Executor!.ExecuteCount,
            "mismatched delete receipt follows execution");
        Check.Equal(
            0,
            fixture.SnapshotReader.ReadCount,
            "invalid delete receipt is not projected");
        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "unknown delete receipt identity emits no terminal result");
    }

    private static async Task CheckRequestConflictIsTerminalAsync()
    {
        await using var fixture = CreateFixture(
            KitBagItemDeleteExecutionResult.RequestHashConflict());

        await InvokeDeleteAsync(fixture.Handler, OperationId);

        Check.Equal(
            1,
            fixture.Executor!.ReplayCount,
            "delete request conflict is found through replay");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "delete request conflict cannot mutate");
        Check.Equal(
            1,
            fixture.SnapshotReader.ReadCount,
            "delete request conflict refreshes the bag");
        AssertRejectedResponse(
            fixture,
            SecureLegacyCommandDisposition.Conflict,
            "kit-bag delete request conflict");
    }

    private static async Task CheckCancellationLeavesPendingAsync()
    {
        await using var fixture = CreateFixture(
            KitBagItemDeleteExecutionResult.ReplayNotFound());
        using var source = new CancellationTokenSource();
        source.Cancel();

        try
        {
            await InvokeDeleteAsync(
                fixture.Handler,
                OperationId,
                cancellationToken: source.Token);
            throw new InvalidOperationException(
                "Cancelled kit-bag deletion unexpectedly completed.");
        }
        catch (OperationCanceledException)
        {
        }

        Check.Equal(
            0,
            fixture.Transport.Events.Count,
            "cancelled kit-bag deletion emits no terminal result");
        Check.Equal(
            0,
            fixture.SnapshotReader.ReadCount,
            "cancelled kit-bag deletion does not project");
    }

    private static async Task
        CheckTokenlessRequestUsesCompatibilityPathAsync()
    {
        await using var fixture = CreateFixture(
            KitBagItemDeleteExecutionResult.ReplayNotFound());
        fixture.Store.Result =
            CreateLegacyDeleteResult(fixture.PersistedBag);

        await InvokeDeleteAsync(fixture.Handler, operationId: null);

        AssertCompatibilityResponse(
            fixture,
            "tokenless kit-bag deletion");
    }

    private static async Task
        CheckNonCanonicalLengthUsesCompatibilityPathAsync()
    {
        await using var fixture = CreateFixture(
            KitBagItemDeleteExecutionResult.ReplayNotFound());
        fixture.Store.Result =
            CreateLegacyDeleteResult(fixture.PersistedBag);

        await InvokeDeleteAsync(
            fixture.Handler,
            OperationId,
            packetLength: 32);

        AssertCompatibilityResponse(
            fixture,
            "non-canonical-length kit-bag deletion");
    }

    private static GameCharacter CreateLegacyDeleteResult(
        string kitBag) =>
        CharacterLoadSnapshotHydrator.Hydrate(
            WithBag(
                CharacterSnapshotContractChecks.CreateValidSnapshot(),
                kitBag))?.Character
        ?? throw new InvalidOperationException(
            "Legacy kit-bag delete result did not hydrate.");

    private static void AssertCompatibilityResponse(
        DeleteHandlerFixture fixture,
        string description)
    {
        Check.Equal(
            1,
            fixture.Store.DeleteCount,
            $"{description} uses the compatibility store");
        Check.Equal(
            0,
            fixture.Executor!.ReplayCount,
            $"{description} does not check the durable inbox");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            $"{description} does not execute a durable command");
        Check.Equal(
            0,
            fixture.Transport.CommandResults.Count,
            $"{description} sends no secure command result");
        var packets = fixture.Transport.ReadClearLegacyPackets();
        Check.Equal(
            1,
            packets.Count,
            $"{description} sends one stock acknowledgement");
        AssertDeleteAcknowledgement(packets[0], description);
    }

    private static void AssertDurableResponse(
        DeleteHandlerFixture fixture,
        KitBagItemDeleteExecutionReceipt receipt,
        SecureLegacyCommandDisposition expectedDisposition,
        bool expectedDeleteAcknowledgement,
        string description)
    {
        var packets = fixture.Transport.ReadClearLegacyPackets();
        Check.True(
            packets.Count >= (expectedDeleteAcknowledgement ? 2 : 1),
            $"{description} sends its stock projection");
        var refreshOffset = 0;
        if (expectedDeleteAcknowledgement)
        {
            AssertDeleteAcknowledgement(packets[0], description);
            refreshOffset = 1;
        }
        else
        {
            Check.True(
                packets.All(
                    packet =>
                        !IsDeleteAcknowledgement(packet)),
                $"{description} sends no false deletion acknowledgement");
        }
        Check.True(
            packets.Skip(refreshOffset).Any(
                packet => ReadOpcode(packet) == 0x2731),
            $"{description} sends an authoritative bag refresh");

        var result = fixture.Transport.CommandResults.Single();
        Check.True(
            result.Disposition == expectedDisposition,
            $"{description} secure disposition");
        Check.Equal(
            (ushort)CommandFamily.KitBagItemDelete,
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
        AssertCommandResultIsLast(fixture, packets.Count, description);
    }

    private static void AssertRejectedResponse(
        DeleteHandlerFixture fixture,
        SecureLegacyCommandDisposition disposition,
        string description)
    {
        var packets = fixture.Transport.ReadClearLegacyPackets();
        Check.True(
            packets.Any(packet => ReadOpcode(packet) == 0x2731),
            $"{description} refreshes the authoritative bag");
        Check.True(
            packets.All(packet => !IsDeleteAcknowledgement(packet)),
            $"{description} sends no deletion acknowledgement");
        var result = fixture.Transport.CommandResults.Single();
        Check.True(
            result.Disposition == disposition,
            $"{description} secure disposition");
        Check.Equal(
            (ushort)CommandFamily.KitBagItemDelete,
            result.CommandFamily,
            $"{description} secure command family");
        Check.Equal(
            0u,
            result.ResultCode,
            $"{description} has no durable result status");
        Check.Equal(
            0ul,
            result.InventoryRevision,
            $"{description} has no durable revision");
        Check.Equal(
            OperationId,
            result.OperationId,
            $"{description} operation UUID");
        AssertCommandResultIsLast(fixture, packets.Count, description);
    }

    private static void AssertCommandResultIsLast(
        DeleteHandlerFixture fixture,
        int packetCount,
        string description)
    {
        Check.Equal(
            "command-result",
            fixture.Transport.Events[^1],
            $"{description} sends family-3 0x0102 last");
        Check.Equal(
            packetCount,
            fixture.Transport.Events.Count(
                static value => value == "legacy"),
            $"{description} sends all stock packets before 0x0102");
    }

    private static void AssertDeleteAcknowledgement(
        byte[] packet,
        string description)
    {
        Check.True(
            IsDeleteAcknowledgement(packet),
            $"{description} uses the stock delete acknowledgement");
        Check.Equal(
            (ushort)(DeleteSlot / 24),
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(8, sizeof(ushort))),
            $"{description} acknowledgement page");
        Check.Equal(
            (ushort)(DeleteSlot % 24),
            BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(10, sizeof(ushort))),
            $"{description} acknowledgement index");
    }

    private static bool IsDeleteAcknowledgement(byte[] packet) =>
        packet.Length == 16 &&
        ReadOpcode(packet) == 0x2744 &&
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(12, sizeof(ushort))) == ushort.MaxValue &&
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(14, sizeof(ushort))) == ushort.MaxValue;

    private static ushort ReadOpcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)));

    private static void AssertRuntimeStatePreserved(
        GameCharacter character,
        string description)
    {
        Check.Equal(
            456_789,
            character.Silver,
            $"{description} preserves live Silver");
        Check.Equal(
            98_765,
            character.Gold,
            $"{description} preserves live Gold");
        Check.Equal(
            82.5f,
            character.PositionX,
            $"{description} preserves live X");
        Check.Equal(
            -61.25f,
            character.PositionZ,
            $"{description} preserves live Z");
        Check.Equal(
            777,
            character.CurrentHp,
            $"{description} preserves live HP");
        Check.Equal(
            333,
            character.CurrentMp,
            $"{description} preserves live MP");
    }
}
