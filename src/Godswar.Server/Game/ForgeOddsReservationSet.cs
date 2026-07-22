using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed class ForgeOddsReservationSet
{
    private readonly Dictionary<int, ForgeSlotSelection> _reservations = [];
    private readonly Dictionary<int, uint> _validatedDescriptors = [];
    private uint? _itemId;

    public int TotalQuantity => _reservations.Values.Sum(selection => selection.Quantity);

    public bool IsFullyLinked =>
        _reservations.Count > 0 &&
        _reservations.All(pair =>
            _validatedDescriptors.TryGetValue(pair.Key, out var itemId) &&
            itemId == pair.Value.ExpectedItem.Id);

    public IReadOnlyList<ForgeSlotSelection> CaptureSelections()
    {
        return _reservations.Values
            .OrderBy(selection => selection.KitBagSlot)
            .ToArray();
    }

    public void ValidateDescriptor(int kitBagSlot, CompactItemEntry item)
    {
        _validatedDescriptors[kitBagSlot] = item.Id;
    }

    public bool TryIncrement(int kitBagSlot, CompactItemEntry item)
    {
        if (item.IsEmpty)
        {
            return false;
        }

        if (_itemId != item.Id)
        {
            _reservations.Clear();
            _itemId = item.Id;
        }

        if (TotalQuantity >= EquipmentForgeCalculator.MaximumOddsQuantity)
        {
            return false;
        }

        if (_reservations.TryGetValue(kitBagSlot, out var current))
        {
            if (current.ExpectedItem != item || current.Quantity >= item.Stack)
            {
                return false;
            }

            _reservations[kitBagSlot] = current with { Quantity = current.Quantity + 1 };
            return true;
        }

        if (item.Stack < 1)
        {
            return false;
        }

        _reservations[kitBagSlot] = new ForgeSlotSelection(kitBagSlot, item, 1);
        return true;
    }

    public void Clear()
    {
        _reservations.Clear();
        _validatedDescriptors.Clear();
        _itemId = null;
    }
}
