using Godswar.Server.Application.Items;

namespace Godswar.Server.State;

internal static partial class HolyStoneItemMutator
{
    public const int MaxSockets = 4;
    public const int HeatedHolyStoneItemId = 9030;

    public static bool TryApply(
        IItemTemplateCatalog templates,
        string equipment,
        string kitBag,
        byte profession,
        HolyStoneOperation operation,
        int targetKitBagSlot,
        int socketIndex,
        int stoneKitBagSlot,
        int destinationKitBagSlot,
        out string updatedEquipment,
        out string updatedKitBag,
        out string summary)
    {
        return TryApply(
            templates,
            equipment,
            kitBag,
            profession,
            operation,
            HolyStoneTargetMode.LegacyFallback,
            targetKitBagSlot,
            socketIndex,
            stoneKitBagSlot,
            destinationKitBagSlot,
            out updatedEquipment,
            out updatedKitBag,
            out summary);
    }

    public static bool TryApply(
        IItemTemplateCatalog templates,
        string equipment,
        string kitBag,
        byte profession,
        HolyStoneOperation operation,
        HolyStoneTargetMode targetMode,
        int targetKitBagSlot,
        int socketIndex,
        int stoneKitBagSlot,
        int destinationKitBagSlot,
        out string updatedEquipment,
        out string updatedKitBag,
        out string summary)
    {
        updatedEquipment = equipment;
        updatedKitBag = kitBag;
        summary = string.Empty;

        if (!TryGetTarget(
                templates,
                equipment,
                kitBag,
                profession,
                targetMode,
                targetKitBagSlot,
                allowNormalCharacterGear:
                    operation is
                        HolyStoneOperation.DrillSocket or
                        HolyStoneOperation.AdvancedDrillSocket,
                out var target))
        {
            summary = "no supported equipment target found";
            return false;
        }
        if ((operation is
                HolyStoneOperation.MountStone or
                HolyStoneOperation.AdvancedDrillSocket) &&
            target.IsKitBag &&
            target.Slot == stoneKitBagSlot)
        {
            summary = "target and material slots must differ";
            return false;
        }

        var item = target.Item;
        var changed = operation switch
        {
            HolyStoneOperation.DrillSocket => TryDrill(
                templates,
                ref item,
                out summary),
            HolyStoneOperation.AdvancedDrillSocket => TryAdvancedDrill(
                templates,
                updatedKitBag,
                ref item,
                stoneKitBagSlot,
                out updatedKitBag,
                out summary),
            HolyStoneOperation.MountStone => TryMount(updatedKitBag, ref item, socketIndex, stoneKitBagSlot, out updatedKitBag, out summary),
            HolyStoneOperation.RemoveStone => TryRemove(updatedKitBag, ref item, socketIndex, destinationKitBagSlot, out updatedKitBag, out summary),
            _ => false
        };

        if (!changed)
        {
            return false;
        }

        if (target.IsKitBag)
        {
            updatedKitBag = KitBagSlots.SetSlot(updatedKitBag, target.Slot, item.ToCompactString());
        }
        else
        {
            updatedEquipment = EquipmentSlots.SetSlot(updatedEquipment, profession, EquipmentSlots.Weapon, item.ToCompactString());
        }

        return true;
    }

