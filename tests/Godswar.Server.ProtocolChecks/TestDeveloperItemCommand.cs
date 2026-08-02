using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class TestDeveloperItemCommand
{
    public static bool TryParse(
        string text,
        out DeveloperItemRequest? request,
        out string error,
        DeveloperMountCatalog? mounts = null) =>
        DeveloperItemCommand.TryParse(
            text,
            out request,
            out error,
            mounts,
            TestItemContent.Content.DeveloperItems);
}
