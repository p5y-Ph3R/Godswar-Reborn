using System.Buffers.Binary;
using System.Text;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.Packets;

internal static partial class PacketBuilder
{
    public static byte[] EquipmentItemSnapshot(GameCharacter character)
    {
        return EquipmentItemSnapshot(character, LocalPlayerObjectId);
    }

    public static byte[] PlayerInspectEquipment(GameCharacter character, uint objectId)
    {
        var packet = new byte[PlayerInspectEquipmentLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerInspectEquipmentOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);

        // Captured 0x2726 responses contain a compact sequence of non-empty item
        // records. Their original source slots are carried separately in the mask
        // at offset 1520; record index is not the equipment slot.
        var items = EquipmentItemsForInspect(character)
            .Where(entry => !entry.Item.IsEmpty)
            .Take(PlayerInspectEquipmentRecordCount)
            .ToArray();
        uint equipmentMask = 0;
        for (var record = 0; record < PlayerInspectEquipmentRecordCount; record++)
        {
            var entry = record < items.Length ? items[record] : default;
            WriteInspectItemRecord(
                packet.AsSpan(
                    PlayerInspectEquipmentHeaderLength + (record * EnterItemRecordLength),
                    EnterItemRecordLength),
                entry.Item,
                character.Id,
                entry.Slot);

            if (!entry.Item.IsEmpty && entry.Slot is >= 0 and < sizeof(uint) * 8)
            {
                equipmentMask |= 1u << entry.Slot;
            }
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(PlayerInspectEquipmentMaskOffset, PlayerInspectEquipmentMaskLength),
            equipmentMask);

        return packet;
    }

    public static byte[] PlayerInspectEquipmentStatusBundle(GameCharacter character, uint objectId)
    {
        return PlayerInspectEquipmentStatusBundle(
            character,
            objectId,
            ClientStatusAggregate.Empty);
    }

    public static byte[] PlayerInspectEquipmentStatusBundle(
        GameCharacter character,
        uint objectId,
        ClientStatusAggregate aggregate)
    {
        var inspectEquipment = PlayerInspectEquipment(character, objectId);
        var inspectStatus = PlayerStatusUpdate(
            character,
            objectId,
            aggregate);
        var bundle = new byte[inspectEquipment.Length + inspectStatus.Length];
        inspectEquipment.CopyTo(bundle, 0);
        inspectStatus.CopyTo(bundle, inspectEquipment.Length);
        return bundle;
    }

