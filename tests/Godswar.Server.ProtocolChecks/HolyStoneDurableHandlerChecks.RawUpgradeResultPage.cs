using System.Buffers.Binary;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private static async Task
        CheckRawUpgradeResultPageRearmsSelectionAsync()
    {
        var receipt = CreateRejectedUpgradeReceipt();
        await using var fixture = await CreateRawFixtureAsync(
            durableExecutionResult:
                HolyStoneExecutionResult.TerminalRejected(receipt),
            requiresDurablePlayerCommands: true,
            hasLocalLegacyAuthenticationAccess: true);
        await StageCommittedRawUpgradeSelectionsAsync(fixture);

        var packetCount = fixture.Transport.ReadLegacyPackets().Count;
        await InvokeAsync(
            fixture.Handler,
            CreateRawUpgradeFinal(populatedScratch: false));

        Check.Equal(
            1,
            fixture.Executor!.ExecuteCount,
            "completed raw Upgrade reaches durable execution once");
        AssertRawUpgradeResultPage(
            fixture.Transport.ReadLegacyPackets()
                .Skip(packetCount)
                .ToArray(),
            receipt.NativeResultSubId,
            "completed raw Upgrade");

        // Sub-ID 3100 replaces initial-page A1 with A3. Stock A3 sends action
        // 401 while its selections are still live, then emits its clear burst.
        await StageRawUpgradeRetryLiveSelectionsAsync(fixture);
        packetCount = fixture.Transport.ReadLegacyPackets().Count;
        await InvokeAsync(
            fixture.Handler,
            CreateRawUpgradeFinal(populatedScratch: false));

        Check.Equal(
            2,
            fixture.Executor.ExecuteCount,
            "raw Upgrade result page re-arms a second selection attempt");
        AssertRawUpgradeResultPage(
            fixture.Transport.ReadLegacyPackets()
                .Skip(packetCount)
                .ToArray(),
            receipt.NativeResultSubId,
            "re-armed raw Upgrade");

        // These are the late control clears emitted after A3 has already sent
        // action 401. The successful action is one-shot, and the late cleanup
        // must not damage the fresh context created by its 3100 result page.
        await ClearRawUpgradeRetrySelectionsAsync(fixture);
        Check.Equal(
            2,
            fixture.Executor.ExecuteCount,
            "post-3100 late clears never execute another Upgrade");

        await StageRawUpgradeRetryLiveSelectionsAsync(fixture);
        await InvokeAsync(
            fixture.Handler,
            CreateRawUpgradeFinal(populatedScratch: true));
        Check.Equal(
            3,
            fixture.Executor.ExecuteCount,
            "late A3 clears leave the next 3100 retry context usable");
    }

    private static async Task StageRawUpgradeRetryLiveSelectionsAsync(
        RawHolyStoneFixture fixture)
    {
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(StoneSlot, selected: true));
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(WeaponSlot, selected: true));
    }

    private static async Task ClearRawUpgradeRetrySelectionsAsync(
        RawHolyStoneFixture fixture)
    {
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(StoneSlot, selected: false));
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(WeaponSlot, selected: false));
    }

    private static void AssertRawUpgradeResultPage(
        IReadOnlyList<byte[]> packets,
        int expectedResultSubId,
        string description)
    {
        Check.True(
            packets.Count > 1,
            $"{description} emits an authoritative projection");
        var actionResponses = packets
            .Select((packet, index) => (packet, index))
            .Where(entry => Opcode(entry.packet) ==
                Opcodes.NpcFunctionActionResponse)
            .ToArray();
        Check.Equal(
            1,
            actionResponses.Length,
            $"{description} emits one final NPC action response");

        var (resultPage, resultIndex) = actionResponses[0];
        Check.Equal(
            packets.Count - 1,
            resultIndex,
            $"{description} sends its result page after projection");
        Check.Equal(
            20,
            resultPage.Length,
            $"{description} result page has exactly two sub-IDs");
        Check.Equal(
            HolyStoneProtocol.UpgradeResultPanelSubId,
            BinaryPrimitives.ReadInt32LittleEndian(
                resultPage.AsSpan(12, sizeof(int))),
            $"{description} rebuilds Upgrade controls first");
        Check.Equal(
            expectedResultSubId,
            BinaryPrimitives.ReadInt32LittleEndian(
                resultPage.AsSpan(16, sizeof(int))),
            $"{description} displays the outcome second");

        var firstProjectionIndex = Array.FindIndex(
            packets.ToArray(),
            packet => Opcode(packet) is
                0x27B6 or 0x27D9 or 0x2731 or 0x2748);
        Check.True(
            firstProjectionIndex >= 0 &&
            firstProjectionIndex < resultIndex,
            $"{description} projects authoritative state before the panel");
        Check.True(
            packets
                .Select((packet, index) => (packet, index))
                .Where(entry =>
                    IsKitBagDeletionAcknowledgement(entry.packet))
                .All(entry => entry.index < resultIndex),
            $"{description} sends deletion acknowledgements before the panel");
    }

    private static ushort Opcode(byte[] packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            packet.AsSpan(2, sizeof(ushort)));
}