    private static bool TryGetTarget(
        IItemTemplateCatalog templates,
        string equipment,
        string kitBag,
        byte profession,
        HolyStoneTargetMode targetMode,
        int targetKitBagSlot,
        bool allowNormalCharacterGear,
        out HolyStoneTarget target)
    {
        if (targetMode == HolyStoneTargetMode.EquippedWeapon)
        {
            var equipped = EquipmentSlots.GetItem(
                equipment,
                profession,
                EquipmentSlots.Weapon);
            if (IsEligibleTarget(
                    templates,
                    equipped,
                    allowNormalCharacterGear: false))
            {
                target = new HolyStoneTarget(
                    false,
                    EquipmentSlots.Weapon,
                    equipped);
                return true;
            }

            target = default;
            return false;
        }

        if (IsKitBagSlot(targetKitBagSlot))
        {
            var requestedItem = KitBagSlots.GetItem(kitBag, targetKitBagSlot);
            if (IsEligibleTarget(
                    templates,
                    requestedItem,
                    allowNormalCharacterGear))
            {
                target = new HolyStoneTarget(true, targetKitBagSlot, requestedItem);
                return true;
            }
        }

        if (targetMode == HolyStoneTargetMode.KitBag)
        {
            target = default;
            return false;
        }

        for (var slot = 0; slot < 96; slot++)
        {
            var item = KitBagSlots.GetItem(kitBag, slot);
            if (IsEligibleTarget(
                    templates,
                    item,
                    allowNormalCharacterGear))
            {
                target = new HolyStoneTarget(true, slot, item);
                return true;
            }
        }

        var equippedWeapon = EquipmentSlots.GetItem(equipment, profession, EquipmentSlots.Weapon);
        if (IsEligibleTarget(
                templates,
                equippedWeapon,
                allowNormalCharacterGear: false))
        {
            target = new HolyStoneTarget(false, EquipmentSlots.Weapon, equippedWeapon);
            return true;
        }

        target = default;
        return false;
    }

    private static bool TryMount(
        string kitBag,
        ref CompactItemEntry item,
        int socketIndex,
        int stoneKitBagSlot,
        out string updatedKitBag,
        out string summary)
    {
        updatedKitBag = kitBag;
        var socketCount = Math.Clamp(item.SocketCount, (short)0, (short)MaxSockets);
        if (socketCount <= 0)
        {
            summary = "target has no drilled socket";
            return false;
        }

        var socket = ResolveMountSocket(item, socketIndex, socketCount);
        if (socket < 0)
        {
            summary = "no available socket";
            return false;
        }

        var stoneItem = IsKitBagSlot(stoneKitBagSlot)
            ? KitBagSlots.GetItem(kitBag, stoneKitBagSlot)
            : CompactItemEntry.Empty;
        if (stoneItem.IsEmpty ||
            stoneItem.Stack <= 0 ||
            !TryResolveHeatedEffectId(
                stoneItem.Id,
                out var effectId))
        {
            summary = "selected material is not a Fire Spirit";
            return false;
        }
        var stoneLevel = ResolveStoneLevel(stoneItem);
        if (HasSocketEffect(item, effectId))
        {
            summary = $"duplicate spirit effect={effectId}";
            return false;
        }

        item = SetSocket(item, socket, effectId, stoneLevel);
        if (stoneItem.Stack > 1)
        {
            updatedKitBag = KitBagSlots.SetSlot(
                updatedKitBag,
                stoneKitBagSlot,
                (stoneItem with
                {
                    Stack = checked((short)(stoneItem.Stack - 1))
                }).ToCompactString());
        }
        else
        {
            updatedKitBag = KitBagSlots.ClearSlot(updatedKitBag, stoneKitBagSlot);
        }

        summary = $"mounted effect={effectId} level={stoneLevel} socket={socket + 1} stoneSlot={stoneKitBagSlot}";
        return true;
    }

    private static bool TryRemove(
        string kitBag,
        ref CompactItemEntry item,
        int socketIndex,
        int destinationKitBagSlot,
        out string updatedKitBag,
        out string summary)
    {
        updatedKitBag = kitBag;
        var socket = ResolveRemoveSocket(item, socketIndex);
        if (socket < 0)
        {
            summary = "no mounted stone found";
            return false;
        }

        var removedLevel = GetSocketLevel(item, socket);
        var destinationSlot = IsKitBagSlot(destinationKitBagSlot) &&
                              KitBagSlots.GetItem(updatedKitBag, destinationKitBagSlot).IsEmpty
            ? destinationKitBagSlot
            : FindFirstEmptyKitBagSlot(updatedKitBag);

        if (destinationSlot < 0)
        {
            summary = "kit bag is full";
            return false;
        }

        item = SetSocket(item, socket, null, null);
        updatedKitBag = KitBagSlots.SetSlot(
            updatedKitBag,
            destinationSlot,
            CreateSimpleItem(
                HeatedHolyStoneItemId,
                removedLevel ?? 1).ToCompactString());
        summary = $"removed socket={socket + 1} destinationSlot={destinationSlot}";
        return true;
    }

