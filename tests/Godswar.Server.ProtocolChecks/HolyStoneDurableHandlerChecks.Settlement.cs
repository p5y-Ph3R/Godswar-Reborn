using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private static async Task CheckSettlementEvictionsAsync()
    {
        await CheckTerminalSettlementEvictionAsync(
            HolyStoneCommandResultStatus.StaleTarget);
        await CheckTerminalSettlementEvictionAsync(
            HolyStoneCommandResultStatus.StaleStone);
        await CheckReplayDivergenceSettlementEvictionAsync();
        await CheckNonDurableSettlementEvictionsAsync();
    }

    private static async Task CheckTerminalSettlementEvictionAsync(
        HolyStoneCommandResultStatus status)
    {
        var receipt = CreateRejectedMountReceipt(status);
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.TerminalRejected(receipt));

        await InvokeMountAsync(fixture, OperationId);

        AssertSettlementResponse(
            fixture,
            receipt.NativeResultSubId,
            SecureLegacyCommandDisposition.Rejected,
            expectedChangedSlots: [StoneSlot],
            $"terminal {status} Mount");
    }

    private static async Task
        CheckReplayDivergenceSettlementEvictionAsync()
    {
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.Duplicate(CreateMountReceipt()));

        await InvokeMountAsync(fixture, OperationId);

        AssertSettlementResponse(
            fixture,
            HolyStoneNativeResults.MountedSubId,
            SecureLegacyCommandDisposition.Replayed,
            expectedChangedSlots: [StoneSlot],
            "replayed Mount after local divergence");
    }

    private static async Task
        CheckNonDurableSettlementEvictionsAsync()
    {
        var outcomes = new[]
        {
            (
                Result: HolyStoneExecutionResult.RequestHashConflict(),
                Disposition: SecureLegacyCommandDisposition.Conflict,
                Name: "request-hash conflict"),
            (
                Result: HolyStoneExecutionResult.InvalidIntent(),
                Disposition: SecureLegacyCommandDisposition.Rejected,
                Name: "invalid intent"),
            (
                Result: HolyStoneExecutionResult.PreconditionFailed(),
                Disposition: SecureLegacyCommandDisposition.Rejected,
                Name: "precondition failure")
        };

        foreach (var outcome in outcomes)
        {
            await using var fixture = await CreateFixtureAsync(
                outcome.Result);

            await InvokeMountAsync(fixture, OperationId);

            AssertSettlementResponse(
                fixture,
                HolyStoneNativeResults.WrongSelectionSubId,
                outcome.Disposition,
                expectedChangedSlots: [StoneSlot],
                $"non-durable {outcome.Name}");
        }
    }

    private static HolyStoneExecutionReceipt CreateRejectedMountReceipt(
        HolyStoneCommandResultStatus status)
    {
        var staleTarget =
            status == HolyStoneCommandResultStatus.StaleTarget;
        return new HolyStoneExecutionReceipt(
            characterId: 19,
            HolyStoneCommandOperation.Mount,
            HolyStoneCommandEnvelope.SpartaNpcId,
            HolyStoneCommandEnvelope.DialogIndex,
            status,
            HolyStoneNativeResults.GetResultSubId(
                HolyStoneCommandOperation.Mount,
                status),
            HolyStoneTargetLocation.Equipment,
            HolyStoneCommandEnvelope.WeaponEquipmentSlot,
            HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
            targetItemInstanceId: 71,
            WeaponBefore.ToCompactString(),
            (staleTarget ? WeaponAfter : WeaponBefore)
                .ToCompactString(),
            (staleTarget ? WeaponAfter : WeaponBefore)
                .ToCompactString(),
            StoneSlot,
            stoneItemInstanceId: staleTarget ? 72 : null,
            StoneBefore.ToCompactString(),
            (staleTarget ? StoneBefore : CompactItemEntry.Empty)
                .ToCompactString(),
            (staleTarget ? StoneBefore : CompactItemEntry.Empty)
                .ToCompactString(),
            outputKitBagSlot: -1,
            outputItemInstanceId: null,
            outputBeforeCompactItemState: null,
            outputAfterCompactItemState: null,
            goldSpent: 0,
            goldBefore: 10,
            goldAfter: 10,
            walletRevision: 0,
            inventoryRevision: 0,
            auditReference: $"audit:holy-stone:{status}",
            outboxEventId: null);
    }

    private static void AssertSettlementResponse(
        HolyStoneFixture fixture,
        int expectedNativeResultSubId,
        SecureLegacyCommandDisposition expectedDisposition,
        int[] expectedChangedSlots,
        string description)
    {
        var packets = fixture.Transport.ReadClearLegacyPackets();
        Check.True(
            packets.Count > 2,
            $"{description} emits authoritative rehydration");
        AssertNpcResult(
            packets[0],
            expectedNativeResultSubId,
            description);

        var acknowledgements = packets
            .Select((packet, index) => (packet, index))
            .Where(entry =>
                IsKitBagDeletionAcknowledgement(entry.packet))
            .ToArray();
        Check.Equal(
            expectedChangedSlots.Length,
            acknowledgements.Length,
            $"{description} changed-slot clear count");
        var actualSlots = acknowledgements
            .Select(entry =>
                BinaryPrimitives.ReadUInt16LittleEndian(
                    entry.packet.AsSpan(8, sizeof(ushort))) * 24 +
                BinaryPrimitives.ReadUInt16LittleEndian(
                    entry.packet.AsSpan(10, sizeof(ushort))))
            .ToArray();
        Check.True(
            actualSlots.SequenceEqual(expectedChangedSlots),
            $"{description} clears exact changed slots");

        var firstHydrationIndex = Array.FindIndex(
            packets.ToArray(),
            packet =>
                BinaryPrimitives.ReadUInt16LittleEndian(
                    packet.AsSpan(2, sizeof(ushort))) is
                    0x27B6 or 0x27D9 or 0x2731 or 0x2748);
        Check.True(
            firstHydrationIndex > 0,
            $"{description} contains authoritative snapshots");
        Check.True(
            acknowledgements.All(entry =>
                entry.index > 0 &&
                entry.index < firstHydrationIndex),
            $"{description} clears changed slots before rehydration");

        var result = fixture.Transport.CommandResults.Single();
        Check.Equal(
            (int)expectedDisposition,
            (int)result.Disposition,
            $"{description} disposition");
        Check.Equal(
            (ushort)CommandFamily.HolyStoneMount,
            result.CommandFamily,
            $"{description} family");
        Check.Equal(
            "command-result",
            fixture.Transport.Events[^1],
            $"{description} sends secure result last");
    }
}
