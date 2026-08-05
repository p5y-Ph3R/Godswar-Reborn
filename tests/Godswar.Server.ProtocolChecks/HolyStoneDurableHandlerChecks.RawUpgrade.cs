using System.Buffers.Binary;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private static async Task CheckRawUpgradeBridgeAsync()
    {
        await CheckRawUpgradeRejectsInitialActionBeforeClearAsync();
        await CheckRawUpgradeRejectsReorderedClearAsync();
        await CheckRawUpgradeResultPageRearmsSelectionAsync();
        await CheckRawAllFfUpgradeExecutesOnceAsync();
        await CheckRawScratchUpgradeExecutesOnceAsync();
        await CheckRawUpgradeRequiresLocalLegacyCapabilityAsync();
    }

    private static async Task
        CheckRawUpgradeRejectsInitialActionBeforeClearAsync()
    {
        await using var fixture = await CreateRawUpgradeFixtureAsync(
            hasLocalLegacyAuthenticationAccess: true);
        await OpenRawUpgradeAsync(fixture);
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(StoneSlot, selected: true));
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(WeaponSlot, selected: true));

        await InvokeAsync(
            fixture.Handler,
            CreateRawUpgradeFinal(populatedScratch: true));
        Check.Equal(
            0,
            fixture.Executor!.ExecuteCount,
            "initial raw Upgrade cannot execute before the stock A1 clear burst");
        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            "initial action-before-clear cannot reach legacy storage");
        AssertNpcResult(
            fixture.Transport.ReadLegacyPackets().Last(),
            HolyStoneNativeResults.WrongSelectionSubId,
            "initial raw Upgrade action before its clear burst");

        await InvokeAsync(
            fixture.Handler,
            CreateRawUpgradeFinal(populatedScratch: false));
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "all-FF action 401 without a completed clear burst remains navigation");
        AssertUpgradeNavigation(
            fixture.Transport.ReadLegacyPackets().Last(),
            "all-FF action 401 without a completed clear burst");
    }

    private static async Task CheckRawUpgradeRejectsReorderedClearAsync()
    {
        await using var fixture = await CreateRawUpgradeFixtureAsync(
            hasLocalLegacyAuthenticationAccess: true);
        await OpenRawUpgradeAsync(fixture);
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(StoneSlot, selected: true));
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(WeaponSlot, selected: true));

        // The clear burst must preserve the same order as the selections.
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(WeaponSlot, selected: false));
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(StoneSlot, selected: false));
        await InvokeAsync(
            fixture.Handler,
            CreateRawUpgradeFinal(populatedScratch: true));

        Check.Equal(
            0,
            fixture.Executor!.ExecuteCount,
            "reordered 10193 clears cannot authorize raw Upgrade");
        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            "reordered raw Upgrade clear cannot reach legacy storage");
        AssertNpcResult(
            fixture.Transport.ReadLegacyPackets().Last(),
            HolyStoneNativeResults.WrongSelectionSubId,
            "raw Upgrade with reordered clear burst");
    }

    private static async Task CheckRawAllFfUpgradeExecutesOnceAsync()
    {
        await using var fixture = await CreateRawUpgradeFixtureAsync(
            hasLocalLegacyAuthenticationAccess: true);
        await StageCommittedRawUpgradeSelectionsAsync(fixture);
        var action = CreateRawUpgradeFinal(populatedScratch: false);

        await InvokeAsync(fixture.Handler, action);

        AssertRawUpgradeExecutedExactlyOnce(
            fixture,
            "all-FF raw Upgrade");
        var envelope = fixture.Executor!.ExecutedEnvelope ??
            throw new InvalidOperationException(
                "raw Upgrade executor did not capture its envelope");
        Check.Equal(
            (int)CommandIdentityStrength.ServerOperationId,
            (int)envelope.IdentityStrength,
            "raw Upgrade executor sees a server operation identity");
        Check.Equal(
            (int)CommandTransportKind.LegacyTcp,
            (int)envelope.Connection.Transport,
            "raw Upgrade executor sees legacy TCP provenance");
        Check.True(
            envelope.Command.Identity.IsRawLocalServer &&
            envelope.Command.Identity.RawLocalConnectionId ==
                envelope.Connection.ConnectionId,
            "raw Upgrade identity is scoped to the active legacy connection");
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)HolyStoneCommandEnvelope.Validate(envelope),
            "raw Upgrade executor receives a valid durable envelope");

        await InvokeAsync(fixture.Handler, action);

        AssertRawUpgradeExecutedExactlyOnce(
            fixture,
            "duplicate all-FF raw Upgrade");
        AssertUpgradeNavigation(
            fixture.Transport.ReadLegacyPackets().Last(),
            "duplicate all-FF action 401");
    }

    private static async Task CheckRawScratchUpgradeExecutesOnceAsync()
    {
        await using var fixture = await CreateRawUpgradeFixtureAsync(
            hasLocalLegacyAuthenticationAccess: true);
        await StageCommittedRawUpgradeSelectionsAsync(fixture);
        var action = CreateRawUpgradeFinal(populatedScratch: true);

        await InvokeAsync(fixture.Handler, action);
        AssertRawUpgradeExecutedExactlyOnce(
            fixture,
            "scratch-tailed raw Upgrade");

        await InvokeAsync(fixture.Handler, action);

        AssertRawUpgradeExecutedExactlyOnce(
            fixture,
            "duplicate scratch-tailed raw Upgrade");
        AssertNpcResult(
            fixture.Transport.ReadLegacyPackets().Last(),
            HolyStoneNativeResults.WrongSelectionSubId,
            "duplicate scratch-tailed raw Upgrade");
    }

    private static async Task
        CheckRawUpgradeRequiresLocalLegacyCapabilityAsync()
    {
        await using var fixture = await CreateRawUpgradeFixtureAsync(
            hasLocalLegacyAuthenticationAccess: false);
        await StageCommittedRawUpgradeSelectionsAsync(fixture);

        await InvokeAsync(
            fixture.Handler,
            CreateRawUpgradeFinal(populatedScratch: false));

        Check.Equal(
            0,
            fixture.Executor!.ExecuteCount,
            "raw Upgrade without the local legacy capability cannot execute");
        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            "raw Upgrade without the local legacy capability cannot reach legacy storage");
        Check.True(
            fixture.Transport.Disconnected,
            "durable-command policy disconnects an unauthorized raw Upgrade session");
    }

    private static Task<RawHolyStoneFixture>
        CreateRawUpgradeFixtureAsync(
            bool hasLocalLegacyAuthenticationAccess) =>
        CreateRawFixtureAsync(
            durableExecutionResult:
                HolyStoneExecutionResult.InvalidIntent(),
            requiresDurablePlayerCommands: true,
            hasLocalLegacyAuthenticationAccess:
                hasLocalLegacyAuthenticationAccess);

    private static async Task StageCommittedRawUpgradeSelectionsAsync(
        RawHolyStoneFixture fixture)
    {
        await OpenRawUpgradeAsync(fixture);
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
    }

    private static async Task OpenRawUpgradeAsync(
        RawHolyStoneFixture fixture)
    {
        await InvokeAsync(
            fixture.Handler,
            CreateRawUpgradeFinal(populatedScratch: false));
        AssertUpgradeNavigation(
            fixture.Transport.ReadLegacyPackets().Last(),
            "raw Upgrade page open");
    }

    private static GamePacket CreateRawUpgradeFinal(
        bool populatedScratch) =>
        HolyStoneCommandContractChecks.CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.UpgradeSubId,
            args =>
            {
                if (populatedScratch)
                {
                    args[0] = 0;
                    args[HolyStoneProtocol.TargetArgumentIndex] = 6;
                    args[HolyStoneProtocol.StoneArgumentIndex] = 7;
                }
            });

    private static void AssertRawUpgradeExecutedExactlyOnce(
        RawHolyStoneFixture fixture,
        string description)
    {
        Check.Equal(
            0,
            fixture.Executor!.ReplayCount,
            $"{description} does not use secure UUID replay");
        Check.Equal(
            1,
            fixture.Executor.ExecuteCount,
            $"{description} reaches durable execution once");
        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            $"{description} never reaches retry-ambiguous legacy storage");
        Check.True(
            !fixture.Transport.Disconnected,
            $"{description} keeps the authorized raw session connected");
    }

    private static void AssertUpgradeNavigation(
        byte[] packet,
        string description)
    {
        var expected = new[] { 406, 506, 606 };
        Check.Equal(
            12 + (expected.Length * sizeof(int)),
            packet.Length,
            $"{description} returns the Upgrade page");
        for (var index = 0; index < expected.Length; index++)
        {
            Check.Equal(
                expected[index],
                BinaryPrimitives.ReadInt32LittleEndian(
                    packet.AsSpan(12 + (index * sizeof(int)))),
                $"{description} page sub-ID {index}");
        }
    }
}
