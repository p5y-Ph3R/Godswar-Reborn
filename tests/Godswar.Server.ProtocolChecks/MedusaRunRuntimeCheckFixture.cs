using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaRunRuntimeCheckFixture
{
    public static readonly DateTimeOffset Start = new(
        2026,
        8,
        22,
        12,
        0,
        0,
        TimeSpan.Zero);

    public static MedusaRunRuntime Create(
        MedusaEncounterDifficulty difficulty =
            MedusaEncounterDifficulty.Enhanced,
        IReadOnlyCollection<int>? characters = null,
        IReadOnlyCollection<MedusaRunSpawnDefinition>? spawns = null,
        DateTimeOffset? startedAt = null) =>
        new(
            new WorldInstanceId(
                new Guid("55b83939-3ad6-4e4b-97e0-f3be6ebfc05f")),
            difficulty,
            characters ?? [101, 102, 103, 104, 105],
            spawns ?? Spawns(difficulty),
            startedAt ?? Start);

    public static MedusaRunSpawnDefinition[] Spawns(
        MedusaEncounterDifficulty difficulty =
            MedusaEncounterDifficulty.Enhanced)
    {
        var definitions = new List<MedusaRunSpawnDefinition>(
            MedusaIslandRosterPolicy.TotalSpawnCount);
        for (var index = 0;
             index < MedusaIslandRosterPolicy.Spawns.Length;
             index++)
        {
            var spawn = MedusaIslandRosterPolicy.Spawns[index];
            Check.True(
                MedusaIslandRosterPolicy.TryResolveTemplate(
                    difficulty,
                    spawn.TemplateAlias,
                    out var template),
                $"{difficulty} template resolves for {spawn.SpawnId}");
            definitions.Add(new(
                spawn.SpawnId,
                checked((uint)(50_000 + index)),
                checked((uint)(1_000 + index)),
                template.TemplateKey,
                spawn.EncounterRole,
                spawn.Rank));
        }

        return definitions.ToArray();
    }

    public static MedusaRunCompletionMarker Complete(
        MedusaRunRuntime runtime,
        DateTimeOffset completedAt,
        int characterId = 101)
    {
        var spawns = runtime.Snapshot().Spawns
            .OrderBy(static spawn => spawn.Role switch
            {
                MedusaEncounterEnemyRole.Stheno => 1,
                MedusaEncounterEnemyRole.Medusa => 2,
                _ => 0
            })
            .ToArray();
        for (var index = 0; index < spawns.Length; index++)
        {
            var spawn = spawns[index];
            var at = index == spawns.Length - 1
                ? completedAt
                : runtime.StartedAt.AddSeconds(1);
            var result = runtime.ClaimDefeat(
                characterId,
                spawn.ObjectId,
                spawn.SpawnGeneration,
                at);
            var expected = index == spawns.Length - 1
                ? MedusaDefeatClaimOutcome.Completed
                : MedusaDefeatClaimOutcome.Applied;
            Check.True(
                result.Outcome == expected,
                $"roster defeat {index + 1} has expected outcome");
        }

        var snapshot = runtime.Snapshot();
        Check.True(
            snapshot.CompletionMarker is { } marker,
            "completed run retains a completion marker");
        return snapshot.CompletionMarker!.Value;
    }
}
