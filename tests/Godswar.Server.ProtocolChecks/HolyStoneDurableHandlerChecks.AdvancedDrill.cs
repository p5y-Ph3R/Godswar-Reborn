using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private static async Task CheckAdvancedDrillRoutingAsync()
    {
        await AssertSecureAdvancedDrillPageAsync();
        await AssertSecureAdvancedDrillRejectedAsync(
            operationId: null,
            args =>
            {
                args[HolyStoneProtocol.AdvancedDrillScratchArgumentIndex] =
                    0;
                args[HolyStoneProtocol.TargetArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(WeaponSlot);
                args[HolyStoneProtocol.StoneArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(StoneSlot);
            },
            "secure Advanced Drill without UUID");
        await AssertSecureAdvancedDrillRejectedAsync(
            OperationId,
            static args => args[7] = 100,
            "malformed UUID-bearing Advanced Drill");
        await AssertSecureAdvancedDrillAcceptedAsync();

        await AssertRawAdvancedDrillPageAsync();
        await AssertRawAdvancedDrillRejectedAsync(
            static args => args[10] = 1,
            "raw Advanced Drill unknown value shape");
    }

    private static async Task AssertSecureAdvancedDrillAcceptedAsync()
    {
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.ReplayNotFound(),
            HolyStoneExecutionResult.InvalidIntent(),
            expectedOperation: HolyStoneCommandOperation.AdvancedDrill);
        var packet = HolyStoneCommandContractChecks.CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.AdvancedDrillSubId,
            args =>
            {
                args[HolyStoneProtocol.AdvancedDrillScratchArgumentIndex] =
                    0;
                args[HolyStoneProtocol.TargetArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(WeaponSlot);
                args[HolyStoneProtocol.StoneArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(StoneSlot);
            },
            OperationId);

        await InvokeAsync(fixture.Handler, packet);

        Check.Equal(
            1,
            fixture.Executor!.ReplayCount,
            "secure Advanced Drill checks the durable inbox");
        Check.Equal(
            1,
            fixture.Executor.ExecuteCount,
            "new secure Advanced Drill executes once");
        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            "secure Advanced Drill cannot reach the legacy store");
        var command = fixture.Executor.ExecutedCommand ??
            throw new InvalidOperationException(
                "Advanced Drill executor did not capture its command.");
        Check.Equal(
            (int)HolyStoneCommandOperation.AdvancedDrill,
            (int)command.Operation,
            "Advanced Drill command operation");
        Check.Equal(
            WeaponSlot,
            command.TargetSlot,
            "Advanced Drill command target slot");
        Check.Equal(
            StoneSlot,
            command.StoneKitBagSlot,
            "Advanced Drill command material slot");
        Check.Equal(
            WeaponBefore.ToCompactString(),
            command.ExpectedTargetCompactItemState,
            "Advanced Drill captures target state");
        Check.Equal(
            StoneBefore.ToCompactString(),
            command.ExpectedStoneCompactItemState,
            "Advanced Drill captures material state");

        var result = fixture.Transport.CommandResults.Single();
        Check.Equal(
            (ushort)CommandFamily.HolyStoneAdvancedDrill,
            result.CommandFamily,
            "Advanced Drill result family");
        Check.Equal(
            OperationId,
            result.OperationId,
            "Advanced Drill result operation UUID");
        Check.Equal(
            (int)SecureLegacyCommandDisposition.Rejected,
            (int)result.Disposition,
            "invalid Advanced Drill test execution is rejected");
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
        if (operationId.HasValue)
        {
            var result = fixture.Transport.CommandResults.Single();
            Check.Equal(
                (ushort)CommandFamily.HolyStoneAdvancedDrill,
                result.CommandFamily,
                $"{description} result family");
            Check.Equal(
                operationId.Value,
                result.OperationId,
                $"{description} result operation UUID");
            Check.Equal(
                (int)SecureLegacyCommandDisposition.Rejected,
                (int)result.Disposition,
                $"{description} result disposition");
        }
        else
        {
            Check.Equal(
                0,
                fixture.Transport.CommandResults.Count,
                $"{description} without UUID has no command result");
        }
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
