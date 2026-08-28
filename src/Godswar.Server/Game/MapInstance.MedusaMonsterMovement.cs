using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private static bool TryApplyConfiguredMedusaMovement(
        IMonsterMapRuntime runtime,
        MedusaMonsterBootstrapPreparation preparation)
    {
        foreach (var spawn in preparation.Spawns)
        {
            if (!MedusaIslandRosterPolicy.TryGetSpawn(
                    spawn.RosterSpawnId,
                    out var roster) ||
                !MedusaMonsterContentCatalog.Current.TryGetMonster(
                    preparation.Difficulty,
                    roster.TemplateAlias,
                    out var rule) ||
                !runtime.TrySetMovementSpeedBasisPoints(
                    spawn.ObjectId,
                    spawn.SpawnGeneration,
                    rule.MovementSpeedBasisPoints))
            {
                return false;
            }
        }
        return true;
    }

    internal int ResolveMonsterBaseMovementSpeedBasisPoints(
        uint objectId,
        uint expectedSpawnGeneration)
    {
        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is null)
            {
                return 10_000;
            }

            var ownership = _medusaInstanceOwner.Snapshot();
            var binding = ownership.MonsterBindings.SingleOrDefault(
                candidate =>
                    candidate.Identity.ObjectId == objectId &&
                    candidate.Identity.SpawnGeneration ==
                        expectedSpawnGeneration);
            if (binding.Identity.ObjectId == 0 ||
                !MedusaIslandRosterPolicy.TryGetSpawn(
                    binding.RosterSpawnId,
                    out var roster) ||
                !MedusaMonsterContentCatalog.Current.TryGetMonster(
                    ownership.Difficulty,
                    roster.TemplateAlias,
                    out var rule))
            {
                return 10_000;
            }
            return rule.MovementSpeedBasisPoints;
        }
    }
}
