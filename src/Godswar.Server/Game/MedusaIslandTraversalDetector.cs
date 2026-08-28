using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.Game;

internal readonly record struct MedusaIslandTraversalResolution(
    string SourceAnchorId,
    string TargetAnchorId,
    float TargetX,
    float TargetZ);

/// <summary>
/// Resolves the two one-way island rings from an already accepted movement
/// segment. This does not make client-authored coordinates authoritative.
/// </summary>
internal static class MedusaIslandTraversalDetector
{
    private static readonly (string Source, string Target)[] Links =
    [
        ("first-to-second-source", "first-to-second-destination"),
        ("second-to-final-source", "second-to-final-destination")
    ];

    public static bool TryResolve(
        in AcceptedMapMovementSegment movement,
        float triggerRadius,
        out MedusaIslandTraversalResolution resolution)
    {
        resolution = default;
        if (movement.MapId is not (200 or 204) ||
            !MapTraversalLimits.IsValidTriggerRadius(triggerRadius) ||
            !MapTraversalLimits.IsFiniteAndBounded(movement.Start) ||
            !MapTraversalLimits.IsFiniteAndBounded(movement.End))
        {
            return false;
        }

        var segmentX = (double)movement.End.X - movement.Start.X;
        var segmentZ = (double)movement.End.Z - movement.Start.Z;
        var lengthSquared = segmentX * segmentX + segmentZ * segmentZ;
        var minimumSquared =
            (double)MapTraversalLimits.MinimumAcceptedSegmentLength *
            MapTraversalLimits.MinimumAcceptedSegmentLength;
        var maximumSquared =
            (double)MapTraversalLimits.MaximumAcceptedSegmentLength *
            MapTraversalLimits.MaximumAcceptedSegmentLength;
        if (!double.IsFinite(lengthSquared) ||
            lengthSquared < minimumSquared ||
            lengthSquared > maximumSquared)
        {
            return false;
        }

        var radiusSquared = (double)triggerRadius * triggerRadius;
        foreach (var link in Links)
        {
            if (!MedusaIslandPlacementPolicy.TryGetTraversalAnchor(
                    link.Source,
                    out var source) ||
                !MedusaIslandPlacementPolicy.TryGetTraversalAnchor(
                    link.Target,
                    out var target) ||
                DistanceToSegmentSquared(
                    source.X,
                    source.Z,
                    movement.Start,
                    segmentX,
                    segmentZ,
                    lengthSquared) > radiusSquared)
            {
                continue;
            }

            resolution = new(
                source.AnchorId,
                target.AnchorId,
                target.X,
                target.Z);
            return true;
        }

        return false;
    }

    private static double DistanceToSegmentSquared(
        float pointX,
        float pointZ,
        in MapTraversalPosition start,
        double segmentX,
        double segmentZ,
        double lengthSquared)
    {
        var projection = Math.Clamp(
            (((double)pointX - start.X) * segmentX +
             ((double)pointZ - start.Z) * segmentZ) / lengthSquared,
            0d,
            1d);
        var deltaX = pointX - (start.X + segmentX * projection);
        var deltaZ = pointZ - (start.Z + segmentZ * projection);
        return deltaX * deltaX + deltaZ * deltaZ;
    }
}
