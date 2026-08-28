using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaCompletionRewardPolicyChecks
{
    public const string CheckName =
        "Medusa live completion rewards";

    public static Task RunAsync()
    {
        Check.True(
            MedusaCompletionRewardPolicy.TryResolve(
                MedusaEncounterDifficulty.Enhanced,
                MedusaIslandPolicy.VictoryScore,
                TimeSpan.FromMinutes(10),
                out var tenMinute) &&
            tenMinute.HardPoints == 2_250 &&
            tenMinute.Title?.Title ==
                MedusaEncounterTitle.MedusaChallengers &&
            tenMinute.NotificationText.Contains(
                "Medusa Challengers",
                StringComparison.Ordinal),
            "an Enhanced 3,000-point ten-minute completion grants 2,250 HardPoints and Medusa Challengers");

        Check.True(
            MedusaCompletionRewardPolicy.TryResolve(
                MedusaEncounterDifficulty.Enhanced,
                MedusaIslandPolicy.VictoryScore,
                TimeSpan.FromMinutes(10).Add(TimeSpan.FromTicks(1)),
                out var afterTen) &&
            afterTen.HardPoints == 2_175 &&
            afterTen.Title?.Title == MedusaEncounterTitle.MedusaSlayers,
            "crossing the inclusive ten-minute boundary selects the next reward and title");

        Check.True(
            MedusaCompletionRewardPolicy.TryResolve(
                MedusaEncounterDifficulty.Normal,
                MedusaIslandPolicy.VictoryScore,
                TimeSpan.FromMinutes(9),
                out var normal) &&
            normal.HardPoints == 1_350 &&
            normal.Title is null,
            "a Normal completion receives its documented HardPoint schedule without an Enhanced title");

        var completedWithActualScore = new MedusaCompletionRewardRequest(
            WorldInstanceId.New(),
            RealmId.Tempest,
            MedusaEncounterDifficulty.Enhanced,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(9),
            finalScore: 2_997,
            characterIds: [17]);
        Check.True(
            completedWithActualScore.FinalScore == 2_997 &&
            completedWithActualScore.Award.HardPoints == 2_250 &&
            completedWithActualScore.Award.Title is null,
            "sub-threshold boss completion keeps its time reward and actual score without granting a 3,000-point title");

        var externalScoreCompletion = new MedusaCompletionRewardRequest(
            WorldInstanceId.New(),
            RealmId.Tempest,
            MedusaEncounterDifficulty.Enhanced,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(9),
            finalScore: 3_802,
            characterIds: [18]);
        Check.True(
            externalScoreCompletion.FinalScore == 3_802 &&
            externalScoreCompletion.Award.HardPoints == 2_250 &&
            externalScoreCompletion.Award.Title?.Title ==
                MedusaEncounterTitle.MedusaChallengers,
            "external-style scores above 3,000 retain their value and title eligibility");

        Check.True(
            MedusaCompletionRewardPolicy.TryResolve(
                MedusaEncounterDifficulty.Enhanced,
                MedusaIslandPolicy.VictoryScore,
                TimeSpan.FromMinutes(20),
                out var twentyMinute) &&
            twentyMinute.Title?.Title ==
                MedusaEncounterTitle.MedusaExecutioners &&
            MedusaCompletionRewardPolicy.TryResolve(
                MedusaEncounterDifficulty.Enhanced,
                MedusaIslandPolicy.VictoryScore,
                TimeSpan.FromMinutes(25),
                out var twentyFiveMinute) &&
            twentyFiveMinute.HardPoints == 2_025 &&
            twentyFiveMinute.Title is null,
            "Enhanced awards Executioners through 20 minutes and no title at the 25-minute reward tier");

        Check.True(
            MedusaCompletionRewardPolicy.SupportsSettlement(
                MedusaEncounterDifficulty.Mythic) &&
            MedusaCompletionRewardPolicy.TryResolve(
                MedusaEncounterDifficulty.Mythic,
                MedusaIslandPolicy.VictoryScore,
                TimeSpan.FromMinutes(9),
                out var mythic) &&
            mythic.HardPoints == 3_375 &&
            mythic.Title?.Title == MedusaEncounterTitle.HeirOfPerseus &&
            mythic.AwardedTitleId == 5152 &&
            !MedusaCompletionRewardPolicy.TryResolve(
                MedusaEncounterDifficulty.Enhanced,
                MedusaIslandPolicy.VictoryScore,
                MedusaIslandPolicy.TimeLimit,
                out _),
            "Mythic settles its database-seeded 3,375 Honor and title, while timed-out runs fail closed");
        CheckDatabaseSnapshotControlsLiveAward();
        return Task.CompletedTask;
    }

    private static void CheckDatabaseSnapshotControlsLiveAward()
    {
        var baseline = MedusaRewardPolicyTestFixture.Create();
        var titles = baseline.Titles.Select(definition =>
            definition.EncounterTitle == MedusaEncounterTitle.HeirOfPerseus
                ? definition with
                {
                    DisplayName = "Configured Heir",
                    Attributes = new(650, 650, 650, 650)
                }
                : definition).ToArray();
        var rules = baseline.Rules.Select(rule =>
            rule.Difficulty == MedusaEncounterDifficulty.Mythic &&
            rule.Kind == MedusaRewardRuleKind.CompletedTime &&
            rule.Threshold == 600
                ? rule with { HonorPoints = 3_450 }
                : rule).ToArray();
        MedusaRewardPolicyCatalog.Install(new(titles, rules));
        try
        {
            Check.True(
                MedusaCompletionRewardPolicy.TryResolve(
                    MedusaEncounterDifficulty.Mythic,
                    MedusaIslandPolicy.VictoryScore,
                    TimeSpan.FromMinutes(9),
                    out var configured) &&
                configured.HardPoints == 3_450 &&
                configured.Title?.DisplayName == "Configured Heir" &&
                MedusaTitleAwardPolicy.Titles.Single(definition =>
                        definition.EncounterTitle ==
                            MedusaEncounterTitle.HeirOfPerseus)
                    .Attributes.StrengthBasisPoints == 650,
                "the installed database snapshot, not compiled reward values, controls points, title text, and attributes");
        }
        finally
        {
            MedusaRewardPolicyCatalog.Install(baseline);
        }
    }
}
