using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static class MedusaMonsterBootstrapPolicyCheckFixture
{
    public static readonly DateTimeOffset StartedAt = new(
        2026,
        8,
        23,
        10,
        0,
        0,
        TimeSpan.Zero);

    public static Fixture Create(
        MedusaEncounterDifficulty difficulty =
            MedusaEncounterDifficulty.Enhanced,
        WorldInstanceId? worldInstanceId = null,
        DateTimeOffset? startedAt = null)
    {
        Check.True(
            MedusaIslandEncounterPolicy.TryGetDifficulty(
                difficulty,
                out var difficultyDefinition),
            $"{difficulty} bootstrap difficulty resolves");
        var id = worldInstanceId ?? new WorldInstanceId(
            new Guid("cf92aacb-087c-4690-a2af-39a06c1ad5a6"));
        var start = startedAt ?? StartedAt;
        var runSpawns = new MedusaRunSpawnDefinition[
            MedusaIslandRosterPolicy.TotalSpawnCount];
        var captures = new CapturedMonsterSpawn[
            runSpawns.Length +
            MedusaIslandAmbientSpawnPolicy.CountFor(difficulty)];

        for (var index = 0; index < runSpawns.Length; index++)
        {
            var roster = MedusaIslandRosterPolicy.Spawns[index];
            Check.True(
                MedusaIslandRosterPolicy.TryResolveTemplate(
                    difficulty,
                    roster.TemplateAlias,
                    out var template),
                $"{difficulty}/{roster.SpawnId} bootstrap template resolves");
            Check.True(
                MedusaIslandPlacementPolicy.TryResolveCandidate(
                    difficulty,
                    roster.SpawnId,
                    out var placement),
                $"{difficulty}/{roster.SpawnId} candidate placement resolves");
            Check.True(
                MedusaMonsterContentCatalog.Current.TryGetMonster(
                    difficulty,
                    roster.TemplateAlias,
                    out var monsterRule),
                $"{difficulty}/{roster.SpawnId} monster content resolves");
            var objectId = checked(
                WorldObjectIds.FirstMedusaMonsterObjectId +
                (uint)index);
            runSpawns[index] = new(
                roster.SpawnId,
                objectId,
                SpawnGeneration: 1,
                template.TemplateKey,
                roster.EncounterRole,
                roster.Rank);
            captures[index] = CreateCapture(
                template,
                objectId,
                monsterRule.Level,
                monsterRule.MaximumHealth,
                placement.Placement.X,
                placement.Placement.Z);
        }

        var ambientSpawns =
            MedusaIslandAmbientSpawnPolicy.SpawnsFor(difficulty);
        for (var index = 0; index < ambientSpawns.Count; index++)
        {
            var ambient = ambientSpawns[index];
            captures[runSpawns.Length + index] = CreateCapture(
                ambient.MapId,
                ambient.SceneKey,
                ambient.TemplateKey,
                ambient.DisplayName,
                WorldObjectIds.MedusaBabyRockElfObjectIds[index],
                ambient.Tier,
                ambient.MaximumHealth,
                ambient.X,
                ambient.Z);
        }

        var run = new MedusaRunRuntime(
            id,
            difficulty,
            [101],
            runSpawns,
            start);
        var mechanics = new MedusaEncounterMechanicsRuntime(run);
        var runSnapshot = run.Snapshot();
        var bindings = runSnapshot.Spawns
            .Select(spawn => new MedusaOwnedMonsterBinding(
                new(spawn.ObjectId, spawn.SpawnGeneration),
                spawn.RosterSpawnId,
                spawn.TemplateKey,
                spawn.Role,
                spawn.Rank,
                difficulty,
                difficultyDefinition.ContentMapId))
            .ToImmutableArray();
        var ownership = new MedusaInstanceOwnershipSnapshot(
            id,
            difficulty,
            difficultyDefinition.ContentMapId,
            bindings,
            runSnapshot,
            mechanics.Snapshot());
        return new(ownership, captures);
    }

    public static CapturedMonsterSpawn[] CloneDefinitions(
        IEnumerable<CapturedMonsterSpawn> definitions) =>
        definitions.Select(definition => definition with
            {
                Packet = definition.Packet.ToArray()
            })
            .ToArray();

    private static CapturedMonsterSpawn CreateCapture(
        MedusaIslandResolvedTemplate template,
        uint objectId,
        uint tier,
        uint health,
        float x,
        float z)
        => CreateCapture(
            template.MapId,
            template.SceneKey,
            template.TemplateKey,
            template.DisplayName,
            objectId,
            tier,
            health,
            x,
            z);

    private static CapturedMonsterSpawn CreateCapture(
        short mapId,
        string sceneKey,
        string templateKey,
        string displayName,
        uint objectId,
        uint tier,
        uint health,
        float x,
        float z)
    {
        var packet = new byte[108];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            10020);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(4, 4),
            0x00000212);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            objectId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(12, 4),
            tier);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(20, 4),
            health);
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(24, 4),
            health);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(28, 4), x);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(32, 4), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(36, 4), z);
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(40, 4), 0f);
        var templateBytes = Encoding.ASCII.GetBytes(templateKey);
        Check.True(
            templateBytes.Length < packet.Length - 44,
            $"{templateKey} fits opcode-10020 appearance");
        templateBytes.CopyTo(packet.AsSpan(44));

        var definition = new CapturedMonsterSpawn(
            mapId,
            sceneKey,
            templateKey,
            displayName,
            objectId,
            x,
            z,
            packet);
        definition.Validate(mapId);
        return definition;
    }

    internal sealed record Fixture(
        MedusaInstanceOwnershipSnapshot Ownership,
        CapturedMonsterSpawn[] Definitions);
}
