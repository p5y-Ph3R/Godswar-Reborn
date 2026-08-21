using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class WorldInstanceRuntimeDirectoryChecks
{
    private static void AssertStatus(
        WorldInstanceRuntimeDirectoryStatus expected,
        WorldInstanceRuntimeDirectoryResult actual,
        string message)
    {
        Check.True(actual.Status == expected, message);
        Check.Equal(
            expected is
                WorldInstanceRuntimeDirectoryStatus.Created or
                WorldInstanceRuntimeDirectoryStatus.ExistingDefault or
                WorldInstanceRuntimeDirectoryStatus.Draining or
                WorldInstanceRuntimeDirectoryStatus.Closed or
                WorldInstanceRuntimeDirectoryStatus.Removed,
            actual.Succeeded,
            $"{message} success classification");
    }

    private static void AssertPlacementStatus(
        WorldInstancePlacementStatus expected,
        WorldInstancePlacementResult actual,
        string message)
    {
        Check.True(actual.Status == expected, message);
        Check.True(actual.Succeeded, $"{message} succeeds");
    }
}
