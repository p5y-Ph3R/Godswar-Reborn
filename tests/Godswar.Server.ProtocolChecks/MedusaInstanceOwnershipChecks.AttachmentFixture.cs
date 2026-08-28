using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
    private sealed record MedusaAttachmentInputs(
        MedusaEncounterDifficulty Difficulty,
        int[] AdmittedCharacterIds,
        MedusaRunSpawnDefinition[] RunSpawns,
        CapturedMonsterSpawn[] Definitions);

    private sealed record MedusaAttachmentFixture(
        MapInstance Map,
        MedusaAttachmentInputs Inputs);

    private static MedusaAttachmentFixture CreateAttachmentFixture(
        MedusaEncounterDifficulty difficulty =
            MedusaEncounterDifficulty.Enhanced,
        MonsterRuntimeMode monsterRuntimeMode = MonsterRuntimeMode.Ecs,
        PlayerRuntimeMode playerRuntimeMode = PlayerRuntimeMode.Ecs,
        bool rejectNeverWorldBoss = false)
    {
        var instanceId = WorldInstanceId.New();
        var inputs = CreateAttachmentInputs(
            difficulty,
            instanceId,
            StartedAt);
        Check.True(
            MedusaIslandEncounterPolicy.TryGetDifficulty(
                difficulty,
                out var difficultyDefinition),
            $"{difficulty} attachment difficulty resolves");
        var descriptor = WorldInstanceDescriptor.Create(
            RealmId.Tempest,
            instanceId,
            difficultyDefinition.ContentMapId,
            InstanceKind.Dungeon,
            playerCapacity: 5,
            StartedAt);
        var worldBossCatalog = rejectNeverWorldBoss
            ? CreateRejectingWorldBossCatalog(inputs.Definitions[0])
            : WorldBossCatalog.Empty;
        var map = new MapInstance(
            descriptor,
            monsterRuntimeMode,
            playerRuntimeMode,
            worldBossCatalog);
        return new(map, inputs);
    }

    private static MedusaAttachmentInputs CreateAttachmentInputs(
        MedusaEncounterDifficulty difficulty,
        WorldInstanceId instanceId,
        DateTimeOffset startedAt)
    {
        var bootstrap =
            MedusaMonsterBootstrapPolicyCheckFixture.Create(
                difficulty,
                instanceId,
                startedAt);
        var run = bootstrap.Ownership.Run;
        var runSpawns = run.Spawns.Select(static spawn => new
            MedusaRunSpawnDefinition(
                spawn.RosterSpawnId,
                spawn.ObjectId,
                spawn.SpawnGeneration,
                spawn.TemplateKey,
                spawn.Role,
                spawn.Rank))
            .ToArray();
        return new(
            difficulty,
            run.AdmittedCharacterIds.ToArray(),
            runSpawns,
            MedusaMonsterBootstrapPolicyCheckFixture.CloneDefinitions(
                bootstrap.Definitions));
    }

    private static MedusaMonsterAttachmentResult AttachAuthored(
        MedusaAttachmentFixture fixture,
        IReadOnlyList<CapturedMonsterSpawn>? definitions = null) =>
        fixture.Map.PrepareAndAttachMedusaForAuthoredValidationTests(
            fixture.Inputs.Difficulty,
            fixture.Inputs.AdmittedCharacterIds,
            fixture.Inputs.RunSpawns,
            definitions ?? fixture.Inputs.Definitions);

    private static WorldBossCatalog CreateRejectingWorldBossCatalog(
        CapturedMonsterSpawn source) => WorldBossCatalog.Create(
        [new WorldBossDefinition(
            source.MapId,
            source.SceneKey,
            source.TemplateKey,
            "Medusa attachment rollback boss",
            RespawnInterval: TimeSpan.FromHours(12))],
        TimeSpan.FromHours(12));

    private static string SnapshotMonsterValues(MapInstance map) =>
        string.Join(
            '|',
            map.SnapshotMonsters().OrderBy(static monster => monster.ObjectId)
                .Select(monster => string.Join(
                    ':',
                    monster.ObjectId,
                    monster.SpawnGeneration,
                    monster.HealthRevision,
                    monster.CurrentHealth,
                    monster.MaximumHealth,
                    monster.IsAlive,
                    monster.IsSpawned,
                    monster.RespawnAt?.UtcTicks ?? 0,
                    monster.RuntimeInstanceId,
                    Convert.ToHexString(monster.Definition.Packet))));
}
