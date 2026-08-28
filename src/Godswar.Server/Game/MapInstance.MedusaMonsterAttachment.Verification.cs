using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private bool IsCompletePublishedMedusaAttachment(
        MedusaInstanceOwnershipSnapshot ownership,
        MedusaMonsterAttachmentSnapshot attachment)
    {
        if (_monsterRuntime is null ||
            _monsterRespawnPolicy != MonsterRespawnPolicy.Never ||
            attachment.WorldInstanceId != ownership.WorldInstanceId ||
            attachment.Difficulty != ownership.Difficulty ||
            attachment.ContentMapId != ownership.ContentMapId ||
            attachment.StartedAt != ownership.Run.StartedAt ||
            attachment.RuntimeMode != MonsterRuntimeMode.Ecs ||
            attachment.RespawnPolicy != MonsterRespawnPolicy.Never ||
            attachment.MonsterCount !=
                MedusaIslandRosterPolicy.TotalSpawnCount +
                MedusaIslandAmbientSpawnPolicy.CountFor(
                    ownership.Difficulty) ||
            attachment.RuntimeInstanceId == Guid.Empty ||
            attachment.Fingerprint.Length != 64 ||
            _monsterRuntime.MapId != ownership.ContentMapId.Value ||
            _monsterRuntime.Count != attachment.MonsterCount)
        {
            return false;
        }

        var expectedObjectIds = ownership.MonsterBindings
            .Select(static binding => binding.Identity.ObjectId)
            .Concat(WorldObjectIds.MedusaBabyRockElfObjectIds.Take(
                MedusaIslandAmbientSpawnPolicy.CountFor(
                    ownership.Difficulty)))
            .Order()
            .ToArray();
        var monsters = _monsterRuntime.Snapshot();
        return monsters.Count == attachment.MonsterCount &&
               monsters.All(monster =>
                   monster.RuntimeInstanceId ==
                       attachment.RuntimeInstanceId) &&
               monsters.Select(static monster => monster.ObjectId)
                   .Order()
                   .SequenceEqual(expectedObjectIds);
    }

    private static bool TryVerifyStagedMedusaRuntime(
        IMonsterMapRuntime runtime,
        MedusaMonsterBootstrapPreparation preparation,
        out Guid runtimeInstanceId)
    {
        runtimeInstanceId = Guid.Empty;
        if (runtime.MapId != preparation.ContentMapId.Value ||
            runtime.Count != preparation.RuntimeSpawnCount)
        {
            return false;
        }

        var monsters = runtime.Snapshot();
        if (monsters.Count != preparation.RuntimeSpawnCount)
        {
            return false;
        }

        var preparedByObjectId = preparation.Spawns.ToDictionary(
            static spawn => spawn.ObjectId);
        var ambientByObjectId = preparation.AmbientSpawns.ToDictionary(
            static spawn => spawn.ObjectId);
        foreach (var monster in monsters)
        {
            var isOwned = preparedByObjectId.TryGetValue(
                monster.ObjectId,
                out var prepared);
            var isAmbient = ambientByObjectId.TryGetValue(
                monster.ObjectId,
                out var ambient);
            var definitionMatches = isOwned
                ? DefinitionMatches(monster.Definition, prepared)
                : isAmbient &&
                  DefinitionMatches(monster.Definition, ambient);
            var expectedMaximumHealth = isOwned
                ? prepared.MaximumHealth
                : ambient.MaximumHealth;
            var expectedSpawnGeneration = isOwned
                ? prepared.SpawnGeneration
                : ambient.SpawnGeneration;
            if (isOwned == isAmbient ||
                !definitionMatches ||
                !SameFloatBits(
                    monster.HomeX,
                    monster.Definition.AppearanceX) ||
                !SameFloatBits(
                    monster.HomeZ,
                    monster.Definition.AppearanceZ) ||
                !SameFloatBits(monster.X, monster.HomeX) ||
                !SameFloatBits(monster.Z, monster.HomeZ) ||
                monster.CurrentHealth != expectedMaximumHealth ||
                monster.MaximumHealth != expectedMaximumHealth ||
                !monster.IsAlive ||
                !monster.IsSpawned ||
                monster.IsMoving ||
                monster.DespawnAt is not null ||
                monster.RespawnAt is not null ||
                monster.CombatPhase != MonsterCombatPhase.None ||
                monster.StunnedUntil is not null ||
                monster.SpawnGeneration != expectedSpawnGeneration ||
                monster.SpawnGeneration != 1 ||
                monster.HealthRevision != 0 ||
                monster.RuntimeInstanceId == Guid.Empty)
            {
                return false;
            }

            if (runtimeInstanceId == Guid.Empty)
            {
                runtimeInstanceId = monster.RuntimeInstanceId;
            }
            else if (runtimeInstanceId != monster.RuntimeInstanceId)
            {
                return false;
            }
        }

        return runtimeInstanceId != Guid.Empty;
    }

    private static bool DefinitionMatches(
        CapturedMonsterSpawn definition,
        MedusaMonsterBootstrapPreparedSpawn prepared) =>
        definition.MapId == prepared.MapId &&
        string.Equals(
            definition.SceneKey,
            prepared.SceneKey,
            StringComparison.Ordinal) &&
        string.Equals(
            definition.TemplateKey,
            prepared.TemplateKey,
            StringComparison.Ordinal) &&
        string.Equals(
            definition.DisplayName,
            prepared.DisplayName,
            StringComparison.Ordinal) &&
        definition.ObjectId == prepared.ObjectId &&
        definition.Tier == prepared.Tier &&
        SameFloatBits(definition.X, prepared.X) &&
        SameFloatBits(definition.Z, prepared.Z) &&
        definition.Packet.AsSpan().SequenceEqual(prepared.Packet.AsSpan());

    private static bool DefinitionMatches(
        CapturedMonsterSpawn definition,
        MedusaMonsterBootstrapPreparedAmbientSpawn prepared) =>
        definition.MapId == prepared.MapId &&
        string.Equals(
            definition.SceneKey,
            prepared.SceneKey,
            StringComparison.Ordinal) &&
        string.Equals(
            definition.TemplateKey,
            prepared.TemplateKey,
            StringComparison.Ordinal) &&
        string.Equals(
            definition.DisplayName,
            prepared.DisplayName,
            StringComparison.Ordinal) &&
        definition.ObjectId == prepared.ObjectId &&
        definition.Tier == prepared.Tier &&
        SameFloatBits(definition.X, prepared.X) &&
        SameFloatBits(definition.Z, prepared.Z) &&
        definition.Packet.AsSpan().SequenceEqual(prepared.Packet.AsSpan());

    private static bool SameFloatBits(float left, float right) =>
        BitConverter.SingleToInt32Bits(left) ==
        BitConverter.SingleToInt32Bits(right);
}
