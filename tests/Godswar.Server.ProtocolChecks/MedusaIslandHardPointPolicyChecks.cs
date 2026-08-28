using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaIslandHardPointPolicyChecks
{
    public const string CheckName =
        "Medusa Island documented HardPoint reward markers";

    public static Task RunAsync()
    {
        CheckSchedule(
            MedusaHardPointSchedule.Normal,
            incompleteScores: [0, 950, 1_200, 1_500, 1_700, 1_900, 2_200],
            incompleteHardPoints: [300, 375, 450, 525, 600, 675, 750],
            completionMinutes: [40, 30, 25, 20, 15, 10],
            completionHardPoints: [975, 1_050, 1_125, 1_200, 1_275, 1_350]);
        CheckSchedule(
            MedusaHardPointSchedule.Enhanced,
            incompleteScores: [0, 950, 1_200, 1_500, 1_700, 1_900, 2_200],
            incompleteHardPoints: [300, 600, 750, 900, 1_050, 1_200, 1_350],
            completionMinutes: [40, 30, 25, 20, 15, 10],
            completionHardPoints: [1_800, 1_950, 2_025, 2_100, 2_175, 2_250]);
        CheckAdvancedTitles();
        CheckExactEvidenceOnly();
        return Task.CompletedTask;
    }

    private static void CheckAdvancedTitles()
    {
        Check.Equal(
            2,
            MedusaIslandPolicy.CompletionTitleEvidence.Count,
            "both conflicting title evidence schedules are retained");
        CheckTitleSchedule(
            MedusaCompletionTitleEvidenceSource.GodsArenaGuideRevision6,
            [
                MedusaCompletionTitle.MedusaExecutioners,
                MedusaCompletionTitle.MedusaSlayers,
                MedusaCompletionTitle.MedusaChallengers
            ]);
        CheckTitleSchedule(
            MedusaCompletionTitleEvidenceSource.StockClientLua,
            [
                MedusaCompletionTitle.MedusaChallengers,
                MedusaCompletionTitle.MedusaSlayers,
                MedusaCompletionTitle.MedusaExecutioners
            ]);
        Check.True(
            !MedusaIslandPolicy.TryResolveAuthoritativeCompletionTitle(
                MedusaIslandVariant.Advanced,
                MedusaIslandPolicy.VictoryScore,
                TimeSpan.FromMinutes(20),
                out _) &&
            !MedusaIslandPolicy.TryResolveAuthoritativeCompletionTitle(
                MedusaIslandVariant.Advanced,
                MedusaIslandPolicy.VictoryScore,
                TimeSpan.FromMinutes(15),
                out _),
            "conflicting title schedules cannot settle even their shared row");
    }

    private static void CheckTitleSchedule(
        MedusaCompletionTitleEvidenceSource source,
        MedusaCompletionTitle[] titles)
    {
        var schedule = MedusaIslandPolicy.CompletionTitleEvidence.Single(
            candidate => candidate.Source == source);
        Check.True(
            schedule.Markers.Select(marker =>
                    checked((int)marker.CompletionTime.TotalMinutes))
                .SequenceEqual([20, 15, 10]) &&
            schedule.Markers.Select(marker => marker.Title)
                .SequenceEqual(titles) &&
            schedule.Markers.Select(marker => marker.DisplayName)
                .SequenceEqual(titles.Select(TitleDisplayName)) &&
            schedule.Markers.All(marker =>
                marker.Variant == MedusaIslandVariant.Advanced &&
                marker.Score == MedusaIslandPolicy.VictoryScore),
            $"{source} title evidence is retained without reconciliation");
    }

    private static string TitleDisplayName(MedusaCompletionTitle title) =>
        title switch
        {
            MedusaCompletionTitle.MedusaChallengers =>
                "Medusa Challengers",
            MedusaCompletionTitle.MedusaSlayers => "Medusa Slayers",
            MedusaCompletionTitle.MedusaExecutioners =>
                "Medusa Executioners",
            _ => throw new ArgumentOutOfRangeException(nameof(title))
        };

    private static void CheckSchedule(
        MedusaHardPointSchedule schedule,
        int[] incompleteScores,
        int[] incompleteHardPoints,
        int[] completionMinutes,
        int[] completionHardPoints)
    {
        var definition = MedusaIslandPolicy.HardPointSchedules.Single(
            candidate => candidate.Schedule == schedule);
        Check.True(
            definition.IncompleteMarkers.Select(marker => marker.Score)
                .SequenceEqual(incompleteScores) &&
            definition.IncompleteMarkers.Select(marker => marker.HardPoints)
                .SequenceEqual(incompleteHardPoints),
            $"{schedule} retains every documented incomplete score marker");
        Check.True(
            definition.CompletedMarkers.All(marker =>
                marker.Score == MedusaIslandPolicy.VictoryScore) &&
            definition.CompletedMarkers.Select(marker =>
                    checked((int)marker.CompletionTime.TotalMinutes))
                .SequenceEqual(completionMinutes) &&
            definition.CompletedMarkers.Select(marker => marker.HardPoints)
                .SequenceEqual(completionHardPoints),
            $"{schedule} retains every documented 3,000-point time marker");

        for (var index = 0; index < incompleteScores.Length; index++)
        {
            Check.True(
                MedusaIslandPolicy.TryGetDocumentedIncompleteHardPoints(
                    schedule,
                    incompleteScores[index],
                    out var hardPoints) &&
                hardPoints == incompleteHardPoints[index],
                $"{schedule} exact incomplete marker {index} resolves");
        }
        for (var index = 0; index < completionMinutes.Length; index++)
        {
            Check.True(
                MedusaIslandPolicy.TryGetDocumentedCompletedHardPoints(
                    schedule,
                    MedusaIslandPolicy.VictoryScore,
                    TimeSpan.FromMinutes(completionMinutes[index]),
                    out var hardPoints) &&
                hardPoints == completionHardPoints[index],
                $"{schedule} exact completion marker {index} resolves");
        }
    }

    private static void CheckExactEvidenceOnly()
    {
        Check.True(
            !MedusaIslandPolicy.TryGetDocumentedIncompleteHardPoints(
                MedusaHardPointSchedule.Normal,
                1_000,
                out _) &&
            !MedusaIslandPolicy.TryGetDocumentedCompletedHardPoints(
                MedusaHardPointSchedule.Normal,
                2_999,
                TimeSpan.FromMinutes(20),
                out _) &&
            !MedusaIslandPolicy.TryGetDocumentedCompletedHardPoints(
                MedusaHardPointSchedule.Normal,
                3_000,
                TimeSpan.FromMinutes(22),
                out _),
            "unpublished score and time values require explicit resolution");
        Check.True(
            !MedusaIslandPolicy.TryGetDocumentedIncompleteHardPoints(
                (MedusaHardPointSchedule)byte.MaxValue,
                0,
                out _) &&
            !MedusaIslandPolicy.TryGetDocumentedCompletedHardPoints(
                (MedusaHardPointSchedule)byte.MaxValue,
                3_000,
                TimeSpan.FromMinutes(40),
                out _),
            "unknown schedules cannot borrow a published reward table");
    }
}
