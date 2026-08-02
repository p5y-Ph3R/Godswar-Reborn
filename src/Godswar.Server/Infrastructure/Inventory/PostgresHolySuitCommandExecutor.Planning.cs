using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Items;
using Godswar.Server.State;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolySuitCommandExecutor
{
    private static HolySuitPlan CreatePlan(
        HolySuitCommand command,
        LockedCharacter character,
        LockedKitBag bag,
        DailyUsage daily,
        bool battlePass,
        IHolySuitContentCatalog catalog,
        IItemTemplateCatalog templates)
    {
        var policy = catalog.OperationPolicy ??
            throw new InvalidDataException(
                "The pinned Holy Suit policy is unavailable.");
        if (character.Level < policy.MinimumPlayerLevel)
        {
            return Reject(
                HolySuitCommandResultStatus.LevelRequirementNotMet,
                character,
                daily);
        }

        return command.Operation switch
        {
            HolySuitCommandOperation.StoreExperience =>
                PlanStore(command, character, bag, daily, battlePass,
                    catalog, policy),
            HolySuitCommandOperation.TransferExperience =>
                PlanTransfer(command, character, bag, daily, catalog,
                    templates, policy),
            HolySuitCommandOperation.ConsumeWare =>
                PlanWare(command, character, bag, daily, catalog,
                    templates, policy),
            HolySuitCommandOperation.TransformExperience =>
                PlanTransform(command, character, bag, daily, catalog,
                    policy),
            _ => throw new InvalidDataException(
                "The Holy Suit operation is unsupported.")
        };
    }

    private static HolySuitPlan PlanTransfer(
        HolySuitCommand command,
        LockedCharacter character,
        LockedKitBag bag,
        DailyUsage daily,
        IHolySuitContentCatalog catalog,
        IItemTemplateCatalog templates,
        HolySuitOperationPolicy policy)
    {
        var gearResult = ResolveSelection(
            bag,
            command.PrimaryKitBagSlot,
            command.ExpectedPrimaryCompactItemState,
            primary: true);
        if (gearResult.Status.HasValue)
        {
            return Reject(gearResult.Status.Value, character, daily);
        }
        var boxResult = ResolveSelection(
            bag,
            command.SecondaryKitBagSlot,
            command.ExpectedSecondaryCompactItemState,
            primary: false);
        if (boxResult.Status.HasValue)
        {
            return Reject(boxResult.Status.Value, character, daily);
        }

        var gear = gearResult.Item!;
        var box = boxResult.Item!;
        var gearStatus = EquipmentStatus(gear.Item, templates, policy);
        if (gearStatus.HasValue)
        {
            return Reject(gearStatus.Value, character, daily);
        }
        if (IsMaximumSuit(gear.Item))
        {
            return Reject(
                HolySuitCommandResultStatus.MaximumHolySuit,
                character,
                daily);
        }
        if (!catalog.TryGetConsumable(box.Item.Id, out var boxDefinition) ||
            boxDefinition.Role != HolySuitConsumableRole.HolyBox)
        {
            return Reject(
                HolySuitCommandResultStatus.SecondItemNotHolyBox,
                character,
                daily);
        }
        if (box.Item.Exp <= 0)
        {
            return Reject(
                HolySuitCommandResultStatus.HolyBoxEmpty,
                character,
                daily);
        }
        if (gear.Item.Exp < 0 || checked((long)gear.Item.Exp + box.Item.Exp) >
            policy.GearExperienceCapacity)
        {
            return Reject(
                HolySuitCommandResultStatus.EquipmentExperienceLimitReached,
                character,
                daily);
        }

        return Commit(
            HolySuitCommandResultStatus.ExperienceTransferred,
            [
                new PlannedMutation(
                    HolySuitReceiptItemRole.Equipment,
                    gear.Slot,
                    gear,
                    gear.Item,
                    gear.Item with
                    {
                        Bound = 1,
                        Exp = checked(gear.Item.Exp + box.Item.Exp)
                    }),
                new PlannedMutation(
                    HolySuitReceiptItemRole.HolyBox,
                    box.Slot,
                    box,
                    box.Item,
                    CompactItemEntry.Empty)
            ],
            character.Experience,
            daily.StoredExperience);
    }

    private static HolySuitPlan PlanWare(
        HolySuitCommand command,
        LockedCharacter character,
        LockedKitBag bag,
        DailyUsage daily,
        IHolySuitContentCatalog catalog,
        IItemTemplateCatalog templates,
        HolySuitOperationPolicy policy)
    {
        var gearResult = ResolveSelection(
            bag,
            command.PrimaryKitBagSlot,
            command.ExpectedPrimaryCompactItemState,
            primary: true);
        if (gearResult.Status.HasValue)
        {
            return Reject(gearResult.Status.Value, character, daily);
        }
        var wareResult = ResolveSelection(
            bag,
            command.SecondaryKitBagSlot,
            command.ExpectedSecondaryCompactItemState,
            primary: false);
        if (wareResult.Status.HasValue)
        {
            return Reject(wareResult.Status.Value, character, daily);
        }

        var gear = gearResult.Item!;
        var ware = wareResult.Item!;
        var gearStatus = EquipmentStatus(gear.Item, templates, policy);
        if (gearStatus.HasValue)
        {
            return Reject(gearStatus.Value, character, daily);
        }
        if (!TryReadSuitState(gear.Item, out var currentType,
            out var currentLevel) ||
            !catalog.TryGetUpgrade(currentType, currentLevel,
                out var upgrade))
        {
            return Reject(
                HolySuitCommandResultStatus.MaximumHolySuit,
                character,
                daily);
        }
        if (!catalog.TryGetConsumable(ware.Item.Id, out var wareDefinition) ||
            wareDefinition.Role != HolySuitConsumableRole.Ware)
        {
            return Reject(
                HolySuitCommandResultStatus.WareNotRequired,
                character,
                daily);
        }
        if (ware.Item.Id != upgrade.WareItemId)
        {
            return Reject(
                HolySuitCommandResultStatus.WareTypeMismatch,
                character,
                daily);
        }
        if (ware.Item.Stack < upgrade.WareQuantity)
        {
            return Reject(
                HolySuitCommandResultStatus.InsufficientWares,
                character,
                daily);
        }
        if (gear.Item.Exp < upgrade.RequiredItemExperience)
        {
            return Reject(
                HolySuitCommandResultStatus.EquipmentInsufficientExperience,
                character,
                daily);
        }

        var mutations = new List<PlannedMutation>
        {
            new(
                HolySuitReceiptItemRole.Equipment,
                gear.Slot,
                gear,
                gear.Item,
                gear.Item with
                {
                    Bound = 1,
                    Exp = checked(gear.Item.Exp -
                        checked((int)upgrade.RequiredItemExperience)),
                    HolySuitCode = checked(
                        upgrade.TargetSuitType * 100 +
                        upgrade.TargetLevel)
                }),
            ConsumeStack(
                HolySuitReceiptItemRole.Ware,
                ware,
                upgrade.WareQuantity)
        };

        if (upgrade.RequiredPrisms > 0)
        {
            var prism = catalog.Consumables.Single(static value =>
                value.Role == HolySuitConsumableRole.ExperiencePrism);
            var prismStacks = bag.Items.Values
                .Where(value => value.Item.Id == prism.ItemId)
                .OrderBy(static value => value.Slot)
                .ToArray();
            if (prismStacks.Sum(static value => value.Item.Stack) <
                upgrade.RequiredPrisms)
            {
                return Reject(
                    HolySuitCommandResultStatus.InsufficientPrisms,
                    character,
                    daily);
            }

            var remaining = upgrade.RequiredPrisms;
            foreach (var stack in prismStacks)
            {
                if (remaining == 0)
                {
                    break;
                }
                var consumed = Math.Min(remaining, stack.Item.Stack);
                mutations.Add(ConsumeStack(
                    HolySuitReceiptItemRole.ExperiencePrism,
                    stack,
                    consumed));
                remaining -= consumed;
            }
        }

        return Commit(
            HolySuitCommandResultStatus.WareConsumed,
            mutations,
            character.Experience,
            daily.StoredExperience,
            prismsConsumed: upgrade.RequiredPrisms);
    }

    private static HolySuitPlan PlanTransform(
        HolySuitCommand command,
        LockedCharacter character,
        LockedKitBag bag,
        DailyUsage daily,
        IHolySuitContentCatalog catalog,
        HolySuitOperationPolicy policy)
    {
        var cost = checked(
            (long)command.PrismsToCreate * policy.ExperiencePrismCost);
        if (cost > character.Experience)
        {
            return Reject(
                HolySuitCommandResultStatus.InsufficientCharacterExperience,
                character,
                daily);
        }

        var prism = catalog.Consumables.Single(static value =>
            value.Role == HolySuitConsumableRole.ExperiencePrism);
        var remaining = command.PrismsToCreate;
        var mutations = new List<PlannedMutation>();
        foreach (var stack in bag.Items.Values
            .Where(value => value.Item.Id == prism.ItemId &&
                value.Item.Bound == prism.GrantedBound &&
                value.Item.Stack < prism.StackCap)
            .OrderBy(static value => value.Slot))
        {
            var added = Math.Min(remaining, prism.StackCap - stack.Item.Stack);
            mutations.Add(new PlannedMutation(
                HolySuitReceiptItemRole.ExperiencePrism,
                stack.Slot,
                stack,
                stack.Item,
                stack.Item with
                {
                    Stack = checked((short)(stack.Item.Stack + added))
                }));
            remaining -= added;
            if (remaining == 0)
            {
                break;
            }
        }

        foreach (var slot in bag.EmptySlots)
        {
            if (remaining == 0)
            {
                break;
            }
            var added = Math.Min(remaining, prism.StackCap);
            var addedItem = CompactItemEntry.Empty with
            {
                Id = prism.ItemId,
                Quality = 1,
                Grade = 1,
                Bound = prism.GrantedBound,
                Stack = checked((short)added)
            };
            mutations.Add(new PlannedMutation(
                HolySuitReceiptItemRole.ExperiencePrism,
                slot,
                Existing: null,
                CompactItemEntry.Empty,
                addedItem));
            remaining -= added;
        }

        if (remaining != 0)
        {
            return Reject(
                HolySuitCommandResultStatus.BagFull,
                character,
                daily);
        }

        return Commit(
            HolySuitCommandResultStatus.ExperienceTransformed,
            mutations,
            checked(character.Experience - cost),
            daily.StoredExperience,
            prismsCreated: command.PrismsToCreate);
    }

    private static (LockedInventoryItem? Item,
        HolySuitCommandResultStatus? Status) ResolveSelection(
        LockedKitBag bag,
        int slot,
        string expectedState,
        bool primary)
    {
        if (!bag.Items.TryGetValue(checked((short)slot), out var item))
        {
            return (null, primary
                ? HolySuitCommandResultStatus.PrimaryItemMissing
                : HolySuitCommandResultStatus.SecondaryItemMissing);
        }
        var expected = CompactItemEntry.Parse(expectedState);
        return expected == item.Item
            ? (item, null)
            : (null, primary
                ? HolySuitCommandResultStatus.StalePrimaryItem
                : HolySuitCommandResultStatus.StaleSecondaryItem);
    }

    private static HolySuitCommandResultStatus? EquipmentStatus(
        CompactItemEntry item,
        IItemTemplateCatalog templates,
        HolySuitOperationPolicy policy)
    {
        if (!templates.TryGet(item.Id, out var template) ||
            template.EquipmentSlot is < 0 or > 11)
        {
            return HolySuitCommandResultStatus.TargetNotEquipment;
        }
        return !template.MinLevel.HasValue ||
            template.MinLevel.Value < policy.MinimumGearLevel
            ? HolySuitCommandResultStatus.LevelRequirementNotMet
            : null;
    }

    private static bool TryReadSuitState(
        CompactItemEntry item,
        out short suitType,
        out short suitLevel)
    {
        suitType = 0;
        suitLevel = 0;
        if (item.HolySuitCode == 0)
        {
            return true;
        }
        suitType = checked((short)(item.HolySuitCode / 100));
        suitLevel = checked((short)(item.HolySuitCode % 100));
        return suitType is >= 1 and <= 7 &&
            suitLevel is >= 1 and <= 10 &&
            item.HolySuitCode == suitType * 100 + suitLevel;
    }

    private static bool IsMaximumSuit(CompactItemEntry item) =>
        TryReadSuitState(item, out var suitType, out var suitLevel) &&
        suitType == 7 && suitLevel == 10;

    private static PlannedMutation ConsumeStack(
        HolySuitReceiptItemRole role,
        LockedInventoryItem item,
        int quantity)
    {
        if (quantity <= 0 || quantity > item.Item.Stack)
        {
            throw new InvalidDataException(
                "The planned Holy Suit stack consumption is invalid.");
        }
        var after = quantity == item.Item.Stack
            ? CompactItemEntry.Empty
            : item.Item with
            {
                Stack = checked((short)(item.Item.Stack - quantity))
            };
        return new PlannedMutation(
            role,
            item.Slot,
            item,
            item.Item,
            after);
    }

    private static HolySuitPlan Reject(
        HolySuitCommandResultStatus status,
        LockedCharacter character,
        DailyUsage daily) =>
        new(status, [], character.Experience, daily.StoredExperience, 0, 0, 0);

    private static HolySuitPlan Commit(
        HolySuitCommandResultStatus status,
        IReadOnlyList<PlannedMutation> mutations,
        long characterExperienceAfter,
        long dailyStoredExperienceAfter,
        int prismsCreated = 0,
        int prismsConsumed = 0,
        long storedExperience = 0) =>
        new(
            status,
            mutations,
            characterExperienceAfter,
            dailyStoredExperienceAfter,
            prismsCreated,
            prismsConsumed,
            storedExperience);
}
