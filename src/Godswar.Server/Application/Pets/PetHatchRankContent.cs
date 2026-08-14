namespace Godswar.Server.Application.Pets;

/// <summary>
/// One weighted rank outcome for an aptitude in an immutable pet-content
/// revision. Outcome order is explicit so the database never relies on row
/// ordering to define the roll intervals.
/// </summary>
internal sealed record PetHatchRankStepContentDefinition(
    short Aptitude,
    short OutcomeOrder,
    decimal Rank,
    short Weight);

/// <summary>
/// Reproducible evidence for a hatch-rank decision. Roll is in the closed
/// interval 0..99 and can therefore be retained safely in an audit receipt.
/// </summary>
internal sealed record PetHatchRankRoll(
    decimal Rank,
    short OutcomeOrder,
    short Roll);

internal sealed record PetHatchRankEvidence(
    decimal Rank,
    short OutcomeOrder,
    short Roll,
    string ContentRevision)
{
    public bool IsValid =>
        PetRankWirePolicy.IsRepresentable(Rank) &&
        OutcomeOrder is >= 0 and <
            PetHatchRankContentPolicy.OutcomesPerAptitude &&
        Roll is >= PetHatchRankContentPolicy.MinimumRoll and <=
            PetHatchRankContentPolicy.MaximumRoll &&
        ContentRevision is { Length: 64 } &&
        ContentRevision.All(static value =>
            value is >= '0' and <= '9' or >= 'A' and <= 'F');

    public static PetHatchRankEvidence Create(
        PetHatchRankRoll roll,
        string contentRevision)
    {
        ArgumentNullException.ThrowIfNull(roll);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRevision);
        var evidence = new PetHatchRankEvidence(
            roll.Rank,
            roll.OutcomeOrder,
            roll.Roll,
            contentRevision);
        return evidence.IsValid
            ? evidence
            : throw new ArgumentException(
                "Pet hatch-rank evidence is invalid.");
    }
}

internal static class PetHatchRankContentPolicy
{
    public const short TotalWeight = 100;
    public const short MinimumRoll = 0;
    public const short MaximumRoll = TotalWeight - 1;
    public const int OutcomesPerAptitude = 3;

    public static void Validate(
        IReadOnlyCollection<short> aptitudeIds,
        IReadOnlyList<PetHatchRankStepContentDefinition> steps)
    {
        ArgumentNullException.ThrowIfNull(aptitudeIds);
        ArgumentNullException.ThrowIfNull(steps);

        if (aptitudeIds.Count == 0 ||
            aptitudeIds.Distinct().Count() != aptitudeIds.Count ||
            steps.Count != aptitudeIds.Count * OutcomesPerAptitude)
        {
            throw new InvalidOperationException(
                "Published pet hatch-rank steps are incomplete.");
        }

        var knownAptitudes = aptitudeIds.ToHashSet();
        foreach (var group in steps.GroupBy(static value => value.Aptitude))
        {
            var ordered = group
                .OrderBy(static value => value.OutcomeOrder)
                .ToArray();
            if (!knownAptitudes.Contains(group.Key) ||
                ordered.Length != OutcomesPerAptitude ||
                !ordered.Select(static value => (int)value.OutcomeOrder)
                    .SequenceEqual(Enumerable.Range(0, OutcomesPerAptitude)) ||
                ordered.Any(static value =>
                    !PetRankWirePolicy.IsRepresentable(value.Rank) ||
                    value.Weight <= 0) ||
                ordered.Select(static value => value.Rank).Distinct().Count() !=
                    OutcomesPerAptitude ||
                ordered.Zip(ordered.Skip(1), static (left, right) =>
                        right.Rank > left.Rank)
                    .Any(static increasing => !increasing) ||
                ordered.Sum(static value => (int)value.Weight) != TotalWeight)
            {
                throw new InvalidOperationException(
                    $"Published pet hatch-rank steps for aptitude {group.Key} are invalid.");
            }
        }

        if (steps.Select(static value => value.Aptitude).Distinct().Count() !=
            knownAptitudes.Count)
        {
            throw new InvalidOperationException(
                "Published pet hatch-rank steps omit an aptitude.");
        }
    }

    public static PetHatchRankRoll Roll(
        IReadOnlyList<PetHatchRankStepContentDefinition> steps,
        short aptitude,
        int roll)
    {
        ArgumentNullException.ThrowIfNull(steps);
        if (roll is < MinimumRoll or > MaximumRoll)
        {
            throw new ArgumentOutOfRangeException(
                nameof(roll),
                roll,
                $"Hatch-rank roll must be between {MinimumRoll} and {MaximumRoll}.");
        }

        var cumulativeWeight = 0;
        foreach (var step in steps
                     .Where(value => value.Aptitude == aptitude)
                     .OrderBy(static value => value.OutcomeOrder))
        {
            cumulativeWeight = checked(cumulativeWeight + step.Weight);
            if (roll < cumulativeWeight)
            {
                return new PetHatchRankRoll(
                    step.Rank,
                    step.OutcomeOrder,
                    checked((short)roll));
            }
        }

        throw new ArgumentOutOfRangeException(
            nameof(aptitude),
            aptitude,
            "No published hatch-rank policy exists for this aptitude.");
    }
}
