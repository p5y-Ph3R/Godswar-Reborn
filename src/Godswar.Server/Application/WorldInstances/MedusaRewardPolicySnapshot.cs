using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Application.WorldInstances;

internal enum MedusaRewardRuleKind : byte
{
    IncompleteScore = 1,
    CompletedTime = 2
}

/// <summary>
/// One database-authored Medusa completion reward rule. Incomplete thresholds
/// are minimum scores; completed thresholds are inclusive maximum seconds.
/// </summary>
internal readonly record struct MedusaRewardRule(
    MedusaEncounterDifficulty Difficulty,
    MedusaRewardRuleKind Kind,
    int Threshold,
    int HonorPoints,
    MedusaEncounterTitle? Title);

/// <summary>
/// Immutable startup snapshot of the database-owned Medusa reward tables.
/// Changing the tables takes effect after the game server is restarted.
/// </summary>
internal sealed class MedusaRewardPolicySnapshot
{
    private const int MaximumRuleCount = 100;
    private readonly IReadOnlyList<MedusaTitleDefinition> _titles;
    private readonly IReadOnlyList<MedusaRewardRule> _rules;
    private readonly IReadOnlyList<MedusaEncounterTitleAward>
        _completionTitles;

    public MedusaRewardPolicySnapshot(
        IReadOnlyCollection<MedusaTitleDefinition> titles,
        IReadOnlyCollection<MedusaRewardRule> rules)
    {
        ArgumentNullException.ThrowIfNull(titles);
        ArgumentNullException.ThrowIfNull(rules);
        var frozenTitles = titles
            .OrderBy(static definition => definition.EncounterTitle)
            .ToArray();
        var frozenRules = rules
            .OrderBy(static rule => rule.Difficulty)
            .ThenBy(static rule => rule.Kind)
            .ThenBy(static rule => rule.Threshold)
            .ToArray();
        Validate(frozenTitles, frozenRules);

        _titles = Array.AsReadOnly(frozenTitles);
        _rules = Array.AsReadOnly(frozenRules);
        _completionTitles = Array.AsReadOnly(
            frozenRules
                .Where(static rule =>
                    rule.Kind == MedusaRewardRuleKind.CompletedTime &&
                    rule.Title is not null)
                .Select(rule =>
                {
                    var title = rule.Title!.Value;
                    var definition = frozenTitles.Single(candidate =>
                        candidate.EncounterTitle == title);
                    return new MedusaEncounterTitleAward(
                        rule.Difficulty,
                        TimeSpan.FromSeconds(rule.Threshold),
                        title,
                        definition.DisplayName);
                })
                .ToArray());
        Sha256 = ComputeSha256(frozenTitles, frozenRules);
    }

    public IReadOnlyList<MedusaTitleDefinition> Titles => _titles;

    public IReadOnlyList<MedusaRewardRule> Rules => _rules;

    public IReadOnlyList<MedusaEncounterTitleAward> CompletionTitles =>
        _completionTitles;

    public string Sha256 { get; }

    public bool SupportsDifficulty(MedusaEncounterDifficulty difficulty) =>
        _rules.Any(rule => rule.Difficulty == difficulty);

    public bool TryGetTitle(
        MedusaEncounterTitle title,
        out MedusaTitleDefinition definition)
    {
        foreach (var candidate in _titles)
        {
            if (candidate.EncounterTitle == title)
            {
                definition = candidate;
                return true;
            }
        }

        definition = default;
        return false;
    }

