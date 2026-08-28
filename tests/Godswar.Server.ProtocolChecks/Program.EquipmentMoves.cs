using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static async Task CheckGuardedEquipmentMoveAsync()
    {
        var shieldEntry = CheckGuardedEquipmentMoveRules();
        await CheckGuardedEquipmentMovePersistenceAsync(shieldEntry);
    }

    private static string CheckGuardedEquipmentMoveRules()
    {
        Check.True(
            EquipmentSlots.TryGetAuthoritativeSlot(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, 1000, out var weaponSlot),
            "starter sword is present in the authoritative equipment catalog");
        Check.Equal(EquipmentSlots.Weapon, weaponSlot, "starter sword resolves to the weapon slot");
        Check.True(
            !EquipmentSlots.TryGetAuthoritativeSlot(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, 4030, out _),
            "MP potion is absent from the authoritative equipment catalog");
        Check.Equal(-1, EquipmentSlots.ResolveSlotForItem(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, 4030, -1), "unknown item has no weapon fallback");
        Check.Equal(
            EquipmentSlots.Weapon,
            EquipmentSlots.ResolveSlotForItem(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, 1000, requestedSlot: -1),
            "right-click equip infers the authoritative weapon slot");
        Check.Equal(
            EquipmentSlots.Weapon,
            EquipmentSlots.ResolveSlotForItem(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, 1000, EquipmentSlots.Weapon),
            "explicit drag accepts the authoritative weapon slot");
        Check.Equal(
            -1,
            EquipmentSlots.ResolveSlotForItem(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, 1000, EquipmentSlots.Armor),
            "explicit drag rejects an incompatible equipment slot");
        Check.Equal(
            EquipmentSlots.Ring2,
            EquipmentSlots.ResolveSlotForItem(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, 3200, EquipmentSlots.Ring2),
            "explicit ring drag accepts either ring slot");
        Check.Equal(
            -1,
            EquipmentSlots.ResolveSlotForItem(Godswar.Server.ProtocolChecks.TestItemContent.Catalog, 3200, EquipmentSlots.Weapon),
            "explicit ring drag rejects a non-ring slot");

        var rightClickEquipPayload = Enumerable.Repeat((byte)0xFF, 88).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(rightClickEquipPayload.AsSpan(0, 4), 7u);
        BinaryPrimitives.WriteUInt32LittleEndian(rightClickEquipPayload.AsSpan(4, 4), 5067u);
        BinaryPrimitives.WriteUInt16LittleEndian(rightClickEquipPayload.AsSpan(8, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(rightClickEquipPayload.AsSpan(10, 2), 0);
        Check.True(
            GameClientHandler.TryReadBreakItemEquip(rightClickEquipPayload, out var rightClickSourceSlot),
            "right-click 10051 request parses its bag source while an NPC is selected");
        Check.Equal(0, rightClickSourceSlot, "live sword request resolves authoritative bag slot zero");

        var dragEquipPayload = new byte[88];
        BinaryPrimitives.WriteUInt32LittleEndian(dragEquipPayload.AsSpan(4, 4), 5140u);
        BinaryPrimitives.WriteUInt16LittleEndian(dragEquipPayload.AsSpan(8, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(dragEquipPayload.AsSpan(10, 2), 7);
        dragEquipPayload.AsSpan(12).Fill(0xA5);
        Check.True(
            GameClientHandler.TryReadBreakItemEquip(dragEquipPayload, out var dragSourceSlot),
            "drag/drop 10051 request ignores the selected NPC and uses the stable bag source");
        Check.Equal(55, dragSourceSlot, "drag/drop 10051 source uses packed page/index coordinates");

        var unequipPayload = new byte[76];
        BinaryPrimitives.WriteUInt16LittleEndian(unequipPayload.AsSpan(4, 2), EquipmentSlots.Shield);
        BinaryPrimitives.WriteUInt16LittleEndian(unequipPayload.AsSpan(6, 2), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(unequipPayload.AsSpan(8, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(unequipPayload.AsSpan(10, 2), 7);
        Check.True(
            GameClientHandler.TryReadStorageItemEquipmentBagTransfer(
                unequipPayload,
                out var parsedUnequipSlot,
                out var parsedUnequipDestination),
            "unequip parses an exact valid equipment-to-bag destination");
        Check.Equal(EquipmentSlots.Shield, parsedUnequipSlot, "unequip equipment source slot");
        Check.Equal(55, parsedUnequipDestination, "unequip exact bag destination");
        BinaryPrimitives.WriteUInt16LittleEndian(unequipPayload.AsSpan(8, 2), 4);
        Check.True(
            !GameClientHandler.TryReadStorageItemEquipmentBagTransfer(unequipPayload, out _, out _),
            "unequip rejects a destination outside the four bag pages");

        BinaryPrimitives.WriteUInt16LittleEndian(unequipPayload.AsSpan(8, 2), 2);
        var transferAck = PacketBuilder.StorageItemEquipmentBagTransfer(
            EquipmentSlots.Shield,
            bagSlot: 55);
        Check.Equal(42, transferAck.Length, "equipment/bag transfer acknowledgement length");
        Check.Equal((ushort)10052, ReadUInt16(transferAck, 2), "equipment/bag transfer acknowledgement opcode");
        Check.Equal(0x1448u, ReadUInt32(transferAck, 4), "equipment/bag transfer local player object ID");
        Check.Equal((ushort)EquipmentSlots.Shield, ReadUInt16(transferAck, 8), "equipment descriptor is first");
        Check.Equal(ushort.MaxValue, ReadUInt16(transferAck, 10), "equipment descriptor sentinel");
        Check.Equal((ushort)2, ReadUInt16(transferAck, 12), "bag descriptor page is second");
        Check.Equal((ushort)7, ReadUInt16(transferAck, 14), "bag descriptor index is second");
        Check.Equal(-1, ReadInt32(transferAck, 16), "equipment/bag transfer move sentinel");

        var shieldEntry = EquipmentSlots.GetEntry(
            GameDefaults.DefaultEquipment(profession: 0),
            profession: 0,
            EquipmentSlots.Shield);
        var emptyTargetCharacter = new GameCharacter
        {
            Profession = 0,
            Equipment = GameDefaults.DefaultEquipment(profession: 0),
            KitBag = GameDefaults.EmptyKitBag
        };
        Check.True(
            GameClientHandler.ResolveEquipmentBagTransferAction(TestItemContent.Catalog,
                emptyTargetCharacter,
                EquipmentSlots.Shield,
                bagSlot: 55) == EquipmentBagTransferAction.Unequip,
            "occupied equipment to an empty requested bag slot resolves as unequip");

        var occupiedTargetCharacter = new GameCharacter
        {
            Profession = 0,
            Equipment = GameDefaults.DefaultEquipment(profession: 0),
            KitBag = KitBagSlots.SetSlot(GameDefaults.EmptyKitBag, 55, shieldEntry)
        };
        Check.True(
            GameClientHandler.ResolveEquipmentBagTransferAction(TestItemContent.Catalog,
                occupiedTargetCharacter,
                EquipmentSlots.Shield,
                bagSlot: 55) == EquipmentBagTransferAction.Replace,
            "compatible bag gear replaces occupied equipment atomically");

        var emptyEquipmentCharacter = new GameCharacter
        {
            Profession = 0,
            Equipment = EquipmentSlots.ClearSlot(
                GameDefaults.DefaultEquipment(profession: 0),
                profession: 0,
                EquipmentSlots.Shield),
            KitBag = KitBagSlots.SetSlot(GameDefaults.EmptyKitBag, 55, shieldEntry)
        };
        Check.True(
            GameClientHandler.ResolveEquipmentBagTransferAction(TestItemContent.Catalog,
                emptyEquipmentCharacter,
                EquipmentSlots.Shield,
                bagSlot: 55) == EquipmentBagTransferAction.Equip,
            "compatible bag gear to an empty equipment slot resolves as explicit drag equip");
        var incompatibleEmptyEquipmentCharacter = new GameCharacter
        {
            Profession = 0,
            Equipment = EquipmentSlots.ClearSlot(
                emptyEquipmentCharacter.Equipment,
                profession: 0,
                EquipmentSlots.Weapon),
            KitBag = emptyEquipmentCharacter.KitBag
        };
        Check.True(
            GameClientHandler.ResolveEquipmentBagTransferAction(TestItemContent.Catalog,
                incompatibleEmptyEquipmentCharacter,
                EquipmentSlots.Weapon,
                bagSlot: 55) == EquipmentBagTransferAction.Reject,
            "explicit drag rejects a bag item aimed at an incompatible empty equipment slot");

        var matchingCharacter = new GameCharacter { KitBag = GameDefaults.StarterKitBag };
        Check.True(
            GameClientHandler.MatchesCurrentKitBagItem(matchingCharacter, 0, 4000),
            "current bag item matches its authoritative slot");
        Check.True(
            !GameClientHandler.MatchesCurrentKitBagItem(matchingCharacter, 0, 0xFFFFFFFC),
            "client sentinel item ID cannot be cached as an equip source");

        var priorRingEquipment = GameDefaults.DefaultEquipment(0);
        priorRingEquipment = EquipmentSlots.SetSlot(
            priorRingEquipment,
            0,
            EquipmentSlots.Ring1,
            "[3200,,,,,,1,1,1,1,0]");
        var duplicateRingCharacter = new GameCharacter
        {
            Profession = 0,
            Equipment = EquipmentSlots.SetSlot(
                priorRingEquipment,
                0,
                EquipmentSlots.Ring2,
                "[3200,,,,,,10,12,1,1,0]")
        };
        Check.Equal(
            EquipmentSlots.Ring2,
            GameClientHandler.ResolveEquippedSlotForAck(
                duplicateRingCharacter,
                priorRingEquipment,
                requestedEquipmentSlot: -1,
                itemIdHint: 3200),
            "inferred duplicate ring resolves to the slot changed by the equip");

        var priorMountEquipment = GameDefaults.DefaultEquipment(0);
        var erebusMountCharacter = new GameCharacter
        {
            Profession = 0,
            Equipment = EquipmentSlots.SetSlot(
                priorMountEquipment,
                0,
                EquipmentSlots.Mount,
                "[16204,,,,,,1,1,1,1,0]")
        };
        var resolvedErebusSlot = GameClientHandler.ResolveEquippedSlotForAck(
            erebusMountCharacter,
            priorMountEquipment,
            requestedEquipmentSlot: -1,
            itemIdHint: 16204);
        Check.Equal(
            EquipmentSlots.Mount,
            resolvedErebusSlot,
            "right-click Erebus Lion equip resolves the mount acknowledgement slot");
        var erebusEquipSnapshot = PacketBuilder.EquipmentItemEquipSnapshot(
            erebusMountCharacter,
            sourceSlot: 19,
            resolvedErebusSlot);
        Check.Equal(92, erebusEquipSnapshot.Length, "right-click Erebus Lion emits an equip snapshot");
        Check.Equal(16204u, ReadUInt32(erebusEquipSnapshot, 20), "Erebus Lion equip snapshot item ID");

        return shieldEntry;
    }
}
