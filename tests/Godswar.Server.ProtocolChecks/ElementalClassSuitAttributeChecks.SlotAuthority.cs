using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ElementalClassSuitAttributeChecks
{
    private static void CheckEquippedRoleAuthority()
    {
        var valid = ElementalAttributeCatalog.CalculateEquippedProfile(
            [At(EquipmentSlots.Weapon, GearForSlot(
                EquipmentSlots.Weapon,
                ElementKind.Fire,
                grade: 25))]);
        var wrongRole = ElementalAttributeCatalog.CalculateEquippedProfile(
            [At(EquipmentSlots.Weapon, ElementalGear(481, 25))]);

        Check.True(
            valid.CountFor(ElementKind.Fire) == 1 &&
            valid.EffectsFor(ElementKind.Fire)
                .EffectPotencyBasisPoints == 1_000,
            "the equipped weapon accepts its potency-family attribute");
        Check.True(
            wrongRole.CountFor(ElementKind.Fire) == 0 &&
            wrongRole.EffectsFor(ElementKind.Fire) == default,
            "an off-family elemental attribute fails closed for its slot");

        var compactSlots = Enumerable.Range(
                EquipmentSlots.Head,
                EquipmentSlots.Shield + 1)
            .Select(slot => slot == EquipmentSlots.Weapon
                ? GearForSlot(slot, ElementKind.Fire, 25).ToCompactString()
                : string.Empty);
        var positional = new GameCharacter
        {
            Equipment = string.Join('#', compactSlots)
        };
        Check.Equal(
            1,
            positional.ElementalEquipment.CountFor(ElementKind.Fire),
            "empty compact slots do not collapse the authoritative slot index");
    }

    private static IEnumerable<ElementalEquippedItem>
        EquippedElementalGear(int count, short grade) =>
        Enumerable.Range(EquipmentSlots.Head, count)
            .Select(slot => At(
                slot,
                GearForSlot(slot, ElementKind.Fire, grade)));

    private static void CheckFireEffectsForSlots(
        ElementalEffectTotals effects,
        int count)
    {
        var roles = Enumerable.Range(EquipmentSlots.Head, count)
            .Select(slot =>
            {
                ElementalAttributeCatalog.TryGetRoleForEquipmentSlot(
                    slot,
                    out var role);
                return role;
            })
            .ToArray();
        Check.True(
            effects.EffectPotencyBasisPoints ==
                roles.Count(static value =>
                    value == ElementalAttributeRole.EffectPotency) * 40 &&
            effects.EffectResistanceBasisPoints ==
                roles.Count(static value =>
                    value == ElementalAttributeRole.EffectResistance) * 40 &&
            effects.ApplicationChanceBasisPoints ==
                roles.Count(static value =>
                    value == ElementalAttributeRole.ApplicationChance) * 20,
            $"Fire totals follow equipped-slot roles at {count} items");
    }

    private static ElementalEquippedItem At(
        int slot,
        CompactItemEntry item) => new(slot, item);

    private static CompactItemEntry GearForSlot(
        int slot,
        ElementKind element,
        short grade)
    {
        ElementalAttributeCatalog.TryGetFamilyForEquipmentSlot(
            slot,
            out var family);
        var attributeId = ElementalAttributeCatalog.MinimumAttributeId +
            ((int)element * 3) +
            (int)family;
        return ElementalGear(attributeId, grade) with
        {
            Id = TierThreeItemIdForSlot(slot)
        };
    }

    private static uint TierThreeItemIdForSlot(int slot) => slot switch
    {
        EquipmentSlots.Head => 2333,
        EquipmentSlots.Amulet => 3133,
        EquipmentSlots.Glove => 2833,
        EquipmentSlots.Armor => 2133,
        EquipmentSlots.Cuff => 2633,
        EquipmentSlots.Girdle => 3033,
        EquipmentSlots.Shoes => 2933,
        EquipmentSlots.Leggings => 2733,
        EquipmentSlots.Ring1 or EquipmentSlots.Ring2 => 3232,
        EquipmentSlots.Weapon => 1034,
        EquipmentSlots.Shield => 2033,
        _ => throw new ArgumentOutOfRangeException(nameof(slot))
    };
}
