using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Networking.Secure;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private static async Task CheckMountGearDrillRoutingAsync()
    {
        await CheckSecureMountGearDrillRoutingAsync();
        await CheckRawMountGearDrillRoutingAsync();
    }

    private static async Task CheckSecureMountGearDrillRoutingAsync()
    {
        await using var fixture = await CreateFixtureAsync(
            HolyStoneExecutionResult.ReplayNotFound(),
            HolyStoneExecutionResult.InvalidIntent(),
            expectedOperation:
                HolyStoneCommandOperation.MountGearDrill);
        await InvokeAsync(
            fixture.Handler,
            CreateMountGearDrillPacket(OperationId));

        Check.Equal(
            1,
            fixture.Executor!.ReplayCount,
            "secure Mount Gear Drill checks the durable inbox");
        Check.Equal(
            1,
            fixture.Executor.ExecuteCount,
            "secure Mount Gear Drill executes exactly once");
        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            "secure Mount Gear Drill cannot reach legacy storage");
        Check.Equal(
            (int)HolyStoneCommandOperation.MountGearDrill,
            (int)fixture.Executor.ExecutedCommand!.Value.Operation,
            "secure Mount Gear Drill preserves operation semantics");
        var result = fixture.Transport.CommandResults.Single();
        Check.Equal(
            (ushort)CommandFamily.MountGearDrill,
            result.CommandFamily,
            "secure Mount Gear Drill returns family 45");
        Check.Equal(
            OperationId,
            result.OperationId,
            "secure Mount Gear Drill returns its operation UUID");
        Check.Equal(
            (int)SecureLegacyCommandDisposition.Rejected,
            (int)result.Disposition,
            "fixture rejection remains a terminal secure result");
    }

    private static async Task CheckRawMountGearDrillRoutingAsync()
    {
        var mountGear = WeaponBefore with
        {
            Id = 14500,
            SocketCount = 0
        };
        var bag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            WeaponSlot,
            mountGear.ToCompactString());
        await using var fixture = await CreateRawFixtureAsync(
            initialKitBag: bag,
            durableExecutionResult:
                HolyStoneExecutionResult.InvalidIntent(),
            durableOperation:
                HolyStoneCommandOperation.MountGearDrill,
            requiresDurablePlayerCommands: true,
            hasLocalLegacyAuthenticationAccess: true);

        await InvokeAsync(
            fixture.Handler,
            CreateMountGearDrillPacket(operationId: null));

        Check.Equal(
            1,
            fixture.Executor!.ExecuteCount,
            "raw Mount Gear Drill reaches the durable boundary once");
        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            "raw Mount Gear Drill cannot downgrade to legacy mutation");
        Check.Equal(
            (int)HolyStoneCommandOperation.MountGearDrill,
            (int)fixture.Executor.ExecutedCommand!.Value.Operation,
            "raw action 801 preserves Mount Gear Drill semantics");
    }

    private static Godswar.Server.Protocol.GamePacket
        CreateMountGearDrillPacket(Guid? operationId) =>
        HolyStoneCommandContractChecks.CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.MountGearDrillSubId,
            args => args[HolyStoneProtocol.TargetArgumentIndex] =
                HolyStoneProtocol.EncodeKitBagReference(WeaponSlot),
            operationId);
}
