using System.Collections.Immutable;

namespace Godswar.Server.Application.WorldInstances;

internal static partial class MedusaIslandRosterPolicy
{
    private static RosterContent BuildCapturedContent()
    {
        if (MedusaIslandCapturedLayout.Spawns.Length != TotalSpawnCount)
        {
            throw new InvalidOperationException(
                "Captured Medusa roster count does not match its policy.");
        }

        var spawns = ImmutableArray.CreateBuilder<MedusaIslandRosterSpawn>(
            TotalSpawnCount);
        foreach (var captured in MedusaIslandCapturedLayout.Spawns)
        {
            var skill = captured.Mechanic is { } mechanic
                ? AuthoredSkills.Single(candidate =>
                    candidate.Mechanic == mechanic)
                : (MedusaIslandRosterSkillBinding?)null;
            spawns.Add(new(
                captured.SpawnId,
                EliteGroupId: null,
                captured.Island,
                captured.Lane,
                captured.Kind,
                captured.Role,
                captured.Rank,
                captured.TemplateAlias,
                skill,
                captured.Anchor));
        }

        var immutable = spawns.MoveToImmutable();
        if (immutable.Select(static spawn => spawn.SpawnId)
                .Distinct(StringComparer.Ordinal).Count() != immutable.Length ||
            immutable.Count(static spawn =>
                spawn.EncounterRole == MedusaEncounterEnemyRole.Ordinary) !=
                OrdinaryCount ||
            immutable.Count(static spawn =>
                spawn.EncounterRole == MedusaEncounterEnemyRole.Elite) !=
                EliteCount ||
            immutable.Count(static spawn =>
                spawn.Rank == MedusaMonsterRank.Boss) != BossCount)
        {
            throw new InvalidOperationException(
                "Captured Medusa roster identities or roles are invalid.");
        }

        return new(
            ImmutableArray<MedusaIslandEliteGroup>.Empty,
            immutable);
    }
}
