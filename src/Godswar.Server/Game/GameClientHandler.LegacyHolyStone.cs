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

        if (intent.Operation is
                HolyStoneCommandOperation.Mount or
                HolyStoneCommandOperation.Remove or
                HolyStoneCommandOperation.Upgrade or
                HolyStoneCommandOperation.Combine or
                HolyStoneCommandOperation.ImplementSpirit or
                HolyStoneCommandOperation.MountGearDrill)
        {
            await HandleRawDurableHolyStoneAsync(
                npcId,
                dialogIndex,
                intent,
                cancellationToken);
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

        var preflightRejection =
            ResolveLegacyDrillPreflightRejection(
                operation,
                targetMode,
                targetSlot,
                stoneSlot);
        if (preflightRejection.HasValue)
        {
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    preflightRejection.Value),
                cancellationToken,
                "NpcFunctionActionResponse");
            return;
        }

        var kitBagBeforeMutation = _character!.KitBag;
        LegacyPersistenceMetrics.Record(
            LegacyPersistenceOperation.ApplyWeaponHolyStone);
        GameCharacter? updatedCharacter;
        try
        {
            updatedCharacter = await _store.ApplyWeaponHolyStoneAsync(
                _account!.Id,
                _character!.Id,
                operation,
                targetMode,
                targetSlot,
                socketIndex,
                stoneSlot,
                destinationSlot,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Console.WriteLine(
                "[holy-stone] legacy persistence failed " +
                $"character={_character.Name} operation={operation} " +
                $"error={exception.GetType().Name}");
            await _session.SendAsync(
                PacketBuilder.NpcFunctionActionResponse(
                    npcId,
                    dialogIndex,
                    HolyStoneNativeResults.WrongSelectionSubId),
                cancellationToken,
                "NpcFunctionActionResponse");
            return;
        }

        var responseSubId = updatedCharacter is null
            ? HolyStoneNativeResults.WrongSelectionSubId
            : operation switch
            {
                HolyStoneOperation.MountStone =>
                    HolyStoneMountSuccess,
                HolyStoneOperation.RemoveStone =>
                    HolyStoneRemoveSuccess,
                HolyStoneOperation.DrillSocket =>
                    HolyStoneDrillSuccess,
                HolyStoneOperation.AdvancedDrillSocket =>
                    HolyStoneDrillSuccess,
                _ => HolyStoneNativeResults.WrongSelectionSubId
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
            PacketBuilder.EquipmentVisualRefresh(
                _character,
                _itemContent?.FashionAppearances),
            cancellationToken,
            "EquipmentVisualRefresh");
        await _session.SendAsync(
            PacketBuilder.EquipmentEffectVisibility(
                LocalPlayerObjectId,
                ResolveEquipmentEffectProjection(_character)),
            cancellationToken,
            "EquipmentEffectVisibility");
        await BroadcastEquipmentRefreshAsync(
            $"holy-stone-{operation}",
            cancellationToken);
    }

    private static HolyStoneOperation MapOperation(
        HolyStoneCommandOperation operation) =>
        operation switch
        {
            HolyStoneCommandOperation.Remove =>
                HolyStoneOperation.RemoveStone,
            HolyStoneCommandOperation.Drill =>
                HolyStoneOperation.DrillSocket,
            HolyStoneCommandOperation.AdvancedDrill =>
                HolyStoneOperation.AdvancedDrillSocket,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private int? ResolveLegacyDrillPreflightRejection(
        HolyStoneOperation operation,
        HolyStoneTargetMode targetMode,
        int targetSlot,
        int stoneSlot)
    {
        if (operation is not (
                HolyStoneOperation.DrillSocket or
                HolyStoneOperation.AdvancedDrillSocket))
        {
            return null;
        }

        if (!HolyStoneItemMutator.TryEvaluateDrill(
                RequireItemContent().Templates,
                _character!.Equipment,
                _character.KitBag,
                _character.Profession,
                operation,
                targetMode,
                targetSlot,
                stoneSlot,
                out var eligibility,
                out var goldCost))
        {
            return HolyStoneNativeResults.TargetNotEquipmentSubId;
        }

        if (eligibility == HolyStoneDrillEligibilityFailure.None)
        {
            return operation == HolyStoneOperation.DrillSocket &&
                _character.Gold < goldCost
                    ? HolyStoneNativeResults.InsufficientFundsSubId
                    : null;
        }

        if (operation == HolyStoneOperation.AdvancedDrillSocket)
        {
            return eligibility switch
            {
                HolyStoneDrillEligibilityFailure.SocketSpell =>
                    HolyStoneNativeResults.AdvancedSpellRequiredSubId,
                HolyStoneDrillEligibilityFailure.MaximumSockets =>
                    HolyStoneNativeResults.AdvancedMaximumSocketsSubId,
                _ => HolyStoneNativeResults.DrillPrerequisiteSubId
            };
        }

        return eligibility ==
            HolyStoneDrillEligibilityFailure.MaximumSockets
                ? HolyStoneNativeResults.MaximumSocketsSubId
                : HolyStoneNativeResults.DrillPrerequisiteSubId;
    }
}
