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
    private static async Task CheckGuardedEquipmentMovePersistenceAsync(string shieldEntry)
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-equipment-guard-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            await using var store = new JsonGameStore(dataPath);
            await store.EnsureSeedDataAsync();
            var owner = await store.LoginOrCreateAccountAsync("equipment-guard-owner", "");
            var character = await store.CreateCharacterAsync(
                owner.Id,
                new GameCharacter { Name = "EquipmentGuardHero", Profession = 0 });

            var occupiedExplicitOwner = await store.LoginOrCreateAccountAsync(
                "equipment-occupied-explicit-owner",
                "");
            const string replacementShieldEntry = "[2000,,,,,,10,12,1,1,0]";
            var occupiedExplicitCharacter = await store.CreateCharacterAsync(
                occupiedExplicitOwner.Id,
                new GameCharacter
                {
                    Name = "OccupiedExplicitGuardHero",
                    Profession = 0,
                    Equipment = GameDefaults.DefaultEquipment(profession: 0),
                    KitBag = KitBagSlots.SetSlot(
                        GameDefaults.EmptyKitBag,
                        55,
                        replacementShieldEntry)
                });
            var rejectedOccupiedExplicit = await store.MoveKitBagToEquipmentAsync(
                occupiedExplicitOwner.Id,
                occupiedExplicitCharacter.Id,
                kitBagSlot: 55,
                requestedEquipmentSlot: EquipmentSlots.Shield,
                requireEmptyEquipmentSlot: true)
                ?? throw new InvalidOperationException("occupied explicit-equipment guard did not return the character");
            Check.Equal(
                shieldEntry,
                EquipmentSlots.GetEntry(
                    rejectedOccupiedExplicit.Equipment,
                    rejectedOccupiedExplicit.Profession,
                    EquipmentSlots.Shield),
                "explicit drag does not replace an occupied equipment slot");
            Check.Equal(
                replacementShieldEntry,
                KitBagSlots.GetEntry(rejectedOccupiedExplicit.KitBag, 55),
                "rejected explicit drag preserves its exact bag source");

            var rightClickReplacement = await store.MoveKitBagToEquipmentAsync(
                occupiedExplicitOwner.Id,
                occupiedExplicitCharacter.Id,
                kitBagSlot: 55,
                requestedEquipmentSlot: -1)
                ?? throw new InvalidOperationException("right-click replacement could not equip the shield");
            Check.Equal(
                replacementShieldEntry,
                EquipmentSlots.GetEntry(
                    rightClickReplacement.Equipment,
                    rightClickReplacement.Profession,
                    EquipmentSlots.Shield),
                "right-click equip can replace compatible occupied equipment");
            Check.Equal(
                shieldEntry,
                KitBagSlots.GetEntry(rightClickReplacement.KitBag, 55),
                "right-click replacement returns the previous gear to the source bag slot");

            var rejectedPotion = await store.MoveKitBagToEquipmentAsync(
                owner.Id,
                character.Id,
                kitBagSlot: 1,
                requestedEquipmentSlot: -1);
            Check.True(rejectedPotion is null, "consumable cannot be moved into equipment");

            var afterRejectedPotion = await store.GetFirstCharacterAsync(owner.Id)
                ?? throw new InvalidOperationException("equipment guard fixture was not reloaded");
            Check.Equal(
                1000u,
                EquipmentSlots.GetItemId(afterRejectedPotion.Equipment, afterRejectedPotion.Profession, EquipmentSlots.Weapon),
                "rejected consumable does not displace the equipped weapon");
            Check.Equal(4030u, KitBagSlots.GetItemId(afterRejectedPotion.KitBag, 1), "rejected consumable remains in bag");

            var rejectedEmptySlot = await store.MoveKitBagToEquipmentAsync(
                owner.Id,
                character.Id,
                kitBagSlot: 23,
                requestedEquipmentSlot: -1);
            Check.True(rejectedEmptySlot is null, "empty bag slot is not reported as a successful equip");

            var occupiedDestination = await store.MoveEquipmentToKitBagAsync(
                owner.Id,
                character.Id,
                EquipmentSlots.Weapon,
                kitBagSlot: 1)
                ?? throw new InvalidOperationException("occupied-destination unequip guard did not return the character");
            Check.Equal(4030u, KitBagSlots.GetItemId(occupiedDestination.KitBag, 1), "occupied destination item is preserved");
            Check.Equal(
                1000u,
                EquipmentSlots.GetItemId(
                    occupiedDestination.Equipment,
                    occupiedDestination.Profession,
                    EquipmentSlots.Weapon),
                "occupied destination leaves the weapon equipped");

            var invalidDestination = await store.MoveEquipmentToKitBagAsync(
                owner.Id,
                character.Id,
                EquipmentSlots.Weapon,
                kitBagSlot: 96)
                ?? throw new InvalidOperationException("invalid-destination unequip guard did not return the character");
            Check.Equal(
                1000u,
                EquipmentSlots.GetItemId(
                    invalidDestination.Equipment,
                    invalidDestination.Profession,
                    EquipmentSlots.Weapon),
                "invalid destination leaves the weapon equipped");

            var unequipped = await store.MoveEquipmentToKitBagAsync(
                owner.Id,
                character.Id,
                EquipmentSlots.Weapon,
                kitBagSlot: 95)
                ?? throw new InvalidOperationException("starter sword could not be moved into the bag");
            Check.Equal(1000u, KitBagSlots.GetItemId(unequipped.KitBag, 95), "starter sword uses the exact empty destination requested by the client");
            Check.Equal(0u, KitBagSlots.GetItemId(unequipped.KitBag, 2), "an earlier empty slot is not substituted for the requested destination");

            var equipped = await store.MoveKitBagToEquipmentAsync(
                owner.Id,
                character.Id,
                kitBagSlot: 95,
                requestedEquipmentSlot: -1)
                ?? throw new InvalidOperationException("starter sword could not be equipped again");
            Check.Equal(
                1000u,
                EquipmentSlots.GetItemId(equipped.Equipment, equipped.Profession, EquipmentSlots.Weapon),
                "right-click starter sword is equipped in its inferred authoritative slot");
            Check.Equal(0u, KitBagSlots.GetItemId(equipped.KitBag, 95), "right-click source bag slot is cleared after equip");

            var snapshot = PacketBuilder.EquipmentItemEquipSnapshot(
                equipped,
                sourceSlot: 95,
                EquipmentSlots.Weapon);
            Check.Equal(92, snapshot.Length, "equip snapshot length");
            Check.Equal((ushort)10051, ReadUInt16(snapshot, 2), "equip snapshot opcode");
            Check.Equal(0x1448u, ReadUInt32(snapshot, 4), "equip snapshot local player object ID");
            Check.Equal(0u, ReadUInt32(snapshot, 8), "equip snapshot bag operation marker");
            Check.Equal((ushort)3, ReadUInt16(snapshot, 12), "equip snapshot source page");
            Check.Equal((ushort)23, ReadUInt16(snapshot, 14), "equip snapshot source index");
            Check.Equal(1000u, ReadUInt32(snapshot, 20), "equip snapshot describes the equipped sword");
            Check.Equal((byte)0, snapshot[46], "equip move snapshot uses captured zero bound flag");
            Check.Equal((byte)0, snapshot[47], "equip move snapshot uses captured zero stack flag");

            var sourceSlotRefresh = PacketBuilder.KitBagSlotIndex(equipped, 95);
            Check.Equal(40, sourceSlotRefresh.Length, "equipped source-slot refresh length");
            Check.Equal((ushort)10056, ReadUInt16(sourceSlotRefresh, 2), "equipped source-slot refresh opcode");
            Check.Equal(3u, ReadUInt32(sourceSlotRefresh, 12), "equipped source-slot refresh page");
            Check.Equal(23u, ReadUInt32(sourceSlotRefresh, 16), "equipped source-slot refresh index");
            Check.Equal(-1, ReadInt32(sourceSlotRefresh, 20), "equipped source slot is explicitly cleared");

            var afterDeletingFirstPotion = await store.DeleteKitBagItemAsync(
                owner.Id,
                character.Id,
                kitBagSlot: 0)
                ?? throw new InvalidOperationException("first starter potion could not be deleted");
            Check.Equal(0u, KitBagSlots.GetItemId(afterDeletingFirstPotion.KitBag, 0), "slot zero is open before shield unequip");

            var rejectedMissingDestination = await store.MoveEquipmentToKitBagAsync(
                owner.Id,
                character.Id,
                EquipmentSlots.Shield,
                kitBagSlot: -1)
                ?? throw new InvalidOperationException("missing-destination unequip guard did not return the character");
            Check.Equal(
                2000u,
                EquipmentSlots.GetItemId(
                    rejectedMissingDestination.Equipment,
                    rejectedMissingDestination.Profession,
                    EquipmentSlots.Shield),
                "unequip without a valid drop destination leaves the shield equipped");

            var shieldUnequipped = await store.MoveEquipmentToKitBagAsync(
                owner.Id,
                character.Id,
                EquipmentSlots.Shield,
                kitBagSlot: 0)
                ?? throw new InvalidOperationException("starter shield could not be moved into the bag");
            Check.Equal(2000u, KitBagSlots.GetItemId(shieldUnequipped.KitBag, 0), "non-weapon gear uses its exact requested empty bag slot");
            Check.Equal(
                0u,
                EquipmentSlots.GetItemId(shieldUnequipped.Equipment, shieldUnequipped.Profession, EquipmentSlots.Shield),
                "shield equipment slot is cleared after exact-slot unequip");

            var rejectedShieldTarget = await store.MoveKitBagToEquipmentAsync(
                owner.Id,
                character.Id,
                kitBagSlot: 0,
                requestedEquipmentSlot: EquipmentSlots.Armor);
            Check.True(rejectedShieldTarget is null, "explicit drag rejects shield-to-armor placement");
            var afterRejectedShieldTarget = await store.GetFirstCharacterAsync(owner.Id)
                ?? throw new InvalidOperationException("explicit shield-target guard fixture was not reloaded");
            Check.Equal(2000u, KitBagSlots.GetItemId(afterRejectedShieldTarget.KitBag, 0), "rejected explicit target leaves shield in its bag slot");
            Check.Equal(
                2100u,
                EquipmentSlots.GetItemId(
                    afterRejectedShieldTarget.Equipment,
                    afterRejectedShieldTarget.Profession,
                    EquipmentSlots.Armor),
                "rejected explicit target does not displace armor");

            var shieldEquipped = await store.MoveKitBagToEquipmentAsync(
                owner.Id,
                character.Id,
                kitBagSlot: 0,
                requestedEquipmentSlot: EquipmentSlots.Shield)
                ?? throw new InvalidOperationException("starter shield could not be equipped again");
            Check.Equal(
                2000u,
                EquipmentSlots.GetItemId(shieldEquipped.Equipment, shieldEquipped.Profession, EquipmentSlots.Shield),
                "explicit drag equips non-weapon gear in its compatible slot");
            Check.Equal(0u, KitBagSlots.GetItemId(shieldEquipped.KitBag, 0), "shield source slot clears after re-equip");

            var duplicateBefore = KitBagSlots.SetSlot(
                GameDefaults.EmptyKitBag,
                0,
                EquipmentSlots.GetEntry(
                    GameDefaults.DefaultEquipment(profession: 0),
                    profession: 0,
                    EquipmentSlots.Shield));
            var duplicateAfter = KitBagSlots.SetSlot(
                duplicateBefore,
                1,
                EquipmentSlots.GetEntry(
                    GameDefaults.DefaultEquipment(profession: 0),
                    profession: 0,
                    EquipmentSlots.Shield));
            Check.Equal(
                1,
                GameClientHandler.ResolveMovedKitBagDestination(
                    duplicateBefore,
                    duplicateAfter,
                    EquipmentSlots.GetEntry(
                        GameDefaults.DefaultEquipment(profession: 0),
                        profession: 0,
                        EquipmentSlots.Shield)),
                "unequip acknowledgement resolves the newly changed slot when an identical item already exists earlier in the bag");

            var fullBagEntry = CompactItemEntry.Parse(
                KitBagSlots.GetEntry(GameDefaults.StarterKitBag, 0)).ToCompactString();
            var fullBag = string.Join('#', Enumerable.Repeat(fullBagEntry, 96)) + '#';
            var fullBagOwner = await store.LoginOrCreateAccountAsync("equipment-full-bag-owner", "");
            var fullBagCharacter = await store.CreateCharacterAsync(
                fullBagOwner.Id,
                new GameCharacter
                {
                    Name = "EquipmentFullBagHero",
                    Profession = 0,
                    KitBag = fullBag
                });
            var afterFullBagUnequip = await store.MoveEquipmentToKitBagAsync(
                fullBagOwner.Id,
                fullBagCharacter.Id,
                EquipmentSlots.Weapon,
                kitBagSlot: 12)
                ?? throw new InvalidOperationException("full-bag unequip guard did not return the character");
            Check.Equal(fullBag, afterFullBagUnequip.KitBag, "full bag is unchanged when no unequip destination exists");
            Check.Equal(
                1000u,
                EquipmentSlots.GetItemId(
                    afterFullBagUnequip.Equipment,
                    afterFullBagUnequip.Profession,
                    EquipmentSlots.Weapon),
                "full bag leaves the weapon equipped instead of losing it");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