    public static byte[] PlayerInspectProfile(GameCharacter character, uint objectId)
    {
        var packet = new byte[PlayerInspectProfileLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerInspectProfileOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        return packet;
    }

    public static byte[] PlayerInspectComplete()
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), PlayerInspectCompleteOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), 0x00000708);
        return packet;
    }

    public static byte[] EquipmentItemSnapshot(GameCharacter character, uint objectId)
    {
        var items = EquipmentItemsBySlot(character)
            .Where(entry => entry.Item is { IsEmpty: false })
            .ToArray();

        if (items.Length == 0)
        {
            return [];
        }

        using var stream = new MemoryStream(items.Length * EquipmentItemSnapshotLength);
        foreach (var (slot, item) in items)
        {
            var packet = EquipmentItemSnapshot(slot, item, objectId);
            stream.Write(packet);
        }

        return stream.ToArray();
    }

    public static byte[] EquipmentItemSnapshot(GameCharacter character, int slot)
    {
        return EquipmentItemSnapshot(character, slot, LocalPlayerObjectId);
    }

    public static byte[] EquipmentItemSnapshot(GameCharacter character, int slot, uint objectId)
    {
        if (!EquipmentSlots.IsEquipmentSlot(slot))
        {
            return [];
        }

        var item = EquipmentSlots.GetItem(EquipmentFor(character), character.Profession, slot);
        return item.IsEmpty ? [] : EquipmentItemSnapshot(slot, item, objectId);
    }

    public static byte[] EquipmentItemEquipSnapshot(GameCharacter character, int sourceSlot, int equippedSlot)
    {
        if (!EquipmentSlots.IsEquipmentSlot(equippedSlot))
        {
            return [];
        }

        var item = EquipmentSlots.GetItem(EquipmentFor(character), character.Profession, equippedSlot);
        if (item.IsEmpty)
        {
            return [];
        }

        var packet = EquipmentItemSnapshot(sourceSlot, item, LocalPlayerObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), 0);
        var sourcePage = Math.DivRem(Math.Max(sourceSlot, 0), 24, out var sourceIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12, 2), (ushort)sourcePage);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14, 2), (ushort)sourceIndex);
        // The working service uses these two bytes as move-event flags in this
        // response, not as the equipped item's persisted bound/stack values.
        packet[46] = 0;
        packet[47] = 0;
        return packet;
    }

    public static byte[] KitBagItemSnapshot(GameCharacter character, int sourceSlot)
    {
        if (sourceSlot is < 0 or >= KitBagPageCount * KitBagSlotsPerPage)
        {
            return [];
        }

        var item = KitBagSlots.GetItem(
            string.IsNullOrWhiteSpace(character.KitBag) ? GameDefaults.EmptyKitBag : character.KitBag,
            sourceSlot);
        var packet = EquipmentItemSnapshot(sourceSlot, item, LocalPlayerObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), 0);
        var sourcePage = Math.DivRem(sourceSlot, KitBagSlotsPerPage, out var sourceIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12, 2), (ushort)sourcePage);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14, 2), (ushort)sourceIndex);
        return packet;
    }

    public static byte[] EquipmentItemClearSnapshot(int slot)
    {
        return EquipmentItemClearSnapshot(slot, LocalPlayerObjectId);
    }

    public static byte[] EquipmentItemClearSnapshot(int slot, uint objectId)
    {
        if (!EquipmentSlots.IsEquipmentSlot(slot))
        {
            return [];
        }

        return EquipmentItemSnapshot(slot, CompactItemEntry.Empty, objectId);
    }

    public static byte[] EquipmentItemClearSnapshots(uint objectId)
    {
        using var stream = new MemoryStream((EquipmentSlots.Mount + 1) * EquipmentItemSnapshotLength);
        for (var slot = EquipmentSlots.Head; slot <= EquipmentSlots.Mount; slot++)
        {
            stream.Write(EquipmentItemClearSnapshot(slot, objectId));
        }

        return stream.ToArray();
    }

    private static byte[] EquipmentItemSnapshot(int slot, CompactItemEntry item, uint objectId)
    {
        var packet = new byte[EquipmentItemSnapshotLength];
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(0, 2), EquipmentItemSnapshotLength);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2, 2), EquipmentItemSnapshotOpcode);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), objectId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(12, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14, 2), (ushort)slot);
        WriteSnapshotItemRecord(packet.AsSpan(20, EnterItemRecordLength), item);
        return packet;
    }

    private static (int Slot, CompactItemEntry Item)[] EquipmentItemsBySlot(GameCharacter character)
    {
        var equipment = ParseEquipment(character);
        return Enumerable.Range(0, Math.Min(equipment.Length, EquipmentSlots.Mount + 1))
            .Select(slot => (Slot: slot, Item: equipment[slot]))
            .ToArray();
    }

    private static (int Slot, CompactItemEntry Item)[] PlayerWorldEquipmentItems(GameCharacter character)
    {
        var equipment = ParseEquipment(character);
        var populated = Enumerable.Range(0, equipment.Length)
            .Select(slot => (Slot: slot, Item: equipment[slot]))
            .Where(entry =>
                !entry.Item.IsEmpty &&
                !(character.FashionHidden &&
                  entry.Slot == EquipmentSlots.Stylish))
            .ToArray();

        if (populated.Length <= PlayerWorldEquipmentIdsLength)
        {
            return populated;
        }

        var selected = populated[..PlayerWorldEquipmentIdsLength];
        var mountIndex = Array.FindIndex(populated, static entry => entry.Slot == EquipmentSlots.Mount);
        if (mountIndex >= 0 &&
            Array.FindIndex(selected, static entry => entry.Slot == EquipmentSlots.Mount) < 0)
        {
            // The native spawn body has only 18 appearance records while the
            // reconstructed equipment model has 19 usable ordinary/mount
            // slots. Keep the ride-defining mount authoritative in a fully
            // populated snapshot; the highest preceding slot (mount amulet
            // in the normal layout) is the least-visible overflow record.
            selected[^1] = populated[mountIndex];
            Array.Sort(selected, static (left, right) => left.Slot.CompareTo(right.Slot));
        }

        return selected;
    }

    private static (int Slot, CompactItemEntry Item)[] EquipmentItemsForInspect(GameCharacter character)
    {
        var equipment = ParseEquipment(character);
        return InspectEquipmentSlots
            .Select(slot => (Slot: slot, Item: slot < equipment.Length ? equipment[slot] : default))
            .ToArray();
    }

    private static CompactItemEntry[] ParseEquipment(GameCharacter character)
    {
        return EquipmentFor(character)
            .Split('#', StringSplitOptions.RemoveEmptyEntries)
            .Select(CompactItemEntry.Parse)
            .ToArray();
    }
}
