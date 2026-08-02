using Godswar.Server.Application.Inventory;

namespace Godswar.Server.Infrastructure.Inventory;

internal sealed partial class PostgresHolySuitCommandExecutor
{
    private static void ValidateStoredExperiencePlan(
        HolySuitCommand command,
        LockedCharacter character,
        DailyUsage daily,
        HolySuitPlan plan)
    {
        if (command.Operation != HolySuitCommandOperation.StoreExperience)
        {
            if (plan.StoredExperience != 0)
            {
                throw new InvalidDataException(
                    "A non-Store Holy Suit plan recorded stored EXP.");
            }
            return;
        }

        var mutation = plan.Mutations.Single();
        var characterDelta = checked(
            character.Experience - plan.CharacterExperienceAfter);
        var dailyDelta = checked(
            plan.DailyStoredExperienceAfter - daily.StoredExperience);
        var boxDelta = checked(
            (long)mutation.After.Exp - mutation.Before.Exp);
        if (plan.StoredExperience <= 0 ||
            mutation.Role != HolySuitReceiptItemRole.HolyBox ||
            mutation.Before.IsEmpty ||
            mutation.After.IsEmpty ||
            mutation.Before.Id != mutation.After.Id ||
            characterDelta != plan.StoredExperience ||
            dailyDelta != plan.StoredExperience ||
            boxDelta != plan.StoredExperience ||
            command.ExperienceToStore > 0 &&
                command.ExperienceToStore != plan.StoredExperience)
        {
            throw new InvalidDataException(
                "The Store EXP plan contains inconsistent resolved deltas.");
        }
    }
}