    public bool TryResolve(
        MedusaEncounterDifficulty difficulty,
        int finalScore,
        TimeSpan elapsed,
        out int honorPoints,
        out MedusaEncounterTitleAward? title)
    {
        if (!SupportsDifficulty(difficulty) ||
            finalScore < 0 ||
            elapsed < TimeSpan.Zero ||
            elapsed >= MedusaIslandPolicy.TimeLimit)
        {
            honorPoints = default;
            title = default;
            return false;
        }

        MedusaRewardRule? selected;
        if (MedusaIslandPolicy.HasVictoryScore(finalScore))
        {
            selected = _rules
                .Where(rule =>
                    rule.Difficulty == difficulty &&
                    rule.Kind == MedusaRewardRuleKind.CompletedTime &&
                    elapsed <= TimeSpan.FromSeconds(rule.Threshold))
                .OrderBy(static rule => rule.Threshold)
                .Cast<MedusaRewardRule?>()
                .FirstOrDefault();
        }
        else
        {
            selected = _rules
                .Where(rule =>
                    rule.Difficulty == difficulty &&
                    rule.Kind == MedusaRewardRuleKind.IncompleteScore &&
                    finalScore >= rule.Threshold)
                .OrderByDescending(static rule => rule.Threshold)
                .Cast<MedusaRewardRule?>()
                .FirstOrDefault();
        }

        if (selected is not { } rule)
        {
            honorPoints = default;
            title = default;
            return false;
        }

        honorPoints = rule.HonorPoints;
        title = null;
        if (MedusaIslandPolicy.HasVictoryScore(finalScore) &&
            rule.Title is { } selectedTitle)
        {
            var definition = _titles.Single(candidate =>
                candidate.EncounterTitle == selectedTitle);
            title = new(
                difficulty,
                TimeSpan.FromSeconds(rule.Threshold),
                selectedTitle,
                definition.DisplayName);
        }
        return true;
    }

    public bool TryResolveCompleted(
        MedusaEncounterDifficulty difficulty,
        int finalScore,
        TimeSpan elapsed,
        out int honorPoints,
        out MedusaEncounterTitleAward? title)
    {
        if (!SupportsDifficulty(difficulty) ||
            finalScore < 0 ||
            elapsed < TimeSpan.Zero ||
            elapsed >= MedusaIslandPolicy.TimeLimit)
        {
            honorPoints = default;
            title = default;
            return false;
        }

        var selected = _rules
            .Where(rule =>
                rule.Difficulty == difficulty &&
                rule.Kind == MedusaRewardRuleKind.CompletedTime &&
                elapsed <= TimeSpan.FromSeconds(rule.Threshold))
            .OrderBy(static rule => rule.Threshold)
            .Cast<MedusaRewardRule?>()
            .FirstOrDefault();
        if (selected is not { } rule)
        {
            honorPoints = default;
            title = default;
            return false;
        }

        honorPoints = rule.HonorPoints;
        title = null;
        if (MedusaIslandPolicy.HasVictoryScore(finalScore) &&
            rule.Title is { } selectedTitle)
        {
            var definition = _titles.Single(candidate =>
                candidate.EncounterTitle == selectedTitle);
            title = new(
                difficulty,
                TimeSpan.FromSeconds(rule.Threshold),
                selectedTitle,
                definition.DisplayName);
        }
        return true;
    }

    private static void Validate(
        IReadOnlyList<MedusaTitleDefinition> titles,
        IReadOnlyList<MedusaRewardRule> rules)
    {
        var authoredTitles = Enum.GetValues<MedusaEncounterTitle>();
        if (titles.Count != authoredTitles.Length ||
            rules.Count is < 1 or > MaximumRuleCount ||
            titles.Select(static title => title.EncounterTitle)
                .Distinct().Count() != titles.Count ||
            titles.Select(static title => title.SemanticKey)
                .Distinct().Count() != titles.Count ||
            titles.Select(static title => title.ClientTitleId)
                .Distinct().Count() != titles.Count)
        {
            throw new InvalidDataException(
                "Medusa reward title definitions are incomplete or duplicated.");
        }

        foreach (var title in titles)
        {
            if (!Enum.IsDefined(title.EncounterTitle) ||
                title.SemanticKey.Value != ExpectedSemanticKey(
                    title.EncounterTitle) ||
                string.IsNullOrWhiteSpace(title.DisplayName) ||
                title.DisplayName.Length > 80 ||
                title.ClientTitleId == 0 ||
                !title.Attributes.IsValid)
            {
                throw new InvalidDataException(
                    "A Medusa reward title definition is invalid.");
            }
        }

        if (rules.Select(static rule =>
                (rule.Difficulty, rule.Kind, rule.Threshold))
            .Distinct().Count() != rules.Count)
        {
            throw new InvalidDataException(
                "Medusa reward rules contain a duplicate threshold.");
        }

        foreach (var rule in rules)
        {
            var validThreshold = rule.Kind switch
            {
                MedusaRewardRuleKind.IncompleteScore =>
                    rule.Threshold is >= 0 and <
                        MedusaIslandPolicy.VictoryScore &&
                    rule.Title is null,
                MedusaRewardRuleKind.CompletedTime =>
                    rule.Threshold is > 0 and <= 2_400,
                _ => false
            };
            if (!Enum.IsDefined(rule.Difficulty) ||
                !validThreshold ||
                rule.HonorPoints <= 0 ||
                rule.Title is { } title &&
                    !titles.Any(candidate =>
                        candidate.EncounterTitle == title))
            {
                throw new InvalidDataException(
                    "A Medusa completion reward rule is invalid.");
            }
        }

        foreach (var difficulty in
                 Enum.GetValues<MedusaEncounterDifficulty>())
        {
            var incomplete = rules.Where(rule =>
                    rule.Difficulty == difficulty &&
                    rule.Kind == MedusaRewardRuleKind.IncompleteScore)
                .OrderBy(static rule => rule.Threshold)
                .ToArray();
            var completed = rules.Where(rule =>
                    rule.Difficulty == difficulty &&
                    rule.Kind == MedusaRewardRuleKind.CompletedTime)
                .OrderBy(static rule => rule.Threshold)
                .ToArray();
            if (incomplete.Length == 0 || incomplete[0].Threshold != 0 ||
                completed.Length == 0 || completed[^1].Threshold != 2_400 ||
                !IsNondecreasing(incomplete.Select(static rule =>
                    rule.HonorPoints)) ||
                !IsNonincreasing(completed.Select(static rule =>
                    rule.HonorPoints)))
            {
                throw new InvalidDataException(
                    $"Medusa reward rules do not fully cover {difficulty}.");
            }
        }
    }

