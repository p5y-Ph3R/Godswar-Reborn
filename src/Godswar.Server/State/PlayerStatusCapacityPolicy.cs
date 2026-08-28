namespace Godswar.Server.State;

/// <summary>
/// Selects only client presentation slots. Every authoritative source remains
/// in its owner and every aggregate was already composed over the full active
/// set before this policy runs.
/// </summary>
internal static class PlayerStatusCapacityPolicy
{
    public static PlayerStatusSnapshot Apply(PlayerStatusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var candidates = snapshot.Presentations
            .GroupBy(static presentation =>
                presentation.Effect.StatusId)
            .Select(static group => group
                .OrderBy(static presentation =>
                    presentation.PresentationClass)
                .ThenByDescending(static presentation =>
                    presentation.Priority)
                .ThenByDescending(static presentation =>
                    presentation.Effect.RemainingSeconds)
                .First())
            .OrderBy(static presentation =>
                presentation.PresentationClass)
            .ThenByDescending(static presentation =>
                presentation.Priority)
            .ThenBy(static presentation =>
                presentation.Effect.StatusId)
            .ToArray();
        var admitted = new List<ClientStatusPresentation>(
            Math.Min(
                candidates.Length,
                PlayerStatusComposer.MaximumTotalStatuses));
        var beneficial = 0;
        foreach (var candidate in candidates)
        {
            if (admitted.Count >=
                PlayerStatusComposer.MaximumTotalStatuses)
            {
                break;
            }
            if (candidate.Beneficial &&
                beneficial >=
                    PlayerStatusComposer.MaximumBeneficialStatuses)
            {
                continue;
            }

            admitted.Add(candidate);
            if (candidate.Beneficial)
            {
                beneficial++;
            }
        }

        var effects = admitted
            .Select(static presentation => presentation.Effect)
            .OrderBy(static effect => effect.StatusId)
            .ToArray();
        return snapshot with
        {
            Effects = effects,
            // Capacity is presentation-only. Retain every source candidate so
            // another overlay can be merged and selected without losing an
            // authoritative layer that was hidden by an earlier pass.
            Presentations = snapshot.Presentations
        };
    }
}
