namespace Godswar.Server.Application.WorldInstances;

internal readonly record struct MedusaCompletionRewardAward(
    int HardPoints,
    MedusaEncounterTitleAward? Title,
    string NotificationText)
{
    public uint AwardedTitleId => Title is { } title
        ? MedusaTitleAwardPolicy.GetClientTitleId(title.Title)
        : 0;
}

/// <summary>
/// Live reward policy for a run that has authoritatively completed by
/// defeating both final bosses. The score remains the points actually earned;
/// it is not rewritten to 3,000 merely to choose the documented time reward.
/// </summary>
internal static class MedusaCompletionRewardPolicy
{
    public static bool SupportsSettlement(
        MedusaEncounterDifficulty difficulty) =>
        MedusaRewardPolicyCatalog.Current.SupportsDifficulty(difficulty);

    public static bool TryResolve(
        MedusaEncounterDifficulty difficulty,
        int finalScore,
        TimeSpan elapsed,
        out MedusaCompletionRewardAward award)
    {
        if (finalScore < 0 ||
            elapsed < TimeSpan.Zero ||
            elapsed >= MedusaIslandPolicy.TimeLimit ||
            !SupportsSettlement(difficulty))
        {
            award = default;
            return false;
        }

        if (!MedusaRewardPolicyCatalog.Current.TryResolveCompleted(
                difficulty,
                finalScore,
                elapsed,
                out var hardPoints,
                out var title))
        {
            award = default;
            return false;
        }

        award = new(
            hardPoints,
            title,
            title is { } selected
                ? $"The team has defeated Medusa within " +
                  $"{selected.MaximumCompletionTime.TotalMinutes:0} " +
                  $"minutes and earned the title of " +
                  $"'{selected.DisplayName}'."
                : "The team has successfully killed Medusa.");
        return true;
    }
}