    private static bool IsNondecreasing(IEnumerable<int> values)
    {
        var previous = int.MinValue;
        foreach (var value in values)
        {
            if (value < previous)
            {
                return false;
            }
            previous = value;
        }
        return true;
    }

    private static bool IsNonincreasing(IEnumerable<int> values)
    {
        var previous = int.MaxValue;
        foreach (var value in values)
        {
            if (value > previous)
            {
                return false;
            }
            previous = value;
        }
        return true;
    }

    private static string ExpectedSemanticKey(
        MedusaEncounterTitle title) => title switch
        {
            MedusaEncounterTitle.MedusaChallengers =>
                MedusaTitleAwardPolicy.ChallengersKey,
            MedusaEncounterTitle.MedusaSlayers =>
                MedusaTitleAwardPolicy.SlayersKey,
            MedusaEncounterTitle.MedusaExecutioners =>
                MedusaTitleAwardPolicy.ExecutionersKey,
            MedusaEncounterTitle.GorgonBreaker =>
                MedusaTitleAwardPolicy.GorgonBreakerKey,
            MedusaEncounterTitle.BaneOfTheThreeSisters =>
                MedusaTitleAwardPolicy.BaneOfTheThreeSistersKey,
            MedusaEncounterTitle.HeirOfPerseus =>
                MedusaTitleAwardPolicy.HeirOfPerseusKey,
            _ => throw new ArgumentOutOfRangeException(nameof(title))
        };

    private static string ComputeSha256(
        IEnumerable<MedusaTitleDefinition> titles,
        IEnumerable<MedusaRewardRule> rules)
    {
        var text = new StringBuilder("medusa-reward-policy-v1\n");
        foreach (var title in titles)
        {
            text.AppendFormat(
                CultureInfo.InvariantCulture,
                "title:{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}\n",
                (byte)title.EncounterTitle,
                title.SemanticKey.Value,
                title.DisplayName,
                title.ClientTitleId,
                title.Attributes.PhysicalAttackBasisPoints,
                title.Attributes.MagicAttackBasisPoints,
                title.Attributes.PhysicalDefenseBasisPoints,
                title.Attributes.MagicDefenseBasisPoints);
        }
        foreach (var rule in rules)
        {
            text.AppendFormat(
                CultureInfo.InvariantCulture,
                "rule:{0}|{1}|{2}|{3}|{4}\n",
                (byte)rule.Difficulty,
                (byte)rule.Kind,
                rule.Threshold,
                rule.HonorPoints,
                rule.Title is { } title ? (byte)title : 0);
        }
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(text.ToString())));
    }
}

internal static class MedusaRewardPolicyCatalog
{
    private static MedusaRewardPolicySnapshot? _current;

    public static MedusaRewardPolicySnapshot Current =>
        Volatile.Read(ref _current) ?? throw new InvalidOperationException(
            "The database-owned Medusa reward policy has not been loaded.");

    public static void Install(MedusaRewardPolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }
}
