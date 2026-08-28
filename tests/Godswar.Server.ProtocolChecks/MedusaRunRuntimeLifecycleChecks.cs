using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaRunRuntimeLifecycleChecks
{
    public const string CheckName =
        "Medusa one-time score, deadline, abandonment, and title runtime";

    public static Task RunAsync()
    {
        CheckDefeatFencesAndOneTimeScore();
        CheckFinalBossPairCompletesWithCurrentScore();
        CheckExactCompletionAndTerminalFence();
        CheckBestOnlyTitles();
        CheckMonotonicAuthoritativeClock();
        CheckRejectedIdentitiesCannotAdvanceClock();
        CheckTimeoutBoundaries();
        CheckWholeRunAbandonment();
        return Task.CompletedTask;
    }

    private static void CheckFinalBossPairCompletesWithCurrentScore()
    {
        var runtime = MedusaRunRuntimeCheckFixture.Create();
        var spawns = runtime.Snapshot().Spawns;
        var medusa = spawns.Single(spawn =>
            spawn.Role == MedusaEncounterEnemyRole.Medusa);
        var stheno = spawns.Single(spawn =>
            spawn.Role == MedusaEncounterEnemyRole.Stheno);

        var firstBoss = runtime.ClaimDefeat(
            101,
            medusa.ObjectId,
            medusa.SpawnGeneration,
            runtime.StartedAt.AddSeconds(1));
        var finalBoss = runtime.ClaimDefeat(
            101,
            stheno.ObjectId,
            stheno.SpawnGeneration,
            runtime.StartedAt.AddSeconds(2));
        var completed = runtime.Snapshot();

        Check.True(
            firstBoss.Outcome == MedusaDefeatClaimOutcome.Applied,
            "the first final boss does not complete the run");
        Check.True(
            finalBoss.Outcome == MedusaDefeatClaimOutcome.Completed,
            "the second final boss completes the run");
        Check.True(
            completed.State == MedusaRunState.Completed,
            "the final-boss pair terminalizes the run");
        Check.Equal(2_100, completed.TeamScore,
            "the final-boss pair retains its external score values");
        Check.Equal(
            2,
            completed.Spawns.Count(spawn => spawn.Defeated),
            "only the defeated final-boss pair is marked defeated");
        Check.True(
            completed.CompletionMarker is
            {
                FinalScore: 2_100,
                SelectedTitle: null
            },
            "partial-score completion freezes its score without a title");
    }

    private static void CheckDefeatFencesAndOneTimeScore()
    {
        var runtime = MedusaRunRuntimeCheckFixture.Create();
        var first = runtime.Snapshot().Spawns[0];
        var at = runtime.StartedAt.AddSeconds(5);

        var nonMember = runtime.ClaimDefeat(
            999,
            first.ObjectId,
            first.SpawnGeneration,
            at);
        var unknown = runtime.ClaimDefeat(
            101,
            uint.MaxValue,
            first.SpawnGeneration,
            at);
        var stale = runtime.ClaimDefeat(
            101,
            first.ObjectId,
            checked(first.SpawnGeneration + 1),
            at);
        Check.True(
            nonMember.Outcome ==
                MedusaDefeatClaimOutcome.CharacterNotAdmitted &&
            unknown.Outcome == MedusaDefeatClaimOutcome.UnknownSpawn &&
            stale.Outcome ==
                MedusaDefeatClaimOutcome.StaleSpawnGeneration &&
            runtime.Snapshot().TeamScore == 0,
            "foreign players, objects, and generations never award score");

        var applied = runtime.ClaimDefeat(
            101,
            first.ObjectId,
            first.SpawnGeneration,
            at);
        var duplicate = runtime.ClaimDefeat(
            102,
            first.ObjectId,
            first.SpawnGeneration,
            at);
        var staleAfterDefeat = runtime.ClaimDefeat(
            102,
            first.ObjectId,
            checked(first.SpawnGeneration + 1),
            at);
        Check.True(
            applied.Outcome == MedusaDefeatClaimOutcome.Applied &&
            applied.ScoreAwarded == first.ScoreValue &&
            duplicate.Outcome ==
                MedusaDefeatClaimOutcome.DuplicateDefeat &&
            duplicate.ScoreAwarded == 0 &&
            staleAfterDefeat.Outcome ==
                MedusaDefeatClaimOutcome.StaleSpawnGeneration &&
            runtime.Snapshot().TeamScore == first.ScoreValue,
            "a fixed generation scores once and cannot respawn or replay");
    }

    private static void CheckExactCompletionAndTerminalFence()
    {
        var runtime = MedusaRunRuntimeCheckFixture.Create();
        var marker = MedusaRunRuntimeCheckFixture.Complete(
            runtime,
            runtime.StartedAt.AddMinutes(12));
        var snapshot = runtime.Snapshot();

        Check.True(
            snapshot.State == MedusaRunState.Completed &&
            snapshot.TeamScore == 3_802 &&
            snapshot.Spawns.All(spawn => spawn.Defeated) &&
            marker.FinalScore == 3_802 &&
            marker.CompletedAt == runtime.StartedAt.AddMinutes(12),
            "all 136 one-time claims retain the captured 3,802 points");

        var first = snapshot.Spawns[0];
        var afterCompletion = runtime.ClaimDefeat(
            101,
            first.ObjectId,
            first.SpawnGeneration,
            runtime.StartedAt.AddMinutes(13));
        Check.True(
            afterCompletion.Outcome ==
                MedusaDefeatClaimOutcome.RunNotActive &&
            afterCompletion.ScoreAwarded == 0 &&
            runtime.Snapshot().TeamScore == 3_802,
            "completed score is retained and terminally fenced");
    }

    private static void CheckBestOnlyTitles()
    {
        CheckTitle(
            MedusaEncounterDifficulty.Enhanced,
            TimeSpan.FromMinutes(10),
            MedusaEncounterTitle.MedusaChallengers);
        CheckTitle(
            MedusaEncounterDifficulty.Enhanced,
            TimeSpan.FromMinutes(10).Add(TimeSpan.FromTicks(1)),
            MedusaEncounterTitle.MedusaSlayers);
        CheckTitle(
            MedusaEncounterDifficulty.Enhanced,
            TimeSpan.FromMinutes(15),
            MedusaEncounterTitle.MedusaSlayers);
        CheckTitle(
            MedusaEncounterDifficulty.Enhanced,
            TimeSpan.FromMinutes(20),
            MedusaEncounterTitle.MedusaExecutioners);
        CheckTitle(
            MedusaEncounterDifficulty.Mythic,
            TimeSpan.FromMinutes(10),
            MedusaEncounterTitle.HeirOfPerseus);
        CheckTitle(
            MedusaEncounterDifficulty.Mythic,
            TimeSpan.FromMinutes(15),
            MedusaEncounterTitle.BaneOfTheThreeSisters);
        CheckTitle(
            MedusaEncounterDifficulty.Mythic,
            TimeSpan.FromMinutes(20),
            MedusaEncounterTitle.GorgonBreaker);

        var normal = MedusaRunRuntimeCheckFixture.Create(
            MedusaEncounterDifficulty.Normal);
        var normalMarker = MedusaRunRuntimeCheckFixture.Complete(
            normal,
            normal.StartedAt.AddMinutes(5));
        var lateEnhanced = MedusaRunRuntimeCheckFixture.Create();
        var lateMarker = MedusaRunRuntimeCheckFixture.Complete(
            lateEnhanced,
            lateEnhanced.StartedAt.AddMinutes(20).AddTicks(1));
        Check.True(
            normalMarker.SelectedTitle is null &&
            lateMarker.SelectedTitle is null,
            "Normal and Enhanced completions outside title thresholds retain no title marker");
    }

    private static void CheckMonotonicAuthoritativeClock()
    {
        var offsetStart = new DateTimeOffset(
            2026,
            8,
            23,
            1,
            0,
            0,
            TimeSpan.FromHours(13));
        var runtime = MedusaRunRuntimeCheckFixture.Create(
            startedAt: offsetStart);
        Check.True(
            runtime.StartedAt.Offset == TimeSpan.Zero &&
            runtime.StartedAt == offsetStart.ToUniversalTime(),
            "authoritative run timestamps are normalized to UTC");

        var first = runtime.Snapshot().Spawns[0];
        var later = runtime.StartedAt.AddMinutes(5);
        var applied = runtime.ClaimDefeat(
            101,
            first.ObjectId,
            first.SpawnGeneration,
            later);
        var before = runtime.Snapshot();
        var second = before.Spawns[1];
        var backward = runtime.ClaimDefeat(
            101,
            second.ObjectId,
            second.SpawnGeneration,
            runtime.StartedAt.AddMinutes(4));
        var after = runtime.Snapshot();
        Check.True(
            applied.Outcome == MedusaDefeatClaimOutcome.Applied &&
            backward.Outcome ==
                MedusaDefeatClaimOutcome.TimestampMovedBackward &&
            after.TeamScore == before.TeamScore &&
            after.LastObservedAt == later &&
            !after.Spawns[1].Defeated,
            "time moving backward is rejected without state mutation");
    }

    private static void CheckTimeoutBoundaries()
    {
        var beforeDeadline = MedusaRunRuntimeCheckFixture.Create();
        var beforeMarker = MedusaRunRuntimeCheckFixture.Complete(
            beforeDeadline,
            beforeDeadline.Deadline.AddTicks(-1));
        Check.True(
            beforeDeadline.Snapshot().State == MedusaRunState.Completed &&
            beforeMarker.CompletedAt == beforeDeadline.Deadline.AddTicks(-1),
            "a lethal defeat strictly before 40 minutes may complete");

        var afterDeadline = MedusaRunRuntimeCheckFixture.Create();
        var first = afterDeadline.Snapshot().Spawns[0];
        var late = afterDeadline.ClaimDefeat(
            101,
            first.ObjectId,
            first.SpawnGeneration,
            afterDeadline.Deadline.AddTicks(1));
        Check.True(
            late.Outcome == MedusaDefeatClaimOutcome.TimedOut &&
            afterDeadline.Snapshot().State == MedusaRunState.TimedOut &&
            afterDeadline.Snapshot().TeamScore == 0 &&
            afterDeadline.Snapshot().CompletionMarker is null,
            "a defeat after 40 minutes times out without score or marker");

        var exact = MedusaRunRuntimeCheckFixture.Create();
        var exactSpawns = exact.Snapshot().Spawns
            .OrderBy(static spawn =>
                spawn.Role == MedusaEncounterEnemyRole.Medusa ? 1 : 0)
            .ToArray();
        for (var index = 0; index < exactSpawns.Length - 1; index++)
        {
            var spawn = exactSpawns[index];
            var result = exact.ClaimDefeat(
                101,
                spawn.ObjectId,
                spawn.SpawnGeneration,
                exact.StartedAt.AddSeconds(1));
            Check.True(
                result.Outcome == MedusaDefeatClaimOutcome.Applied,
                $"pre-boundary defeat {index + 1} applies");
        }

        var finalSpawn = exactSpawns[^1];
        var scoreBeforeBoundary = exact.Snapshot().TeamScore;
        var boundary = exact.ClaimDefeat(
            101,
            finalSpawn.ObjectId,
            finalSpawn.SpawnGeneration,
            exact.Deadline);
        Check.True(
            boundary.Outcome ==
                MedusaDefeatClaimOutcome.DeadlineBoundaryUnresolved &&
            exact.Snapshot().State == MedusaRunState.Active &&
            exact.Snapshot().TeamScore == scoreBeforeBoundary &&
            exact.Snapshot().CompletionMarker is null &&
            !exact.Snapshot().Spawns.Single(spawn =>
                spawn.ObjectId == finalSpawn.ObjectId).Defeated,
            "exactly-at-40-minute lethal defeat fails closed distinctly");

        var afterBoundary = exact.ClaimDefeat(
            101,
            finalSpawn.ObjectId,
            finalSpawn.SpawnGeneration,
            exact.Deadline.AddTicks(1));
        Check.True(
            afterBoundary.Outcome == MedusaDefeatClaimOutcome.TimedOut &&
            exact.Snapshot().State == MedusaRunState.TimedOut,
            "a later authoritative observation terminalizes boundary state as timed out");

        var clockOnly = MedusaRunRuntimeCheckFixture.Create();
        Check.True(
            clockOnly.ObserveTime(clockOnly.Deadline) ==
                MedusaRunClockOutcome.DeadlineBoundaryUnresolved &&
            clockOnly.Snapshot().State == MedusaRunState.Active &&
            clockOnly.ObserveTime(clockOnly.Deadline.AddTicks(1)) ==
                MedusaRunClockOutcome.TimedOut &&
            clockOnly.Snapshot().State == MedusaRunState.TimedOut,
            "clock observation preserves the unresolved exact boundary then times out after it");
    }

    private static void CheckRejectedIdentitiesCannotAdvanceClock()
    {
        var runtime = MedusaRunRuntimeCheckFixture.Create();
        var spawns = runtime.Snapshot().Spawns;

        var foreignAfterDeadline = runtime.ClaimDefeat(
            999,
            spawns[0].ObjectId,
            spawns[0].SpawnGeneration,
            runtime.Deadline.AddTicks(1));
        var unknownAfterDeadline = runtime.ClaimDefeat(
            101,
            uint.MaxValue,
            spawns[0].SpawnGeneration,
            runtime.Deadline.AddTicks(1));
        var staleAfterDeadline = runtime.ClaimDefeat(
            101,
            spawns[0].ObjectId,
            checked(spawns[0].SpawnGeneration + 1),
            runtime.Deadline.AddTicks(1));
        var afterRejectedFutureClaims = runtime.Snapshot();

        var firstApplied = runtime.ClaimDefeat(
            101,
            spawns[0].ObjectId,
            spawns[0].SpawnGeneration,
            runtime.StartedAt.AddSeconds(1));
        var duplicateAfterDeadline = runtime.ClaimDefeat(
            101,
            spawns[0].ObjectId,
            spawns[0].SpawnGeneration,
            runtime.Deadline.AddTicks(1));
        var foreignBackward = runtime.ClaimDefeat(
            999,
            spawns[1].ObjectId,
            spawns[1].SpawnGeneration,
            runtime.StartedAt);
        var afterForeignBackward = runtime.Snapshot();
        var secondApplied = runtime.ClaimDefeat(
            101,
            spawns[1].ObjectId,
            spawns[1].SpawnGeneration,
            runtime.StartedAt.AddSeconds(2));

        Check.True(
            foreignAfterDeadline.Outcome ==
                MedusaDefeatClaimOutcome.CharacterNotAdmitted &&
            unknownAfterDeadline.Outcome ==
                MedusaDefeatClaimOutcome.UnknownSpawn &&
            staleAfterDeadline.Outcome ==
                MedusaDefeatClaimOutcome.StaleSpawnGeneration &&
            afterRejectedFutureClaims.State == MedusaRunState.Active &&
            afterRejectedFutureClaims.LastObservedAt == runtime.StartedAt &&
            firstApplied.Outcome == MedusaDefeatClaimOutcome.Applied &&
            duplicateAfterDeadline.Outcome ==
                MedusaDefeatClaimOutcome.DuplicateDefeat &&
            foreignBackward.Outcome ==
                MedusaDefeatClaimOutcome.CharacterNotAdmitted &&
            afterForeignBackward.State == MedusaRunState.Active &&
            afterForeignBackward.LastObservedAt ==
                runtime.StartedAt.AddSeconds(1) &&
            secondApplied.Outcome == MedusaDefeatClaimOutcome.Applied,
            "foreign, unknown, stale, and duplicate claims cannot poison the authoritative clock in either time direction");
    }

    private static void CheckWholeRunAbandonment()
    {
        var runtime = MedusaRunRuntimeCheckFixture.Create();
        var first = runtime.Snapshot().Spawns[0];
        _ = runtime.ClaimDefeat(
            101,
            first.ObjectId,
            first.SpawnGeneration,
            runtime.StartedAt.AddSeconds(1));

        var foreign = runtime.AbandonRun(
            999,
            runtime.Deadline.AddTicks(1));
        var afterForeign = runtime.Snapshot();
        var abandoned = runtime.AbandonRun(
            101,
            runtime.StartedAt.AddSeconds(3));
        var snapshot = runtime.Snapshot();
        Check.True(
            foreign == MedusaRunAbandonOutcome.CharacterNotAdmitted &&
            afterForeign.State == MedusaRunState.Active &&
            afterForeign.LastObservedAt == runtime.StartedAt.AddSeconds(1) &&
            abandoned == MedusaRunAbandonOutcome.Exited &&
            snapshot.State == MedusaRunState.VoluntarilyExited &&
            snapshot.CompletionMarker is null,
            "an admitted explicit whole-run abandonment terminalizes with no reward marker");

        var second = snapshot.Spawns[1];
        var after = runtime.ClaimDefeat(
            102,
            second.ObjectId,
            second.SpawnGeneration,
            runtime.StartedAt.AddSeconds(4));
        Check.True(
            after.Outcome == MedusaDefeatClaimOutcome.RunNotActive &&
            runtime.Snapshot().TeamScore == first.ScoreValue,
            "abandoned runs cannot award later score");

        var exact = MedusaRunRuntimeCheckFixture.Create();
        Check.True(
            exact.AbandonRun(101, exact.Deadline) ==
                MedusaRunAbandonOutcome.DeadlineBoundaryUnresolved &&
            exact.Snapshot().State == MedusaRunState.Active,
            "exact-deadline abandonment also preserves the unresolved boundary");
    }

    private static void CheckTitle(
        MedusaEncounterDifficulty difficulty,
        TimeSpan elapsed,
        MedusaEncounterTitle expectedTitle)
    {
        var runtime = MedusaRunRuntimeCheckFixture.Create(difficulty);
        var marker = MedusaRunRuntimeCheckFixture.Complete(
            runtime,
            runtime.StartedAt.Add(elapsed));
        Check.True(
            marker.SelectedTitle is { } selected &&
            selected.Title == expectedTitle &&
            selected.Difficulty == difficulty,
            $"{difficulty} {elapsed} retains only {expectedTitle}");
    }
}
