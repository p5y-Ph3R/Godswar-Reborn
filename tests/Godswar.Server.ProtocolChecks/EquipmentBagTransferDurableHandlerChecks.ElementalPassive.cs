using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class EquipmentBagTransferDurableHandlerChecks
{
    private static void CheckGaiaPassiveRefreshAfterDurableEquip()
    {
        var persistedEquipment = GameDefaults.DefaultEquipment(profession: 0);
        persistedEquipment = EquipmentSlots.SetSlot(
            persistedEquipment,
            profession: 0,
            EquipmentSlots.Weapon,
            ElementalTierThreeGear(1034, 489).ToCompactString());
        persistedEquipment = EquipmentSlots.SetSlot(
            persistedEquipment,
            profession: 0,
            EquipmentSlots.Head,
            ElementalTierThreeGear(2333, 491).ToCompactString());
        persistedEquipment = EquipmentSlots.SetSlot(
            persistedEquipment,
            profession: 0,
            EquipmentSlots.Armor,
            ElementalTierThreeGear(2133, 490).ToCompactString());

        var live = new GameCharacter
        {
            Id = 51,
            AccountId = 61,
            Profession = 0,
            MaxHp = 1_000,
            CurrentHp = 1_000,
            Equipment = GameDefaults.DefaultEquipment(profession: 0)
        };
        var persisted = new GameCharacter
        {
            Id = live.Id,
            AccountId = live.AccountId,
            Profession = live.Profession,
            Equipment = persistedEquipment,
            CalculatedStats = new CharacterStats
            {
                CharacterId = live.Id,
                AccountId = live.AccountId,
                MaxHp = 1_000,
                CurrentHp = 1_000,
                MaxMp = 100,
                CurrentMp = 100
            }
        };

        GameClientHandler.ApplyDurableEquipmentBagTransferProjection(
            live,
            persisted);

        Check.True(
            live.CalculatedStats!.MaxHp == 1_000 &&
            live.MaxHp == 1_080 &&
            live.CurrentHp == 1_000,
            "durable equipment refresh applies Gaia once over base MaxHP");

        GameClientHandler.ApplyDurableEquipmentBagTransferProjection(
            live,
            persisted);
        Check.Equal(
            1_080,
            live.MaxHp,
            "replaying the durable projection does not double-add Gaia");
    }

    private static CompactItemEntry ElementalTierThreeGear(
        uint itemId,
        int elementalAttribute) =>
        CompactItemEntry.Empty with
        {
            Id = itemId,
            Quality = 20,
            Grade = 25,
            Bound = 1,
            Stack = 1,
            ElementalAttribute1 = elementalAttribute
        };
}
