using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Warehouse;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private sealed record WarehouseTransferAuthoritativeState(
        WarehouseSnapshot Warehouse,
        CharacterLoadSnapshot Character);

    private async Task<WarehouseTransferAuthoritativeState?>
        ReadWarehouseTransferStateAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var warehouse = await _warehouseSnapshots!.ReadAsync(
                subject,
                ownership,
                cancellationToken);
            var account = await _characterSnapshots.ReadAsync(
                subject.AccountId,
                _processRealmId,
                cancellationToken);
            if (!RevalidateCurrentPlayerOwnership(ownership))
            {
                throw new PlayerOwnershipValidationException(
                    PlayerOwnershipValidationStatus.OwnershipLost);
            }

            var character = account.Character;
            if (warehouse is null || character is null)
            {
                return null;
            }
            warehouse.Validate();
            if (warehouse.AccountId != subject.AccountId ||
                warehouse.CharacterId != subject.CharacterId ||
                character.Identity.AccountId != subject.AccountId ||
                character.Identity.CharacterId != subject.CharacterId ||
                character.Identity.RealmId != _processRealmId)
            {
                throw new InvalidDataException(
                    "Warehouse state crossed its character boundary.");
            }

            if (warehouse.InventoryRevision ==
                character.Loadout.InventoryRevision)
            {
                return new(warehouse, character);
            }
        }

        return null;
    }

    private static bool TryCreateWarehouseTransferCommand(
        WarehouseOperationIdentity identity,
        int realmId,
        in WarehouseTransferIntent intent,
        WarehouseTransferAuthoritativeState state,
        out WarehouseTransferCommand command,
        out WarehouseTransferResultStatus rejection)
    {
        command = default;
        rejection = WarehouseTransferResultStatus.ConcurrentConflict;
        var warehouse = state.Warehouse;
        var character = state.Character;

        if (intent.WarehouseSlot >= warehouse.Capacity ||
            intent.DestinationWarehouseSlot >= warehouse.Capacity)
        {
            rejection = WarehouseTransferResultStatus.CapacityExceeded;
            return false;
        }

        var source = intent.Operation switch
        {
            WarehouseTransferOperation.Deposit =>
                KitBagSlots.GetItem(
                    character.Loadout.KitBag,
                    intent.KitBagSlot).ToCompactString(),
            _ => WarehouseItemState(
                warehouse,
                intent.WarehouseSlot)
        };
        if (source == "[]")
        {
            rejection = WarehouseTransferResultStatus.EmptySource;
            return false;
        }

        var destination = intent.Operation switch
        {
            WarehouseTransferOperation.Deposit
                when intent.WarehouseSlot >= 0 =>
                WarehouseItemState(warehouse, intent.WarehouseSlot),
            WarehouseTransferOperation.Withdraw
                when intent.KitBagSlot >= 0 =>
                KitBagSlots.GetItem(
                    character.Loadout.KitBag,
                    intent.KitBagSlot).ToCompactString(),
            WarehouseTransferOperation.InternalMove =>
                WarehouseItemState(
                    warehouse,
                    intent.DestinationWarehouseSlot),
            _ => "[]"
        };

        return WarehouseTransferCommandEnvelope.TryCreateCommand(
            identity,
            realmId,
            intent.Operation,
            intent.WarehouseSlot,
            intent.KitBagSlot,
            intent.DestinationWarehouseSlot,
            intent.Money,
            intent.StorageType,
            warehouse.WarehouseRevision,
            warehouse.InventoryRevision,
            source,
            destination,
            out command);
    }

    private static string WarehouseItemState(
        WarehouseSnapshot snapshot,
        int slot) =>
        snapshot.Items.FirstOrDefault(item => item.Slot == slot)
            ?.CompactItemState ?? "[]";
}
