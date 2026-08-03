using Godswar.Server.Application.Items;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GearEnhancerSelectionContext
{
    private int ResolveNativeSelectionSlot(
        GearEnhancerItemSelectionPacket selection,
        string kitBag,
        IItemMaterialCatalog? materials)
    {
        var declaredSlot = selection.KitBagSlot;
        if (!selection.Selected)
        {
            return ResolveRemovalPageAlias(selection, declaredSlot);
        }

        if (selection.BagPage != 0 ||
            materials is null ||
            !_gearSelection.HasValue ||
            !_catalystSelection.HasValue ||
            _attributeStoneSelection.HasValue)
        {
            return declaredSlot;
        }

        var declaredItem = KitBagSlots.GetItem(kitBag, declaredSlot);
        if (materials.TryGetAttributeStone(declaredItem.Id, out _))
        {
            return declaredSlot;
        }

        // The stock physical Gear Mentor can report page zero for an item
        // dragged from a later bag page. Only the third control has a strong
        // server-owned type discriminator, so recover that alias solely when
        // exactly one same-cell candidate is an Attribute Stone. Ambiguous
        // coordinates retain the declared slot and fail closed in the
        // authoritative planner.
        var candidates = Enumerable.Range(
                0,
                GearEnhancerItemSelectionPacket.PageCount)
            .Select(page => checked(
                (page * GearEnhancerItemSelectionPacket.SlotsPerPage) +
                selection.PageSlot))
            .Where(slot =>
            {
                var item = KitBagSlots.GetItem(kitBag, slot);
                return !item.IsEmpty &&
                    materials.TryGetAttributeStone(item.Id, out _);
            })
            .Take(2)
            .ToArray();
        return candidates.Length == 1
            ? candidates[0]
            : declaredSlot;
    }

    private int ResolveRemovalPageAlias(
        GearEnhancerItemSelectionPacket selection,
        int declaredSlot)
    {
        if (selection.BagPage != 0 ||
            IsStagedSlot(declaredSlot))
        {
            return declaredSlot;
        }

        var candidates = CurrentSelections()
            .Where(candidate =>
                candidate.KitBagSlot %
                    GearEnhancerItemSelectionPacket.SlotsPerPage ==
                selection.PageSlot)
            .Select(static candidate => candidate.KitBagSlot)
            .Take(2)
            .ToArray();
        return candidates.Length == 1
            ? candidates[0]
            : declaredSlot;
    }

    private bool IsStagedSlot(int slot) =>
        _gearSelection?.KitBagSlot == slot ||
        _catalystSelection?.KitBagSlot == slot ||
        _attributeStoneSelection?.KitBagSlot == slot;
}
