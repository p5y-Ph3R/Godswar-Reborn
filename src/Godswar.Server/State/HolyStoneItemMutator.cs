namespace Godswar.Server.State;

internal static class HolyStoneItemMutator
{
    public const int MaxSockets = 4;
    public const int HeatedHolyStoneItemId = 9030;
    private const short DefaultHeatedEffectId = 1;
    private const short DefaultStoneLevel = 1;

    public static bool TryApply(
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
        updatedEquipment = equipment;
        updatedKitBag = kitBag;
        summary = string.Empty;

        if (!TryGetTargetWeapon(equipment, kitBag, profession, targetKitBagSlot, out var target))
        {
            summary = "no weapon target found";
            return false;
        }

        var item = target.Item;
        var changed = operation switch
        {
            HolyStoneOperation.DrillSocket => TryDrill(ref item, out summary),
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

    private static bool TryGetTargetWeapon(
        string equipment,
        string kitBag,
        byte profession,
        int targetKitBagSlot,
        out HolyStoneTarget target)
    {
        if (IsKitBagSlot(targetKitBagSlot))
        {
            var requestedItem = KitBagSlots.GetItem(kitBag, targetKitBagSlot);
            if (IsWeapon(requestedItem.Id))
            {
                target = new HolyStoneTarget(true, targetKitBagSlot, requestedItem);
                return true;
            }
        }

        for (var slot = 0; slot < 96; slot++)
        {
            var item = KitBagSlots.GetItem(kitBag, slot);
            if (IsWeapon(item.Id))
            {
                target = new HolyStoneTarget(true, slot, item);
                return true;
            }
        }

        var equippedWeapon = EquipmentSlots.GetItem(equipment, profession, EquipmentSlots.Weapon);
        if (IsWeapon(equippedWeapon.Id))
        {
            target = new HolyStoneTarget(false, EquipmentSlots.Weapon, equippedWeapon);
            return true;
        }

        target = default;
        return false;
    }

    private static bool TryDrill(ref CompactItemEntry item, out string summary)
    {
        var current = Math.Clamp(item.SocketCount, (short)0, (short)MaxSockets);
        if (current >= MaxSockets)
        {
            summary = $"socket_count already {MaxSockets}";
            return false;
        }

        item = item with { SocketCount = (short)(current + 1) };
        summary = $"drilled socket={current + 1}";
        return true;
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
            socketCount = 1;
            item = item with { SocketCount = socketCount };
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
        var effectId = ResolveHeatedEffectId(stoneItem.Id);
        var stoneLevel = ResolveStoneLevel(stoneItem);
        if (HasSocketEffect(item, effectId))
        {
            summary = $"duplicate spirit effect={effectId}";
            return false;
        }

        item = SetSocket(item, socket, effectId, stoneLevel);
        if (!stoneItem.IsEmpty)
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

        item = SetSocket(item, socket, null, null);
        var destinationSlot = IsKitBagSlot(destinationKitBagSlot) &&
                              KitBagSlots.GetItem(updatedKitBag, destinationKitBagSlot).IsEmpty
            ? destinationKitBagSlot
            : FindFirstEmptyKitBagSlot(updatedKitBag);

        if (destinationSlot >= 0)
        {
            updatedKitBag = KitBagSlots.SetSlot(updatedKitBag, destinationSlot, CreateSimpleItem(HeatedHolyStoneItemId).ToCompactString());
        }

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

        for (var socket = 0; socket < MaxSockets; socket++)
        {
            if (GetSocketEffect(item, socket).HasValue)
            {
                return socket;
            }
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

    private static short ResolveHeatedEffectId(uint itemId)
    {
        return itemId switch
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
            _ => DefaultHeatedEffectId
        };
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

        return DefaultStoneLevel;
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

    private static CompactItemEntry CreateSimpleItem(int itemId)
    {
        return CompactItemEntry.Empty with
        {
            Id = (uint)itemId,
            Quality = 1,
            Grade = 1,
            Bound = 1,
            Stack = 1
        };
    }

    private static bool IsWeapon(uint itemId)
    {
        return itemId is >= 1000 and < 2000;
    }

    private static bool IsKitBagSlot(int slot)
    {
        return slot is >= 0 and < 96;
    }

    private readonly record struct HolyStoneTarget(bool IsKitBag, int Slot, CompactItemEntry Item);
}
