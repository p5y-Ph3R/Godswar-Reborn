using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Warehouse;

internal sealed record WarehouseItemSnapshot(
    int Slot,
    string CompactItemState);

internal sealed record WarehouseSnapshot(
    int AccountId,
    int CharacterId,
    int Capacity,
    long WarehouseRevision,
    long InventoryRevision,
    IReadOnlyList<WarehouseItemSnapshot> Items)
{
    public void Validate()
    {
        if (AccountId <= 0 ||
            CharacterId <= 0 ||
            !WarehouseCapacityPolicy.IsValidCapacity(Capacity) ||
            WarehouseRevision < 0 ||
            InventoryRevision < 0 ||
            Items is null ||
            Items.Count > Capacity ||
            Items.Select(static item => item.Slot).Distinct().Count() !=
                Items.Count)
        {
            throw new InvalidDataException(
                "The warehouse snapshot is outside its bounded contract.");
        }

        foreach (var item in Items)
        {
            if (item is null ||
                !WarehouseCapacityPolicy.IsOpenWarehouseSlot(
                    item.Slot,
                    Capacity) ||
                string.IsNullOrWhiteSpace(item.CompactItemState) ||
                item.CompactItemState == "[]" ||
                item.CompactItemState.Length > 512 ||
                item.CompactItemState.Any(char.IsControl) ||
                item.CompactItemState[0] != '[' ||
                item.CompactItemState[^1] != ']')
            {
                throw new InvalidDataException(
                    "A warehouse item snapshot is invalid.");
            }
        }
    }
}

internal interface IWarehouseSnapshotReader
{
    Task<WarehouseSnapshot?> ReadAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken = default);
}
