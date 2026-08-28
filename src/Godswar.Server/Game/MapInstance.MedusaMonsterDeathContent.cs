using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    internal bool TryResolveMedusaMonsterRule(
        uint objectId,
        uint spawnGeneration,
        out MedusaMonsterRule rule)
    {
        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is null)
            {
                rule = default;
                return false;
            }

            var ownership = _medusaInstanceOwner.Snapshot();
            var binding = ownership.MonsterBindings.SingleOrDefault(
                candidate =>
                    candidate.Identity.ObjectId == objectId &&
                    candidate.Identity.SpawnGeneration == spawnGeneration);
            if (binding.Identity.ObjectId == 0 ||
                !MedusaIslandRosterPolicy.TryGetSpawn(
                    binding.RosterSpawnId,
                    out var roster))
            {
                rule = default;
                return false;
            }

            return MedusaMonsterContentCatalog.Current.TryGetMonster(
                ownership.Difficulty,
                roster.TemplateAlias,
                out rule);
        }
    }
}
