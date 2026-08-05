using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private static async Task CheckUpgradeSelectionRoutingAsync()
    {
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.ReplayNotFound(),
            HolyStoneExecutionResult.InvalidIntent(),
            expectedOperation: HolyStoneCommandOperation.Upgrade);

        await InvokeAsync(
            fixture.Handler,
            HolyStoneCommandContractChecks.CreatePacket(
                HolyStoneProtocol.SpartaNpcId,
                HolyStoneProtocol.UpgradeSubId,
                static _ => { }));
        Check.Equal(
            0,
            fixture.Executor!.ReplayCount,
            "opening Upgrade does not read the durable inbox");
        _ = fixture.Transport.ReadClearLegacyPackets();

        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(StoneSlot, selected: true));
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(WeaponSlot, selected: true));
        // NpcFunEment clears its ItemBtn controls immediately before action
        // 401. The bounded clear snapshot must remain correlated exactly once.
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(StoneSlot, selected: false));
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(WeaponSlot, selected: false));
        await InvokeAsync(
            fixture.Handler,
            HolyStoneCommandContractChecks.CreatePacket(
                HolyStoneProtocol.SpartaNpcId,
                HolyStoneProtocol.UpgradeSubId,
                static _ => { },
                OperationId));

        Check.Equal(
            1,
            fixture.Executor.ReplayCount,
            "stock action 401 checks the Upgrade inbox");
        Check.Equal(
            1,
            fixture.Executor.ExecuteCount,
            "stock action 401 executes one durable Upgrade");
        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            "secure Upgrade never reaches retry-ambiguous legacy storage");
        var command = fixture.Executor.ExecutedCommand ??
            throw new InvalidOperationException(
                "Upgrade executor did not capture its command.");
        Check.True(
            command.Operation == HolyStoneCommandOperation.Upgrade &&
            command.TargetLocation == HolyStoneTargetLocation.KitBag &&
            command.TargetSlot == StoneSlot &&
            command.StoneKitBagSlot == WeaponSlot &&
            command.CatalystKitBagSlot ==
                HolyStoneCommandEnvelope.NoStoneKitBagSlot,
            "ordered 10193 selections become target, Eclipse, and optional catalyst roles");
        Check.Equal(
            StoneBefore.ToCompactString(),
            command.ExpectedTargetCompactItemState,
            "Upgrade captures the full Holy Stone state");
        Check.Equal(
            WeaponBefore.ToCompactString(),
            command.ExpectedStoneCompactItemState,
            "Upgrade captures the full Eclipse-slot state");
        Check.Equal(
            "[]",
            command.ExpectedCatalystCompactItemState,
            "two-slot Upgrade records no optional catalyst");

        var result = fixture.Transport.CommandResults.Single();
        Check.Equal(
            (ushort)CommandFamily.HolyStoneUpgrade,
            result.CommandFamily,
            "Upgrade settlement uses command family 42");
        Check.Equal(
            (int)SecureLegacyCommandDisposition.Rejected,
            (int)result.Disposition,
            "fixture's injected invalid execution settles as rejected");

        await CheckSelectionlessUpgradeReplayAsync();
    }

    private static async Task CheckSelectionlessUpgradeReplayAsync()
    {
        var receipt = CreateRejectedUpgradeReceipt();
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.ReplayNotFound(),
            HolyStoneExecutionResult.TerminalRejected(receipt),
            expectedOperation: HolyStoneCommandOperation.Upgrade);
        fixture.Executor!.ReplayResultAfterExecution =
            HolyStoneExecutionResult.Duplicate(receipt);

        await InvokeAsync(
            fixture.Handler,
            HolyStoneCommandContractChecks.CreatePacket(
                HolyStoneProtocol.SpartaNpcId,
                HolyStoneProtocol.UpgradeSubId,
                static _ => { }));
        _ = fixture.Transport.ReadClearLegacyPackets();
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(StoneSlot, selected: true));
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(WeaponSlot, selected: true));
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(StoneSlot, selected: false));
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(WeaponSlot, selected: false));
        var action = HolyStoneCommandContractChecks.CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.UpgradeSubId,
            static _ => { },
            OperationId);
        await InvokeAsync(fixture.Handler, action);

        // The first response is assumed lost. No selection context survives,
        // but the same secure UUID must still recover its durable receipt.
        await InvokeAsync(fixture.Handler, action);

        Check.Equal(
            2,
            fixture.Executor.ReplayCount,
            "selectionless Upgrade retry checks the durable inbox");
        Check.Equal(
            1,
            fixture.Executor.ExecuteCount,
            "selectionless Upgrade retry never executes twice");
        Check.True(
            fixture.Transport.CommandResults.Any(result =>
                result.CommandFamily ==
                    (ushort)CommandFamily.HolyStoneUpgrade &&
                result.Disposition ==
                    SecureLegacyCommandDisposition.Replayed),
            "selectionless Upgrade retry settles with the stored result");
    }

    private static HolyStoneExecutionReceipt CreateRejectedUpgradeReceipt()
    {
        return new HolyStoneExecutionReceipt(
            characterId: 19,
            HolyStoneCommandOperation.Upgrade,
            HolyStoneCommandEnvelope.SpartaNpcId,
            HolyStoneCommandEnvelope.DialogIndex,
            HolyStoneCommandResultStatus.EclipseStoneRequired,
            HolyStoneNativeResults.EclipseStoneRequiredSubId,
            HolyStoneTargetLocation.KitBag,
            StoneSlot,
            HolyStoneCommandEnvelope.ServerSelectedSocketIndex,
            targetItemInstanceId: 101,
            StoneBefore.ToCompactString(),
            StoneBefore.ToCompactString(),
            StoneBefore.ToCompactString(),
            WeaponSlot,
            stoneItemInstanceId: 102,
            WeaponBefore.ToCompactString(),
            WeaponBefore.ToCompactString(),
            WeaponBefore.ToCompactString(),
            outputKitBagSlot: -1,
            outputItemInstanceId: null,
            outputBeforeCompactItemState: null,
            outputAfterCompactItemState: null,
            goldSpent: 0,
            goldBefore: 98_765,
            goldAfter: 98_765,
            walletRevision: 0,
            inventoryRevision: 0,
            auditReference: "1",
            outboxEventId: null);
    }
}
