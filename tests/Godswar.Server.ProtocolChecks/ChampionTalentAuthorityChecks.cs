using System.Globalization;
using System.Text.Json;
using Godswar.Server.Application.World;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class ChampionTalentAuthorityChecks
{
    public const string CheckName = "Champion talent server-authority scalars";

    private const decimal TooltipScale = 2.6m;

    private static readonly IReadOnlyDictionary<int, decimal> Stock =
        new Dictionary<int, decimal>
        {
            [50] = 3m,
            [51] = 10m,
            [52] = 9m,
            [53] = 50m,
            [54] = 2m,
            [55] = 0.005m,
            [56] = 5m,
            [57] = 16m,
            [58] = 4m,
            [59] = 7m,
            [60] = 3m,
            [61] = 0.01m,
            [62] = 20m,
            [63] = 1.6m,
            [64] = 4m,
            [65] = 1.2m,
            [66] = 7m,
            [67] = 90m,
            [68] = 90m
        };

    public static Task RunAsync()
    {
        var champion = SkillTalentSeeds.Talents
            .Where(static talent => talent.ClassId == 1)
            .ToDictionary(static talent => talent.Id);
        Check.Equal(Stock.Count, champion.Count,
            "the Champion catalog contains exactly talents 50-68");
        foreach (var expected in Stock)
        {
            Check.True(champion.TryGetValue(expected.Key, out var talent),
                $"Champion talent {expected.Key} is present");
            Check.Equal(expected.Value, talent.EffectValue,
                $"Champion talent {expected.Key} keeps its stock server scalar");
            using var stats = JsonDocument.Parse(talent.StatsJson);
            Check.Equal(
                $"{talent.EffectId},{Format(expected.Value)}",
                stats.RootElement.GetProperty(talent.EffectType).GetString() ??
                    string.Empty,
                $"Champion talent {expected.Key} raw effect matches its scalar");
        }

        AssertSuccessorChangesOnlyChampionAuthority();
        AssertStandaloneSummaryScriptsUseEffectiveRank();
        return Task.CompletedTask;
    }

    private static void AssertSuccessorChangesOnlyChampionAuthority()
    {
        var corrected = GameplayContentTestFixtures.Published;
        var inflated = corrected with
        {
            Talents = corrected.Talents.Select(InflateChampion).ToArray()
        };
        var successor = PostgresGameplayContentPublisher
            .CreateChampionTalentAuthoritySuccessor(inflated);
        var changes = inflated.Talents.Zip(successor.Talents)
            .Where(static pair => pair.First != pair.Second)
            .ToArray();
        Check.Equal(Stock.Count, changes.Length,
            "the immutable successor changes exactly 19 talents");
        foreach (var pair in changes)
        {
            Check.True(
                pair.First.ClassId == 1 && Stock.ContainsKey(pair.First.Id),
                "every immutable successor delta is a reviewed Champion talent");
            Check.Equal(Stock[pair.Second.Id], pair.Second.EffectValue,
                $"successor talent {pair.Second.Id} restores its stock scalar");
            Check.Equal(
                pair.First with
                {
                    EffectValue = pair.Second.EffectValue,
                    StatsJson = pair.Second.StatsJson
                },
                pair.Second,
                $"successor talent {pair.Second.Id} changes no other field");
        }
    }

    private static GameplayTalentDefinition InflateChampion(
        GameplayTalentDefinition talent)
    {
        if (talent.ClassId != 1 || !Stock.TryGetValue(talent.Id, out var stock))
        {
            return talent;
        }

        var tooltip = stock * TooltipScale;
        return talent with
        {
            EffectValue = tooltip,
            StatsJson = talent.StatsJson.Replace(
                $"\"{talent.EffectId},{Format(stock)}\"",
                $"\"{talent.EffectId},{Format(tooltip)}\"",
                StringComparison.Ordinal)
        };
    }

    private static string Format(decimal value) =>
        value.ToString("G29", CultureInfo.InvariantCulture);

    private static void AssertStandaloneSummaryScriptsUseEffectiveRank()
    {
        var root = FindRepositoryRoot();
        var aggregate = File.ReadAllText(Path.Combine(
            root,
            "database",
            "postgres",
            "041_character_stat_summary.sql"));
        var detail = File.ReadAllText(Path.Combine(
            root,
            "database",
            "postgres",
            "042_character_talent_stat_summary.sql"));
        Check.True(
            aggregate.Contains(
                "talent_effective_rank(rank) AS rank",
                StringComparison.Ordinal),
            "standalone aggregate diagnostics apply progressive talent rank");
        Check.True(
            detail.Contains(
                "ROUND(talent_effective_rank(ct.rank) * CASE",
                StringComparison.Ordinal),
            "standalone talent diagnostics apply progressive talent rank");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "GodswarServer.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Godswar repository root not found.");
    }
}
