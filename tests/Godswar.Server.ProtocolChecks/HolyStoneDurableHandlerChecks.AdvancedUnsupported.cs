using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private static async Task CheckAdvancedDrillStaysFailClosedAsync()
    {
        await AssertSecureAdvancedDrillRejectedAsync(
            operationId: null,
            static _ => { },
            "secure Advanced Drill page transition");
        await AssertSecureAdvancedDrillRejectedAsync(
            operationId: null,
            static args => args[6] = 205,
            "secure Advanced Drill unknown value shape");
        await AssertSecureAdvancedDrillRejectedAsync(
            OperationId,
            static args => args[7] = 100,
            "UUID-bearing Advanced Drill unknown value shape");

        await AssertRawAdvancedDrillRejectedAsync(
            static _ => { },
            "raw Advanced Drill page transition");
        await AssertRawAdvancedDrillRejectedAsync(
            static args => args[10] = 1,
            "raw Advanced Drill unknown value shape");
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
