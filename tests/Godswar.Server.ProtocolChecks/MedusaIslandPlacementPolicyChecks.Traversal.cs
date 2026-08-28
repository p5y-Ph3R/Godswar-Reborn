using Godswar.Server.Game;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaIslandPlacementPolicyChecks
{
    private static void CheckLiveTraversalDetector()
    {
        var firstRing = Segment(200, -40f, 51f, -30f, 51f);
        Check.True(MedusaIslandTraversalDetector.TryResolve(
                firstRing,
                6f,
                out var first) &&
            first.SourceAnchorId == "first-to-second-source" &&
            first.TargetAnchorId == "first-to-second-destination" &&
            first.TargetX == -83f && first.TargetZ == 101f,
            "the first ring transfers to the second island");

        var secondRing = Segment(204, -134f, 139f, -124f, 139f);
        Check.True(MedusaIslandTraversalDetector.TryResolve(
                secondRing,
                6f,
                out var second) &&
            second.SourceAnchorId == "second-to-final-source" &&
            second.TargetAnchorId == "second-to-final-destination" &&
            second.TargetX == -145f && second.TargetZ == 152f,
            "the second ring transfers to the final island");

        Check.True(!MedusaIslandTraversalDetector.TryResolve(
                Segment(200, 0f, 0f, 2f, 0f),
                6f,
                out _) &&
            !MedusaIslandTraversalDetector.TryResolve(
                Segment(1, -40f, 51f, -30f, 51f),
                6f,
                out _),
            "unrelated movement and non-Medusa maps cannot transfer");
    }

    private static AcceptedMapMovementSegment Segment(
        short mapId,
        float startX,
        float startZ,
        float endX,
        float endZ) => new(
            mapId,
            new MapTraversalPosition(startX, startZ),
            new MapTraversalPosition(endX, endZ));
}
