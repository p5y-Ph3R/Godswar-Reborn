using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaRunRuntimeConstructionChecks
{
    public const string CheckName =
        "Medusa run identity, fixed roster, and immutable snapshot contract";

    public static Task RunAsync()
    {
        CheckIdentityAndDifficultyGuards();
        CheckParticipantGuards();
        CheckFixedRosterGuards();
        CheckExplicitSharedMapDifficulty();
        CheckNoPartyScaling();
        CheckStartAndDeadline();
        CheckImmutableCopiesAndSnapshots();
        return Task.CompletedTask;
    }

    private static void CheckIdentityAndDifficultyGuards()
    {
        Check.Throws<ArgumentException>(
            () => _ = new MedusaRunRuntime(
                default,
                MedusaEncounterDifficulty.Normal,
                [101],
                MedusaRunRuntimeCheckFixture.Spawns(),
                MedusaRunRuntimeCheckFixture.Start),
            "default world instance identity is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = new MedusaRunRuntime(
                WorldInstanceId.New(),
                (MedusaEncounterDifficulty)byte.MaxValue,
                [101],
                MedusaRunRuntimeCheckFixture.Spawns(),
                MedusaRunRuntimeCheckFixture.Start),
            "unknown difficulty is rejected rather than inferred");
        Check.Throws<ArgumentNullException>(
            () => _ = new MedusaRunRuntime(
                WorldInstanceId.New(),
                MedusaEncounterDifficulty.Normal,
                null!,
                MedusaRunRuntimeCheckFixture.Spawns(),
                MedusaRunRuntimeCheckFixture.Start),
            "null admission roster is rejected");
        Check.Throws<ArgumentNullException>(
            () => _ = new MedusaRunRuntime(
                WorldInstanceId.New(),
                MedusaEncounterDifficulty.Normal,
                [101],
                null!,
                MedusaRunRuntimeCheckFixture.Start),
            "null spawn roster is rejected");
    }

    private static void CheckParticipantGuards()
    {
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = MedusaRunRuntimeCheckFixture.Create(
                characters: Array.Empty<int>()),
            "empty run roster is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = MedusaRunRuntimeCheckFixture.Create(
                characters: [1, 2, 3, 4, 5, 6]),
            "more than five participants are rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = MedusaRunRuntimeCheckFixture.Create(
                characters: [0]),
            "non-positive character IDs are rejected");
        Check.Throws<ArgumentException>(
            () => _ = MedusaRunRuntimeCheckFixture.Create(
                characters: [101, 101]),
            "duplicate character IDs are rejected");
    }

    private static void CheckFixedRosterGuards()
    {
        var missing = MedusaRunRuntimeCheckFixture.Spawns()[..^1];
        Check.Throws<ArgumentException>(
            () => _ = MedusaRunRuntimeCheckFixture.Create(spawns: missing),
            "runs require all 136 captured spawns");

        var duplicateObject = MedusaRunRuntimeCheckFixture.Spawns();
        duplicateObject[1] = duplicateObject[1] with
        {
            ObjectId = duplicateObject[0].ObjectId
        };
        Check.Throws<ArgumentException>(
            () => _ = MedusaRunRuntimeCheckFixture.Create(
                spawns: duplicateObject),
            "object IDs are unique within a run");

        var duplicateRoster = MedusaRunRuntimeCheckFixture.Spawns();
        duplicateRoster[1] = duplicateRoster[1] with
        {
            RosterSpawnId = duplicateRoster[0].RosterSpawnId
        };
        Check.Throws<ArgumentException>(
            () => _ = MedusaRunRuntimeCheckFixture.Create(
                spawns: duplicateRoster),
            "authored roster IDs are unique within a run");

        var zeroGeneration = MedusaRunRuntimeCheckFixture.Spawns();
        zeroGeneration[0] = zeroGeneration[0] with
        {
            SpawnGeneration = 0
        };
        Check.Throws<ArgumentException>(
            () => _ = MedusaRunRuntimeCheckFixture.Create(
                spawns: zeroGeneration),
            "zero spawn generations are rejected");

        var wrongRank = MedusaRunRuntimeCheckFixture.Spawns();
        wrongRank[0] = wrongRank[0] with
        {
            Rank = MedusaMonsterRank.Boss
        };
        Check.Throws<ArgumentException>(
            () => _ = MedusaRunRuntimeCheckFixture.Create(spawns: wrongRank),
            "roster role rank cannot be rewritten");

        var wrongRole = MedusaRunRuntimeCheckFixture.Spawns();
        wrongRole[0] = wrongRole[0] with
        {
            Role = MedusaEncounterEnemyRole.Medusa
        };
        Check.Throws<ArgumentException>(
            () => _ = MedusaRunRuntimeCheckFixture.Create(spawns: wrongRole),
            "authored roster role cannot be rewritten");

        var wrongTemplate = MedusaRunRuntimeCheckFixture.Spawns();
        wrongTemplate[0] = wrongTemplate[0] with
        {
            TemplateKey = wrongTemplate[0].TemplateKey + "_wrong"
        };
        Check.Throws<ArgumentException>(
            () => _ = MedusaRunRuntimeCheckFixture.Create(
                spawns: wrongTemplate),
            "difficulty-specific client template cannot be rewritten");

        var normalSpawns = MedusaRunRuntimeCheckFixture.Spawns(
            MedusaEncounterDifficulty.Normal);
        var enhancedSpawns = MedusaRunRuntimeCheckFixture.Spawns(
            MedusaEncounterDifficulty.Enhanced);
        normalSpawns[0] = normalSpawns[0] with
        {
            TemplateKey = enhancedSpawns[0].TemplateKey
        };
        Check.Throws<ArgumentException>(
            () => _ = MedusaRunRuntimeCheckFixture.Create(
                MedusaEncounterDifficulty.Normal,
                spawns: normalSpawns),
            "Normal cannot bind its Enhanced map template key");

        var unknownRosterSlot = MedusaRunRuntimeCheckFixture.Spawns();
        unknownRosterSlot[0] = unknownRosterSlot[0] with
        {
            RosterSpawnId = "invented-slot"
        };
        Check.Throws<ArgumentException>(
            () => _ = MedusaRunRuntimeCheckFixture.Create(
                spawns: unknownRosterSlot),
            "run bindings must name authored roster slots");

        var runtime = MedusaRunRuntimeCheckFixture.Create();
        var snapshot = runtime.Snapshot();
        Check.True(
            snapshot.Spawns.Count == 136 &&
            snapshot.Spawns.Select(spawn => spawn.ObjectId).Distinct()
                .Count() == 136 &&
            snapshot.Spawns.Sum(spawn => spawn.ScoreValue) == 3_802,
            "captured run table has 136 unique objects and 3,802 points");
    }

    private static void CheckExplicitSharedMapDifficulty()
    {
        var enhanced = MedusaRunRuntimeCheckFixture.Create(
            MedusaEncounterDifficulty.Enhanced).Snapshot();
        var mythic = MedusaRunRuntimeCheckFixture.Create(
            MedusaEncounterDifficulty.Mythic).Snapshot();

        Check.True(
            enhanced.ContentMapId.Value == 200 &&
            mythic.ContentMapId.Value == 200 &&
            enhanced.Difficulty == MedusaEncounterDifficulty.Enhanced &&
            mythic.Difficulty == MedusaEncounterDifficulty.Mythic,
            "shared map 200 retains explicit Enhanced or Mythic run identity");
        Check.True(
            !MedusaIslandEncounterPolicy.TryGetUniqueDifficultyByContentMap(
                200,
                out _),
            "map 200 alone remains ambiguous and fails closed");
    }

    private static void CheckNoPartyScaling()
    {
        var solo = MedusaRunRuntimeCheckFixture.Create(
            characters: [101]).Snapshot();
        var fullParty = MedusaRunRuntimeCheckFixture.Create(
            characters: [101, 102, 103, 104, 105]).Snapshot();

        Check.True(
            solo.Spawns.SequenceEqual(fullParty.Spawns) &&
            solo.Spawns.Sum(spawn => spawn.ScoreValue) ==
                fullParty.Spawns.Sum(spawn => spawn.ScoreValue),
            "one and five admitted characters receive the identical run table");
    }

    private static void CheckStartAndDeadline()
    {
        var runtime = MedusaRunRuntimeCheckFixture.Create();
        var snapshot = runtime.Snapshot();
        Check.True(
            snapshot.StartedAt == MedusaRunRuntimeCheckFixture.Start &&
            snapshot.LastObservedAt == snapshot.StartedAt &&
            snapshot.Deadline == snapshot.StartedAt.AddMinutes(40),
            "start is recorded once and defines a fixed 40-minute deadline");

        Check.Throws<ArgumentOutOfRangeException>(
            () => _ = MedusaRunRuntimeCheckFixture.Create(
                startedAt: DateTimeOffset.MaxValue.AddMinutes(-20)),
            "a start that cannot represent its deadline is rejected");
    }

    private static void CheckImmutableCopiesAndSnapshots()
    {
        var characters = new[] { 105, 101 };
        var spawns = MedusaRunRuntimeCheckFixture.Spawns();
        var originalFirst = spawns[0];
        var runtime = MedusaRunRuntimeCheckFixture.Create(
            characters: characters,
            spawns: spawns);
        var before = runtime.Snapshot();

        characters[0] = 999;
        spawns[0] = spawns[0] with { ObjectId = uint.MaxValue };
        Check.True(
            before.AdmittedCharacterIds.SequenceEqual([101, 105]) &&
            before.Spawns.Any(spawn =>
                spawn.ObjectId == originalFirst.ObjectId),
            "constructor copies participant and spawn inputs");

        Check.Throws<NotSupportedException>(
            () => ((IList<int>)before.AdmittedCharacterIds)[0] = 999,
            "admitted-character snapshot is read-only");
        Check.Throws<NotSupportedException>(
            () => ((IList<MedusaRunSpawnSnapshot>)before.Spawns)[0] =
                default,
            "spawn snapshot is read-only");

        var first = before.Spawns[0];
        _ = runtime.ClaimDefeat(
            101,
            first.ObjectId,
            first.SpawnGeneration,
            runtime.StartedAt.AddSeconds(1));
        Check.True(
            !before.Spawns[0].Defeated &&
            runtime.Snapshot().Spawns[0].Defeated,
            "an earlier immutable snapshot does not change with live state");
    }
}
