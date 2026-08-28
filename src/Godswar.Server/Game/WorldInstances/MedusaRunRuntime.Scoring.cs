using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Game.WorldInstances;

internal sealed partial class MedusaRunRuntime
{
    private static int CappedScoreAfter(int currentScore, int award) =>
        checked(currentScore + award);
}
