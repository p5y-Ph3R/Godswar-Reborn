using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Godswar.Server.Application.WorldInstances;

internal static partial class MedusaIslandPlacementPolicy
{
    private static PlacementContent BuildCapturedContent()
    {
        var placements = ImmutableArray.CreateBuilder<
            MedusaIslandAuthoredPlacement>(
            MedusaIslandRosterPolicy.TotalSpawnCount);
        foreach (var captured in MedusaIslandCapturedLayout.Spawns)
        {
            if (!MedusaIslandRosterPolicy.TryGetSpawn(
                    captured.SpawnId,
                    out var roster) ||
                !TryProjectToHmpBlock(
                    captured.X,
                    captured.Z,
                    out var blockCell))
            {
                throw new InvalidOperationException(
                    $"Captured Medusa placement {captured.SpawnId} is invalid.");
            }

            placements.Add(new(
                captured.SpawnId,
                roster.EliteGroupId,
                captured.Island,
                captured.Lane,
                captured.X,
                captured.Z,
                $"external-capture/{captured.Island}/{captured.SpawnId}",
                "Observed as a live client-accepted appearance in the " +
                "2026-08-27 external Medusa capture.",
                MedusaIslandPlacementEvidenceLevel.ClientPlacementAccepted,
                MinimumClientBlockTableClearance,
                blockCell,
                DecodedHmpBlockValue: 0,
                DecodedHmpComponent: captured.Island));
        }

        var immutable = placements.MoveToImmutable();
        if (immutable.Length != MedusaIslandRosterPolicy.TotalSpawnCount ||
            immutable.Select(static placement => placement.SpawnId)
                .Distinct(StringComparer.Ordinal).Count() != immutable.Length ||
            immutable.Any(static placement =>
                !placement.IsLiveSpawnEligible))
        {
            throw new InvalidOperationException(
                "Captured Medusa placement set is incomplete.");
        }

        return new(
            immutable,
            immutable.ToFrozenDictionary(
                static placement => placement.SpawnId,
                StringComparer.Ordinal));
    }
}
