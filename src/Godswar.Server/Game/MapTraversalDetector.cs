namespace Godswar.Server.Game;

/// <summary>
/// Detects portal crossings only from an already accepted authoritative
/// movement segment. It performs no client-position or movement acceptance.
/// </summary>
internal static class MapTraversalDetector
{
    public static bool TryDetect(
        MapTraversalCatalog catalog,
        in AcceptedMapMovementSegment movement,
        float triggerRadius,
        out MapTraversalLinkEvidence link)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        link = null!;

        if (!MapTraversalLimits.IsValidTriggerRadius(triggerRadius) ||
            !MapTraversalLimits.IsFiniteAndBounded(movement.Start) ||
            !MapTraversalLimits.IsFiniteAndBounded(movement.End) ||
            !catalog.TryGetMap(movement.MapId, out var map) ||
            map.Classification ==
                MapTraversalClassification.SpecialInstance)
        {
            return false;
        }

        var segmentX = (double)movement.End.X - movement.Start.X;
        var segmentZ = (double)movement.End.Z - movement.Start.Z;
        var segmentLengthSquared =
            segmentX * segmentX + segmentZ * segmentZ;
        var minimumLengthSquared =
            (double)MapTraversalLimits.MinimumAcceptedSegmentLength *
            MapTraversalLimits.MinimumAcceptedSegmentLength;
        var maximumLengthSquared =
            (double)MapTraversalLimits.MaximumAcceptedSegmentLength *
            MapTraversalLimits.MaximumAcceptedSegmentLength;
        if (!double.IsFinite(segmentLengthSquared) ||
            segmentLengthSquared < minimumLengthSquared ||
            segmentLengthSquared > maximumLengthSquared)
        {
            return false;
        }

        var radiusSquared = (double)triggerRadius * triggerRadius;
        var bestDistanceSquared = double.PositiveInfinity;
        foreach (var candidate in catalog.GetAutomaticLinks(movement.MapId))
        {
            var distanceSquared = DistanceToSegmentSquared(
                candidate.Portal,
                movement.Start,
                movement.End,
                segmentX,
                segmentZ,
                segmentLengthSquared);
            if (distanceSquared > radiusSquared ||
                !IsPreferred(
                    candidate,
                    distanceSquared,
                    link,
                    bestDistanceSquared))
            {
                continue;
            }

            link = candidate;
            bestDistanceSquared = distanceSquared;
        }

        return link is not null;
    }

    public static bool TryDetectAndResolve(
        MapTraversalCatalog catalog,
        in AcceptedMapMovementSegment movement,
        float triggerRadius,
        out MapTraversalResolution resolution)
    {
        resolution = null!;
        return TryDetect(
                   catalog,
                   movement,
                   triggerRadius,
                   out var link) &&
               catalog.TryResolveTargetArrival(
                   link,
                   triggerRadius,
                   out resolution);
    }

    private static double DistanceToSegmentSquared(
        in MapTraversalPosition point,
        in MapTraversalPosition start,
        in MapTraversalPosition end,
        double segmentX,
        double segmentZ,
        double segmentLengthSquared)
    {
        var pointFromStartX = (double)point.X - start.X;
        var pointFromStartZ = (double)point.Z - start.Z;
        var projection = Math.Clamp(
            (pointFromStartX * segmentX +
             pointFromStartZ * segmentZ) /
            segmentLengthSquared,
            0d,
            1d);
        var closestX = start.X + segmentX * projection;
        var closestZ = start.Z + segmentZ * projection;
        var deltaX = point.X - closestX;
        var deltaZ = point.Z - closestZ;
        return deltaX * deltaX + deltaZ * deltaZ;
    }

    private static bool IsPreferred(
        MapTraversalLinkEvidence candidate,
        double candidateDistanceSquared,
        MapTraversalLinkEvidence? current,
        double currentDistanceSquared)
    {
        const double equalityTolerance = 0.000001d;
        if (current is null ||
            candidateDistanceSquared <
            currentDistanceSquared - equalityTolerance)
        {
            return true;
        }

        if (Math.Abs(candidateDistanceSquared - currentDistanceSquared) >
            equalityTolerance)
        {
            return false;
        }

        var targetComparison =
            candidate.TargetMapId.CompareTo(current.TargetMapId);
        if (targetComparison != 0)
        {
            return targetComparison < 0;
        }

        var xComparison = candidate.Portal.X.CompareTo(current.Portal.X);
        return xComparison != 0
            ? xComparison < 0
            : candidate.Portal.Z < current.Portal.Z;
    }
}
