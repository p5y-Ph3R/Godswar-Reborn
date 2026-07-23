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
    private static Task CheckHolyStoneAuthoritativePersistencePlanAsync()
    {
        const byte profession = 0;
        var equipment = string.Join('#', Enumerable.Repeat("[]", 24)) + '#';
        var kitBag = string.Join('#', Enumerable.Repeat("[]", 96)) + '#';

        var target = CompactItemEntry.Empty with
        {
            Id = 1000,
            Attribute1 = 11,
            Attribute2 = 12,
            Attribute3 = 13,
            Attribute4 = 14,
            Attribute5 = 15,
            Quality = 20,
            Grade = 25,
            Bound = 1,
            Stack = 1,
            Exp = 87_654,
            HolySuitCode = 610,
            AttributeLevel1 = 21,
            AttributeLevel2 = 22,
            AttributeLevel3 = 23,
            AttributeLevel4 = 24,
            AttributeLevel5 = 25,
            SocketCount = 2,
            Socket1EffectId = 2,
            Socket1Level = 9
        };
        var stone = CompactItemEntry.Empty with
        {
            Id = 9060,
            Quality = 9,
            Grade = 7,
            Bound = 1,
            Stack = 1
        };
        var unrelatedBagItem = CompactItemEntry.Empty with
        {
            Id = 2200,
            Attribute1 = 31,
            Attribute2 = 32,
            Quality = 22,
            Grade = 24,
            Bound = 1,
            Stack = 1,
            Exp = 44_444,
            HolySuitCode = 509,
            AttributeLevel1 = 24,
            AttributeLevel2 = 23,
            SocketCount = 4,
            Socket1EffectId = 3,
            Socket1Level = 10,
            Socket4EffectId = 8,
            Socket4Level = 6
        };
        var unrelatedEquipmentItem = CompactItemEntry.Empty with
        {
            Id = 2100,
            Attribute1 = 41,
            Attribute5 = 45,
            Quality = 21,
            Grade = 25,
            Bound = 1,
            Stack = 1,
            Exp = 55_555,
            HolySuitCode = 407,
            AttributeLevel1 = 25,
            AttributeLevel5 = 21,
            SocketCount = 2,
            Socket1EffectId = 5,
            Socket1Level = 8,
            Socket2EffectId = 7,
            Socket2Level = 5
        };

        kitBag = KitBagSlots.SetSlot(kitBag, 0, target.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, 1, stone.ToCompactString());
        kitBag = KitBagSlots.SetSlot(kitBag, 2, unrelatedBagItem.ToCompactString());
        equipment = EquipmentSlots.SetSlot(
            equipment,
            profession,
            EquipmentSlots.Armor,
            unrelatedEquipmentItem.ToCompactString());

        Check.True(
            HolyStonePersistencePlanner.TryCreate(
                equipment,
                kitBag,
                profession,
                HolyStoneOperation.MountStone,
                targetKitBagSlot: 0,
                socketIndex: 1,
                stoneKitBagSlot: 1,
                destinationKitBagSlot: -1,
                out var plan,
                out var summary),
            $"valid authoritative holy-stone plan is created: {summary}");

        Check.Equal(2, plan!.Mutations.Count, "only target and consumed-stone slots are scheduled for persistence");
        Check.True(
            plan.Mutations.Any(static mutation => mutation.IsKitBag && mutation.Slot == 0),
            "target weapon slot is scheduled");
        Check.True(
            plan.Mutations.Any(static mutation => mutation.IsKitBag && mutation.Slot == 1),
            "consumed stone slot is scheduled");
        Check.True(
            plan.Mutations.All(static mutation => mutation.IsKitBag && mutation.Slot is 0 or 1),
            "no unrelated equipment or bag slot is scheduled");

        var updatedTarget = KitBagSlots.GetItem(plan.UpdatedKitBag, 0);
        Check.Equal(target.Quality, updatedTarget.Quality, "extended target quality remains authoritative");
        Check.Equal(target.Grade, updatedTarget.Grade, "extended target grade remains authoritative");
        Check.True(target.Attribute1 == updatedTarget.Attribute1, "target attributes remain unchanged");
        Check.True(target.Attribute5 == updatedTarget.Attribute5, "all target attribute positions remain unchanged");
        Check.True(target.AttributeLevel1 == updatedTarget.AttributeLevel1, "target attribute levels remain unchanged");
        Check.True(target.AttributeLevel5 == updatedTarget.AttributeLevel5, "all target attribute levels remain unchanged");
        Check.Equal(target.HolySuitCode, updatedTarget.HolySuitCode, "target holy-suit state remains unchanged");
        Check.True(target.Socket1EffectId == updatedTarget.Socket1EffectId, "existing target stone remains unchanged");
        Check.True(updatedTarget.Socket2EffectId == 1, "new stone is mounted in the requested socket");
        Check.True(updatedTarget.Socket2Level == 7, "new stone grade determines its level");

        Check.Equal(
            unrelatedBagItem,
            KitBagSlots.GetItem(plan.UpdatedKitBag, 2),
            "unrelated high-ceiling bag quality, grade, attributes, holy suit, and stones remain byte-equivalent");
        Check.Equal(
            unrelatedEquipmentItem,
            EquipmentSlots.GetItem(plan.UpdatedEquipment, profession, EquipmentSlots.Armor),
            "unrelated high-ceiling equipment quality, grade, attributes, holy suit, and stones remain byte-equivalent");

        return Task.CompletedTask;
    }
}
