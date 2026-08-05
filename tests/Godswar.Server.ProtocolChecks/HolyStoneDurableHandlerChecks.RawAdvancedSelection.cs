using System.Buffers.Binary;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Game;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class HolyStoneDurableHandlerChecks
{
    private const int StockAdvancedTargetSlot = 50;
    private const int StockAdvancedSpellSlot = 27;

    private static async Task
        CheckStockRawAdvancedDrillSelectionRoutingAsync()
    {
        var target = CreateAdvancedDrillTarget(socketCount: 2);
        var spell = CreateSocketSpell(
            HolyStoneDrillEligibilityPolicy.SocketSpellThreeItemId,
            stack: 99);
        var bag = KitBagSlots.SetSlot(
            GameDefaults.EmptyKitBag,
            StockAdvancedTargetSlot,
            target.ToCompactString());
        bag = KitBagSlots.SetSlot(
            bag,
            StockAdvancedSpellSlot,
            spell.ToCompactString());
        await using var fixture = await CreateRawFixtureAsync(
            initialKitBag: bag,
            storeMutation: character => character);

        await InvokeAsync(
            fixture.Handler,
            HolyStoneCommandContractChecks.CreatePacket(
                HolyStoneProtocol.SpartaNpcId,
                HolyStoneProtocol.AdvancedDrillSubId,
                static _ => { }));
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(
                StockAdvancedTargetSlot,
                selected: true));
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(
                StockAdvancedSpellSlot,
                selected: true));

        // The shipped controls clear both visual fields immediately before
        // emitting action 701. Preserve that exact, bounded clear burst as
        // the one-shot confirmation selection.
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(
                StockAdvancedTargetSlot,
                selected: false));
        await InvokeAsync(
            fixture.Handler,
            CreateRawItemSelectionPacket(
                StockAdvancedSpellSlot,
                selected: false));
        await InvokeAsync(
            fixture.Handler,
            CreateStockRawAdvancedDrillPacket(
                StockAdvancedTargetSlot,
                StockAdvancedSpellSlot));

        var call = fixture.Store.LastCall ??
            throw new InvalidOperationException(
                "stock raw Advanced Drill did not reach the store");
        Check.Equal(
            StockAdvancedTargetSlot,
            call.TargetSlot,
            "stock action 701 resolves the selected weapon's full bag page");
        Check.Equal(
            StockAdvancedSpellSlot,
            call.StoneSlot,
            "stock action 701 resolves the selected Spell III's full bag page");
    }

    private static async Task
        CheckRawHolyStonePersistenceFailureResponseAsync()
    {
        var target = CreateAdvancedDrillTarget(socketCount: 2);
        var spell = CreateSocketSpell(
            HolyStoneDrillEligibilityPolicy.SocketSpellThreeItemId,
            stack: 99);
        await using var fixture = await CreateRawFixtureAsync(
            initialKitBag: CreateAdvancedDrillBag(
                target,
                AdvancedSpellSlot,
                spell),
            storeMutation: _ => throw new InvalidOperationException(
                "injected stacked-material persistence failure"));

        await InvokeAsync(
            fixture.Handler,
            CreateRawAdvancedDrillPacket(AdvancedSpellSlot));

        Check.Equal(
            1,
            fixture.Store.HolyStoneCount,
            "raw persistence failure reaches the store once");
        AssertNpcResult(
            fixture.Transport.ReadLegacyPackets().Single(),
            HolyStoneNativeResults.WrongSelectionSubId,
            "raw persistence failure returns a native error without disconnecting");
    }

    private static GamePacket CreateStockRawAdvancedDrillPacket(
        int targetSlot,
        int spellSlot) =>
        HolyStoneCommandContractChecks.CreatePacket(
            HolyStoneProtocol.SpartaNpcId,
            HolyStoneProtocol.AdvancedDrillSubId,
            args =>
            {
                args[HolyStoneProtocol
                    .AdvancedDrillScratchArgumentIndex] = 0;
                args[HolyStoneProtocol.TargetArgumentIndex] =
                    targetSlot %
                    GearEnhancerItemSelectionPacket.SlotsPerPage;
                args[HolyStoneProtocol.StoneArgumentIndex] =
                    spellSlot %
                    GearEnhancerItemSelectionPacket.SlotsPerPage;
            });

    private static GamePacket CreateRawItemSelectionPacket(
        int kitBagSlot,
        bool selected)
    {
        var bytes = new byte[
            sizeof(ushort) +
            sizeof(ushort) +
            GearEnhancerItemSelectionPacket.PayloadLength];
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes,
            checked((ushort)bytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(sizeof(ushort)),
            Opcodes.GearEnhancerItemSelection);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(sizeof(uint)),
            kitBagSlot /
                GearEnhancerItemSelectionPacket.SlotsPerPage);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(sizeof(uint) + sizeof(int)),
            kitBagSlot %
                GearEnhancerItemSelectionPacket.SlotsPerPage);
        bytes[sizeof(uint) + (sizeof(int) * 2)] =
            selected ? (byte)1 : (byte)0;
        return new GamePacket(bytes);
    }
}