    private static int ResolveMountSocket(CompactItemEntry item, int requestedSocket, int socketCount)
    {
        if (requestedSocket is >= 0 and < MaxSockets && requestedSocket < socketCount)
        {
            return requestedSocket;
        }

        for (var socket = 0; socket < socketCount; socket++)
        {
            if (!GetSocketEffect(item, socket).HasValue)
            {
                return socket;
            }
        }

        return -1;
    }

    private static int ResolveRemoveSocket(CompactItemEntry item, int requestedSocket)
    {
        if (requestedSocket is >= 0 and < MaxSockets && GetSocketEffect(item, requestedSocket).HasValue)
        {
            return requestedSocket;
        }

        return -1;
    }

    private static short? GetSocketEffect(CompactItemEntry item, int socket)
    {
        return socket switch
        {
            0 => item.Socket1EffectId,
            1 => item.Socket2EffectId,
            2 => item.Socket3EffectId,
            3 => item.Socket4EffectId,
            4 => item.Socket5EffectId,
            5 => item.Socket6EffectId,
            _ => null
        };
    }

    private static short? GetSocketLevel(
        CompactItemEntry item,
        int socket) =>
        socket switch
        {
            0 => item.Socket1Level,
            1 => item.Socket2Level,
            2 => item.Socket3Level,
            3 => item.Socket4Level,
            _ => null
        };

    private static CompactItemEntry SetSocket(CompactItemEntry item, int socket, short? effectId, short? level)
    {
        return socket switch
        {
            0 => item with { Socket1EffectId = effectId, Socket1Level = level },
            1 => item with { Socket2EffectId = effectId, Socket2Level = level },
            2 => item with { Socket3EffectId = effectId, Socket3Level = level },
            3 => item with { Socket4EffectId = effectId, Socket4Level = level },
            4 => item with { Socket5EffectId = effectId, Socket5Level = level },
            5 => item with { Socket6EffectId = effectId, Socket6Level = level },
            _ => item
        };
    }

    private static bool TryResolveHeatedEffectId(
        uint itemId,
        out short effectId)
    {
        effectId = itemId switch
        {
            9060 => 1,
            9061 => 2,
            9062 => 5,
            9063 => 6,
            9064 => 7,
            9065 => 8,
            9066 => 3,
            9067 => 4,
            9088 => 17,
            9089 => 18,
            _ => 0
        };
        return effectId > 0;
    }

    private static bool HasSocketEffect(CompactItemEntry item, short effectId)
    {
        for (var socket = 0; socket < MaxSockets; socket++)
        {
            if (GetSocketEffect(item, socket) == effectId)
            {
                return true;
            }
        }

        return false;
    }

    private static short ResolveStoneLevel(CompactItemEntry stoneItem)
    {
        if (stoneItem.Grade > 0)
        {
            return (short)Math.Clamp((int)stoneItem.Grade, 1, 10);
        }

        if (stoneItem.Quality > 0)
        {
            return (short)Math.Clamp((int)stoneItem.Quality, 1, 10);
        }

        return 1;
    }

    private static int FindFirstEmptyKitBagSlot(string kitBag)
    {
        for (var slot = 0; slot < 96; slot++)
        {
            if (KitBagSlots.GetItem(kitBag, slot).IsEmpty)
            {
                return slot;
            }
        }

        return -1;
    }

    private static CompactItemEntry CreateSimpleItem(
        int itemId,
        short grade)
    {
        return CompactItemEntry.Empty with
        {
            Id = (uint)itemId,
            Quality = 1,
            Grade = (short)Math.Clamp(grade, (short)1, (short)10),
            Bound = 1,
            Stack = 1
        };
    }

    private static bool IsEligibleTarget(
        IItemTemplateCatalog templates,
        CompactItemEntry item,
        bool allowNormalCharacterGear) =>
        item.Stack == 1 &&
        (allowNormalCharacterGear
            ? HolyStoneEquipmentEligibility.IsNormalCharacterGear(
                templates,
                item.Id)
            : HolyStoneEquipmentEligibility.IsWeapon(
                templates,
                item.Id));

    private static bool IsKitBagSlot(int slot)
    {
        return slot is >= 0 and < 96;
    }

    private readonly record struct HolyStoneTarget(bool IsKitBag, int Slot, CompactItemEntry Item);
}
