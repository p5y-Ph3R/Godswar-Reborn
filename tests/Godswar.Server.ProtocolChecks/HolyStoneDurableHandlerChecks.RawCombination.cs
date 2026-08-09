using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private static async Task CheckRawCombinationBridgeAsync()
    {
        await CheckRawCombinationRequiresFourClearedSelectionsAsync();
        await CheckRawCombinationExecutesOneDurableCommandAsync();
    }

    private static async Task
        CheckRawCombinationRequiresFourClearedSelectionsAsync()
    {
        await using var fixture = await CreateRawCombinationFixtureAsync();
        await OpenRawCombinationAsync(fixture);
        await StageCombinationSelectionsAsync(
            fixture,
            clearSelections: false);

        await InvokeAsync(fixture.Handler, CreateRawCombinationFinal());

        Check.Equal(
            0,
            fixture.Executor!.ExecuteCount,
            "initial Combination cannot execute before its four-control " +
            "clear burst");
        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            "rejected Combination never reaches legacy persistence");
    }

    private static async Task
        CheckRawCombinationExecutesOneDurableCommandAsync()
    {
        await using var fixture = await CreateRawCombinationFixtureAsync();
        await OpenRawCombinationAsync(fixture);
        await StageCombinationSelectionsAsync(
            fixture,
            clearSelections: true);
        var action = CreateRawCombinationFinal();

        await InvokeAsync(fixture.Handler, action);

        Check.Equal(
            1,
            fixture.Executor!.ExecuteCount,
            "raw Combination reaches durable execution exactly once");
        Check.Equal(
            0,
            fixture.Executor.ReplayCount,
            "raw Combination does not forge a secure-client replay");
        Check.Equal(
            0,
            fixture.Store.HolyStoneCount,
            "raw Combination never reaches retry-ambiguous legacy storage");

        var envelope = fixture.Executor.ExecutedEnvelope ??
            throw new InvalidOperationException(
                "raw Combination executor did not capture its envelope");
        Check.Equal(
            (int)HolyStoneCommandOperation.Combine,
            (int)envelope.Command.Operation,
            "raw Combination uses the dedicated durable operation");
        Check.Equal(WeaponSlot, envelope.Command.TargetSlot,
            "ItemBtn1 is the major stone");
        Check.Equal(StoneSlot, envelope.Command.StoneKitBagSlot,
            "ItemBtn2 is the first consumed stone");
        Check.Equal(8, envelope.Command.CatalystKitBagSlot,
            "ItemBtn3 is the second consumed stone");
        Check.Equal(9, envelope.Command.ThirdMaterialKitBagSlot,
            "ItemBtn4 is the third consumed stone");
        Check.Equal(
            (int)CommandIdentityStrength.ServerOperationId,
            (int)envelope.IdentityStrength,
            "raw Combination records a connection-scoped server identity");
        Check.Equal(
            (int)CommandEnvelopeValidation.Valid,
            (int)HolyStoneCommandEnvelope.Validate(envelope),
            "raw Combination executor receives a valid command envelope");

        await InvokeAsync(fixture.Handler, action);
        Check.Equal(
            1,
            fixture.Executor.ExecuteCount,
            "duplicate final action cannot reuse the four selected rows");
    }

    private static Task<RawHolyStoneFixture>
        CreateRawCombinationFixtureAsync()
    {
        var bag = GameDefaults.EmptyKitBag;
        foreach (var (slot, itemId) in new (int Slot, uint ItemId)[]
                 {
                     (WeaponSlot, 9030),
                     (StoneSlot, 9030),
                     (8, 9030),
                     (9, 9030)
                 })
        {
            var item = CompactItemEntry.Empty with
            {
                Id = itemId,
                Quality = 1,
                Grade = 4,
                Bound = 1,
                Stack = 1
            };
            bag = KitBagSlots.SetSlot(
                bag,
                slot,
                item.ToCompactString());
        }

        return CreateRawFixtureAsync(
            initialKitBag: bag,
            durableExecutionResult:
                HolyStoneExecutionResult.InvalidIntent(),
            durableOperation: HolyStoneCommandOperation.Combine,
            requiresDurablePlayerCommands: true,
            hasLocalLegacyAuthenticationAccess: true);
    }

    private static async Task OpenRawCombinationAsync(
        RawHolyStoneFixture fixture) =>
        await InvokeAsync(
            fixture.Handler,
            HolyStoneCommandContractChecks.CreatePacket(
                HolyStoneProtocol.SpartaNpcId,
                HolyStoneProtocol.CombineSubId,
                static _ => { }));

    private static async Task StageCombinationSelectionsAsync(
        RawHolyStoneFixture fixture,
        bool clearSelections)
    {
        var slots = new[] { WeaponSlot, StoneSlot, 8, 9 };
        foreach (var slot in slots)
        {
            await InvokeAsync(
                fixture.Handler,
                CreateRawItemSelectionPacket(slot, selected: true));
        }
        if (!clearSelections)
        {
            return;
        }
        foreach (var slot in slots)
        {
            await InvokeAsync(
                fixture.Handler,
                CreateRawItemSelectionPacket(slot, selected: false));
        }
    }

    private static GamePacket CreateRawCombinationFinal() =>
        HolyStoneCommandContractChecks.CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.CombineSubId,
            args =>
            {
                args[HolyStoneProtocol.TargetArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(WeaponSlot);
                args[HolyStoneProtocol.StoneArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(StoneSlot);
                args[HolyStoneProtocol.CombineSecondMaterialArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(8);
                args[HolyStoneProtocol.CombineThirdMaterialArgumentIndex] =
                    HolyStoneProtocol.EncodeKitBagReference(9);
            });
}
