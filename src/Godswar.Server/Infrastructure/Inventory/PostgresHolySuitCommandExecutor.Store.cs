using Godswar.Server.Application.Inventory;
using Godswar.Server.Application.Items;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolySuitCommandExecutor
{
    private static HolySuitPlan PlanStore(
        HolySuitCommand command,
        LockedCharacter character,
        LockedKitBag bag,
        DailyUsage daily,
        bool battlePass,
        IHolySuitContentCatalog catalog,
        HolySuitOperationPolicy policy)
    {
        var automaticMaximum = command.ExperienceToStore == 0;
        var selection = ResolveSelection(
            bag,
            command.PrimaryKitBagSlot,
            command.ExpectedPrimaryCompactItemState,
            primary: true);
        if (selection.Status.HasValue)
        {
            return Reject(selection.Status.Value, character, daily);
        }

        var box = selection.Item!;
        if (!catalog.TryGetConsumable(box.Item.Id, out var consumable) ||
            consumable.Role != HolySuitConsumableRole.HolyBox)
        {
            return Reject(
                HolySuitCommandResultStatus.NotHolyBox,
                character,
                daily);
        }

        if (box.Item.Exp < 0)
        {
            return Reject(
                HolySuitCommandResultStatus.HolyBoxFull,
                character,
                daily);
        }
        var boxRemaining = checked(
            (long)consumable.ExperienceCapacity - box.Item.Exp);
        if (boxRemaining <= 0)
        {
            return Reject(
                HolySuitCommandResultStatus.HolyBoxFull,
                character,
                daily);
        }
        if (!automaticMaximum && command.ExperienceToStore > boxRemaining)
        {
            return Reject(
                HolySuitCommandResultStatus.HolyBoxFull,
                character,
                daily);
        }
        if (!automaticMaximum &&
            command.ExperienceToStore > policy.PerOperationExperienceMaximum)
        {
            return Reject(
                HolySuitCommandResultStatus.RequestedExperienceLimitExceeded,
                character,
                daily);
        }
        if (character.Experience <= 0)
        {
            return Reject(
                HolySuitCommandResultStatus.InsufficientCharacterExperience,
                character,
                daily);
        }

        var dailyLimit = policy.ResolveDailyExperienceLimit(character.Level);
        var dailyRemaining = battlePass
            ? long.MaxValue
            : Math.Max(0, dailyLimit - daily.StoredExperience);
        if (automaticMaximum && dailyRemaining == 0)
        {
            return Reject(
                HolySuitCommandResultStatus.DailyStoreLimitExceeded,
                character,
                daily);
        }

        var amount = automaticMaximum
            ? Minimum(
                policy.PerOperationExperienceMaximum,
                boxRemaining,
                character.Experience,
                dailyRemaining)
            : command.ExperienceToStore;
        if (amount > character.Experience)
        {
            return Reject(
                HolySuitCommandResultStatus.InsufficientCharacterExperience,
                character,
                daily);
        }
        if (amount > dailyRemaining)
        {
            return Reject(
                HolySuitCommandResultStatus.DailyStoreLimitExceeded,
                character,
                daily);
        }
        if (amount <= 0)
        {
            throw new InvalidDataException(
                "The Holy Suit Store Maximum policy resolved no EXP.");
        }

        var dailyAfter = checked(daily.StoredExperience + amount);
        var updated = box.Item with
        {
            Bound = consumable.GrantedBound,
            Exp = checked(box.Item.Exp + checked((int)amount))
        };
        return Commit(
            HolySuitCommandResultStatus.ExperienceStored,
            [new PlannedMutation(
                HolySuitReceiptItemRole.HolyBox,
                box.Slot,
                box,
                box.Item,
                updated)],
            checked(character.Experience - amount),
            dailyAfter,
            storedExperience: amount);
    }

    private static long Minimum(
        long first,
        long second,
        long third,
        long fourth) =>
        Math.Min(Math.Min(first, second), Math.Min(third, fourth));
}
