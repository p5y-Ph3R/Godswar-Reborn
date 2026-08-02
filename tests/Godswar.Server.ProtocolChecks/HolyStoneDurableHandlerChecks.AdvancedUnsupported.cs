using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private static async Task CheckAdvancedDrillStaysFailClosedAsync()
    {
        await AssertSecureAdvancedDrillPageAsync();
        await AssertSecureAdvancedDrillRejectedAsync(
            operationId: null,
            static args => args[6] = 205,
            "secure Advanced Drill unknown value shape");
        await AssertSecureAdvancedDrillRejectedAsync(
            OperationId,
            static args => args[7] = 100,
            "UUID-bearing Advanced Drill unknown value shape");

        await AssertRawAdvancedDrillPageAsync();
        await AssertRawAdvancedDrillRejectedAsync(
            static args => args[10] = 1,
            "raw Advanced Drill unknown value shape");
    }

    private static async Task AssertSecureAdvancedDrillPageAsync()
    {
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.ReplayNotFound());
        await InvokeAsync(
            fixture.Handler,
            HolyStoneCommandContractChecks.CreatePacket(
                HolyStoneProtocol.SpartaNpcId,
                HolyStoneProtocol.AdvancedDrillSubId,
                static _ => { }));

        Check.Equal(
            0,
            fixture.Executor!.ReplayCount,
            "secure Advanced Drill page cannot read the durable inbox");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            "secure Advanced Drill page cannot execute a mutation");
        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            "secure Advanced Drill page cannot reach the legacy store");
        AssertAdvancedDrillPage(
            fixture.Transport.ReadClearLegacyPackets().Single(),
            "secure Advanced Drill page");
    }

    private static async Task AssertRawAdvancedDrillPageAsync()
    {
        await using var fixture = await CreateRawFixtureAsync();
        await InvokeAsync(
            fixture.Handler,
            HolyStoneCommandContractChecks.CreatePacket(
                HolyStoneProtocol.SpartaNpcId,
                HolyStoneProtocol.AdvancedDrillSubId,
                static _ => { }));

        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            "raw Advanced Drill page cannot reach the legacy store");
        AssertAdvancedDrillPage(
            fixture.Transport.ReadLegacyPackets().Single(),
            "raw Advanced Drill page");
    }

    private static void AssertAdvancedDrillPage(
        byte[] packet,
        string description)
    {
        Check.Equal(24, packet.Length, $"{description} response length");
        AssertNpcResult(
            packet,
            HolyStoneProtocol.AdvancedDrillPageSubId,
            description);
        Check.Equal(
            HolyStoneProtocol.AdvancedDrillEquipmentSlotSubId,
            BitConverter.ToInt32(packet, 16),
            $"{description} equipment slot label");
        Check.Equal(
            HolyStoneProtocol.AdvancedDrillSpellSlotSubId,
            BitConverter.ToInt32(packet, 20),
            $"{description} spell slot label");
    }

    private static async Task AssertSecureAdvancedDrillRejectedAsync(
        Guid? operationId,
        Action<int[]> configure,
        string description)
    {
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.ReplayNotFound());
        var packet = HolyStoneCommandContractChecks.CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.AdvancedDrillSubId,
            configure,
            operationId);

        await InvokeAsync(fixture.Handler, packet);

        Check.Equal(
            0,
            fixture.Executor!.ReplayCount,
            $"{description} cannot read the durable inbox");
        Check.Equal(
            0,
            fixture.Executor.ExecuteCount,
            $"{description} cannot execute a durable mutation");
        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            $"{description} cannot reach the legacy store");
        Check.Equal(
            0,
            fixture.Transport.CommandResults.Count,
            $"{description} cannot invent a command-family result");
        AssertNpcResult(
            fixture.Transport.ReadClearLegacyPackets().Single(),
            HolyStoneNativeResults.WrongSelectionSubId,
            description);
    }

    private static async Task AssertRawAdvancedDrillRejectedAsync(
        Action<int[]> configure,
        string description)
    {
        await using var fixture = await CreateRawFixtureAsync();
        var packet = HolyStoneCommandContractChecks.CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.AdvancedDrillSubId,
            configure);

        await InvokeAsync(fixture.Handler, packet);

        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            $"{description} cannot reach the legacy store");
        AssertNpcResult(
            fixture.Transport.ReadLegacyPackets().Single(),
            HolyStoneNativeResults.WrongSelectionSubId,
            description);
    }
}
