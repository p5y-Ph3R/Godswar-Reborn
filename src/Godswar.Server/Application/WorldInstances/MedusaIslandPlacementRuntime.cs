namespace Godswar.Server.Application.WorldInstances;

internal static partial class MedusaIslandPlacementPolicy
{
    /// <summary>
    /// Resolves a server-authored monster spawn only when the installed
    /// client's decoded collision table proves that point is unblocked.
    /// Traversal-trigger certification is separate: scene entry and island
    /// transfers do not determine whether a monster may be placed at an
    /// otherwise valid world position.
    /// </summary>
    public static bool TryResolveServerSpawn(
        MedusaEncounterDifficulty difficulty,
        string? spawnId,
        out MedusaIslandResolvedPlacement resolved)
    {
        if (!HasVerifiedClientBlockTableConsumer ||
            !TryResolveCandidate(difficulty, spawnId, out resolved) ||
            !resolved.Placement.IsClientBlockTableUnblocked)
        {
            resolved = default;
            return false;
        }

        return true;
    }
}
