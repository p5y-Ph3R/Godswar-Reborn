using Godswar.Server.Application.Inventory;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleLegacyHolyStoneAsync(
        uint npcId,
        int dialogIndex,
        int subId,
        IReadOnlyList<int> args,
        HolyStoneWireIntent? exactIntent,
        CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"[holy-stone] action npc={npcId} dialog={dialogIndex} " +
            $"subId={subId} args={string.Join(',', args)}");

        if (HolyStoneProtocol.IsMountNavigation(subId, args))
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    HolyStoneProtocol.MountAliasOneSubId,
                    HolyStoneProtocol.MountAliasTwoSubId,
                    HolyStoneProtocol.MountAliasThreeSubId,
                    HolyStoneProtocol.MountAliasFourSubId),
                cancellationToken,
                "NpcFunctionActionResponse");
            return;
        }

        if (exactIntent is not { } intent)
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    HolyStoneNativeResults.WrongSelectionSubId),
                cancellationToken,
                "NpcFunctionActionResponse");
            return;
        }

        var operation = MapOperation(intent.Operation);
        var targetMode =
            intent.TargetLocation switch
            {
                HolyStoneTargetLocation.Equipment =>
                    HolyStoneTargetMode.EquippedWeapon,
                HolyStoneTargetLocation.KitBag =>
                    HolyStoneTargetMode.KitBag,
                _ => throw new InvalidDataException(
                    "Unknown Holy Stone target location.")
            };
        var targetSlot = intent.TargetSlot;
        var stoneSlot = intent.StoneKitBagSlot;
        var destinationSlot = stoneSlot >= 0 ? stoneSlot : -1;
        var socketIndex = intent.SocketIndex;
        if (!AllowLegacyPlayerMutationFallback(
                "holy_stone"))
        {
            return;
        }

        var kitBagBeforeMutation = _character!.KitBag;
        var updatedCharacter = await _store.ApplyWeaponHolyStoneAsync(
            _account!.Id,
            _character!.Id,
            operation,
            targetMode,
            targetSlot,
            socketIndex,
            stoneSlot,
            destinationSlot,
            cancellationToken);

        var responseSubId = updatedCharacter is null
            ? HolyStoneInsufficientFunds
            : operation switch
            {
                HolyStoneOperation.MountStone =>
                    HolyStoneMountSuccess,
                HolyStoneOperation.RemoveStone =>
                    HolyStoneRemoveSuccess,
                HolyStoneOperation.DrillSocket =>
                    HolyStoneDrillSuccess,
                _ => HolyStoneInsufficientFunds
            };

        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                responseSubId),
            cancellationToken,
            "NpcFunctionActionResponse");

        if (updatedCharacter is null)
        {
            return;
        }

        foreach (var acknowledgement in
            PacketBuilder.KitBagMutationDeletionAcknowledgements(
                kitBagBeforeMutation,
                updatedCharacter.KitBag))
        {
            await _session.SendAsync(
                acknowledgement,
                cancellationToken,
                "HolyStoneKitBagDeleteAck");
        }

        InstallUpdatedCharacter(updatedCharacter);
        await RefreshActiveCharacterStatsAsync(
            $"holy-stone-{operation}",
            cancellationToken);
        _registry.UpdateCharacter(_session, _character);

        await _session.SendAsync(
            BuildLocalPlayerStatusUpdate(),
            cancellationToken,
            "PlayerStatusUpdate");
        await _session.SendAsync(
            PacketBuilder.EquipmentItemSnapshot(
                _character,
                EquipmentSlots.Weapon),
            cancellationToken,
            "EquipmentItemSnapshot");
        foreach (var detailPage in
            PacketBuilder.KitBagDetailPages(_character))
        {
            await _session.SendAsync(
                detailPage,
                cancellationToken,
                "KitBagDetail");
        }

        await _session.SendAsync(
            PacketBuilder.EquipmentVisualRefresh(_character),
            cancellationToken,
            "EquipmentVisualRefresh");
        await _session.SendAsync(
            PacketBuilder.PlayerDetailRefreshAck(),
            cancellationToken,
            "PlayerDetailRefreshAck");
        await BroadcastEquipmentRefreshAsync(
            $"holy-stone-{operation}",
            cancellationToken);
    }

    private static HolyStoneOperation MapOperation(
        HolyStoneCommandOperation operation) =>
        operation switch
        {
            HolyStoneCommandOperation.Mount =>
                HolyStoneOperation.MountStone,
            HolyStoneCommandOperation.Remove =>
                HolyStoneOperation.RemoveStone,
            HolyStoneCommandOperation.Drill =>
                HolyStoneOperation.DrillSocket,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
}
