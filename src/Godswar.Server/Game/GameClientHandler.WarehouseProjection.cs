using Godswar.Server.Application.Characters;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task ReloadWarehouseKitBagProjectionAsync(
        PlayerOwnershipFence ownership,
        long minimumInventoryRevision,
        CancellationToken cancellationToken)
    {
        var accountSnapshot = await _characterSnapshots.ReadAsync(
            _account!.Id,
            _processRealmId,
            cancellationToken);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            throw new InvalidOperationException(
                "The warehouse owner changed during projection reload.");
        }

        var persisted = accountSnapshot.Character;
        if (persisted is null)
        {
            throw new InvalidDataException(
                "The authoritative warehouse inventory projection is stale.");
        }

        ApplyWarehouseKitBagProjection(
            persisted,
            minimumInventoryRevision);
    }

    private void ApplyWarehouseKitBagProjection(
        CharacterLoadSnapshot persisted,
        long minimumInventoryRevision)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        if (_account is null ||
            _character is null ||
            persisted.Identity.CharacterId != _character.Id ||
            persisted.Identity.AccountId != _account.Id ||
            persisted.Identity.RealmId != _processRealmId ||
            persisted.Loadout.InventoryRevision < minimumInventoryRevision)
        {
            throw new InvalidDataException(
                "The authoritative warehouse inventory projection is stale.");
        }

        _character.KitBag = persisted.Loadout.KitBag;
        _registry.UpdateCharacter(
            _session,
            _character,
            advanceWorldRevision: false);
        _pendingUnequipFollowup = null;
        ClearForgeSelection();
    }
}
