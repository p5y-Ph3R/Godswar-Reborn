using System.Collections.Immutable;

namespace Godswar.Server.Application.WorldInstances;

internal static partial class MedusaIslandPlacementPolicy
{
    private static void AddSecondIslandExtraElite(
        ImmutableArray<MedusaIslandAuthoredPlacement>.Builder placements) =>
        AddExact(
            placements,
            "E20-Elite",
            -115f,
            115f,
            "second-island/center/E20",
            "E20 adds one Elite Gorgon Guardian at the group center.");
}
