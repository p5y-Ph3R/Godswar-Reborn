using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class MapTraversalCatalogChecks
{
    public static Task RunAsync()
    {
        var catalog = MapTraversalCatalog.Default;

        CheckMapSetAndClassification(catalog);
        CheckCapturedDeduplication(catalog);
        CheckNorthernChain(catalog);
        CheckMycenaeWalkingTopology(catalog);
        CheckCapturedArrivalEvidence(catalog);
        CheckReciprocalArrival(catalog);
        CheckSpecialMapsHaveNoWalkingLinks(catalog);
        CheckMalformedMovementIsRejected(catalog);
        CheckNonTriggerMovement(catalog);

        return Task.CompletedTask;
    }

    private static void CheckCapturedArrivalEvidence(
        MapTraversalCatalog catalog)
    {
        Check.Equal(
            1,
            catalog.ArrivalEvidence.Count,
            "only live-corroborated arrival override is active");
        var arrival = catalog.ArrivalEvidence.Single();
        Check.Equal(
            (short)4,
            arrival.SourceMapId,
            "captured arrival source map");
        Check.Equal(
            (short)0,
            arrival.TargetMapId,
            "captured arrival target map");
        Check.Equal(
            new MapTraversalPosition(193f, -120f),
            arrival.Arrival,
            "captured Sparta gate arrival");
        Check.True(
            arrival.Source.EndsWith(
                "/Sparta/Address.ini",
                StringComparison.Ordinal),
            "captured arrival retains exact client source");
    }

    private static void CheckMapSetAndClassification(
        MapTraversalCatalog catalog)
    {
        var expectedIds = Enumerable.Range(0, 70)
            .Concat(Enumerable.Range(200, 11))
            .Select(static id => (short)id)
            .ToArray();

        Check.Equal(81, catalog.Maps.Count, "all client runtime maps captured");
        Check.True(
            catalog.Maps.Select(static map => map.MapId)
                .SequenceEqual(expectedIds),
            "runtime map IDs are exactly 0-69 and 200-210");
        Check.Equal(
            2,
            catalog.Maps.Count(static map =>
                map.Classification == MapTraversalClassification.City),
            "maps 0-1 are cities");
        Check.Equal(
            21,
            catalog.Maps.Count(static map =>
                map.Classification ==
                MapTraversalClassification.CoreWorld),
            "maps 2-22 are core world maps");
        Check.Equal(
            58,
            catalog.Maps.Count(static map =>
                map.Classification ==
                MapTraversalClassification.SpecialInstance),
            "remaining maps are special instances");
    }

    private static void CheckCapturedDeduplication(
        MapTraversalCatalog catalog)
    {
        Check.Equal(44, MapTemplateSeeds.Links.Count, "raw SpanMap rows retained");

        var distinctRawCount = MapTemplateSeeds.Links
            .Select(static link =>
                (link.MapId, link.TargetMapId, link.X, link.Z))
            .Distinct()
            .Count();
        Check.Equal(40, distinctRawCount, "raw SpanMap identity count");
        Check.Equal(50, catalog.EvidenceLinks.Count, "deduped raw plus north evidence");
        Check.Equal(48, catalog.AutomaticLinks.Count, "automatic reciprocal links");
        Check.Equal(
            2,
            catalog.DisabledLinks.Count,
            "nonwalking Mycenae rows remain disabled");
        Check.True(
            catalog.EvidenceLinks
                .GroupBy(static link =>
                    (link.SourceMapId,
                     link.TargetMapId,
                     link.Portal.X,
                     link.Portal.Z))
                .All(static group => group.Count() == 1),
            "catalog contains no duplicate portal evidence");
        Check.True(
            catalog.AutomaticLinks
                .GroupBy(static link =>
                    (link.SourceMapId, link.TargetMapId))
                .All(static group => group.Count() == 1),
            "automatic directed map pairs are unambiguous");
    }

    private static void CheckNorthernChain(MapTraversalCatalog catalog)
    {
        var expected = new[]
        {
            (Source: (short)6, Target: (short)7, X: -198f, Z: 0f),
            (Source: (short)7, Target: (short)6, X: 212f, Z: -104f),
            (Source: (short)7, Target: (short)20, X: -181f, Z: 226f),
            (Source: (short)20, Target: (short)7, X: 132f, Z: -224f),
            (Source: (short)20, Target: (short)10, X: -200f, Z: -4f),
            (Source: (short)10, Target: (short)20, X: 216f, Z: -68f),
            (Source: (short)10, Target: (short)22, X: -195f, Z: 150f),
            (Source: (short)22, Target: (short)10, X: 208f, Z: -16f),
            (Source: (short)22, Target: (short)21, X: -208f, Z: 124f),
            (Source: (short)21, Target: (short)22, X: 212f, Z: 80f)
        };

        foreach (var item in expected)
        {
            Check.True(
                catalog.TryGetAutomaticLink(
                    item.Source,
                    item.Target,
                    out var link),
                $"northern link {item.Source}->{item.Target} exists");
            Check.Equal(
                new MapTraversalPosition(item.X, item.Z),
                link.Portal,
                $"northern link {item.Source}->{item.Target} coordinate");
            Check.True(
                link.Confidence ==
                MapTraversalEvidenceConfidence.ReciprocalAddressPoint,
                $"northern link {item.Source}->{item.Target} confidence");
            Check.True(
                link.Source.EndsWith(
                    "/Address.ini",
                    StringComparison.Ordinal),
                "northern evidence names its exact Address.ini");
        }
    }

    private static void CheckMycenaeWalkingTopology(
        MapTraversalCatalog catalog)
    {
        var entry = catalog.AutomaticLinks
            .Single(static link => link.TargetMapId == 6);
        Check.Equal(
            (short)7,
            entry.SourceMapId,
            "only Olympia enters Mycenae by walking portal");

        var exit = catalog.GetAutomaticLinks(6).Single();
        Check.Equal(
            (short)7,
            exit.TargetMapId,
            "Mycenae walking portal returns only to Olympia");

        foreach (var target in new short[] { 9, 15 })
        {
            var evidence = catalog.EvidenceLinks.Single(link =>
                link.SourceMapId == 6 &&
                link.TargetMapId == target);
            Check.True(
                evidence.Activation ==
                MapTraversalActivation.DisabledByWorldTopology,
                $"Mycenae->{target} is disabled by world topology");
            Check.True(
                evidence.Confidence ==
                MapTraversalEvidenceConfidence
                    .ExcludedByObservedTopology,
                $"Mycenae->{target} exclusion is explicit");
            Check.True(
                !catalog.TryGetAutomaticLink(6, target, out _),
                $"Mycenae->{target} is excluded from walking traversal");
            Check.True(
                !catalog.TryGetAutomaticLink(target, 6, out _),
                $"{target}->Mycenae is excluded from walking traversal");
            Check.True(
                !catalog.TryResolveTargetArrival(
                    evidence,
                    6f,
                    out _),
                $"disabled Mycenae->{target} has no automatic arrival");
        }
    }

    private static void CheckReciprocalArrival(
        MapTraversalCatalog catalog)
    {
        foreach (var link in catalog.AutomaticLinks)
        {
            foreach (var radius in new[]
                     {
                         MapTraversalLimits.MinimumTriggerRadius,
                         6f,
                         MapTraversalLimits.MaximumTriggerRadius
                     })
            {
                Check.True(
                    catalog.TryResolveTargetArrival(
                        link,
                        radius,
                        out var candidate),
                    $"automatic link {link.SourceMapId}->{link.TargetMapId} " +
                    $"has a safe reciprocal arrival at radius {radius}");
                Check.Equal(
                    link.TargetMapId,
                    candidate.TargetMapId,
                    "arrival resolves the requested target map");

                var portalDeltaX =
                    candidate.TargetArrival.X - candidate.TargetPortal.X;
                var portalDeltaZ =
                    candidate.TargetArrival.Z - candidate.TargetPortal.Z;
                Check.True(
                    portalDeltaX * portalDeltaX +
                    portalDeltaZ * portalDeltaZ >
                    radius * radius,
                    "arrival is outside the reciprocal trigger");
            }
        }

        Check.True(
            catalog.TryGetAutomaticLink(0, 4, out var sourceLink),
            "Sparta-to-suburb source link exists");
        Check.True(
            catalog.TryResolveTargetArrival(
                sourceLink,
                6f,
                out var direct),
            "reciprocal target arrival resolves");
        Check.Equal(
            new MapTraversalPosition(102f, -232f),
            direct.TargetPortal,
            "arrival uses the reciprocal suburb portal");

        var offsetX = direct.TargetArrival.X - direct.TargetPortal.X;
        var offsetZ = direct.TargetArrival.Z - direct.TargetPortal.Z;
        var offsetLength = Math.Sqrt(
            (double)offsetX * offsetX +
            (double)offsetZ * offsetZ);
        Check.True(
            Math.Abs(offsetLength - 10d) < 0.001d,
            "arrival clears radius by four units");
        Check.True(
            offsetX * -direct.TargetPortal.X +
            offsetZ * -direct.TargetPortal.Z > 0f,
            "arrival offset points toward map center");

        Check.True(
            catalog.TryGetAutomaticLink(4, 0, out var reverseLink),
            "suburb-to-Sparta source link exists");
        Check.True(
            catalog.TryResolveTargetArrival(
                reverseLink,
                6f,
                out var reverse),
            "captured Sparta target arrival resolves");
        Check.Equal(
            new MapTraversalPosition(204f, -120f),
            reverse.TargetPortal,
            "Sparta arrival uses reciprocal city portal");
        Check.Equal(
            new MapTraversalPosition(193f, -120f),
            reverse.TargetArrival,
            "Sparta arrival uses client-authored walkable anchor");
        Check.True(
            reverse.Confidence ==
                MapTraversalEvidenceConfidence
                    .ReciprocalAddressPoint,
            "Sparta arrival retains address evidence confidence");

        var movement = new AcceptedMapMovementSegment(
            0,
            new MapTraversalPosition(190f, -120f),
            new MapTraversalPosition(210f, -120f));
        Check.True(
            MapTraversalDetector.TryDetectAndResolve(
                catalog,
                movement,
                6f,
                out var detected),
            "accepted movement segment detects and resolves portal");
        Check.Equal((short)4, detected.TargetMapId, "detected target map");
        Check.Equal(direct.TargetPortal, detected.TargetPortal, "same reciprocal");
    }

    private static void CheckSpecialMapsHaveNoWalkingLinks(
        MapTraversalCatalog catalog)
    {
        Check.True(
            catalog.AutomaticLinks.All(link =>
                catalog.TryGetMap(link.SourceMapId, out var source) &&
                catalog.TryGetMap(link.TargetMapId, out var target) &&
                source.Classification !=
                    MapTraversalClassification.SpecialInstance &&
                target.Classification !=
                    MapTraversalClassification.SpecialInstance),
            "automatic links stay inside city/core world");

        foreach (var map in catalog.Maps.Where(static map =>
                     map.Classification ==
                     MapTraversalClassification.SpecialInstance))
        {
            Check.Equal(
                0,
                catalog.GetAutomaticLinks(map.MapId).Count,
                $"special map {map.MapId} has no walking link");
        }
    }

    private static void CheckMalformedMovementIsRejected(
        MapTraversalCatalog catalog)
    {
        var validStart = new MapTraversalPosition(190f, -120f);
        var validEnd = new MapTraversalPosition(210f, -120f);

        foreach (var malformed in new[]
                 {
                     new AcceptedMapMovementSegment(
                         0,
                         new MapTraversalPosition(float.NaN, 0f),
                         validEnd),
                     new AcceptedMapMovementSegment(
                         0,
                         validStart,
                         new MapTraversalPosition(
                             float.PositiveInfinity,
                             0f)),
                     new AcceptedMapMovementSegment(
                         0,
                         new MapTraversalPosition(
                             MapTraversalLimits.MaximumCoordinateMagnitude + 1f,
                             0f),
                         validEnd),
                     new AcceptedMapMovementSegment(
                         0,
                         new MapTraversalPosition(0f, 0f),
                         new MapTraversalPosition(
                             MapTraversalLimits.MaximumAcceptedSegmentLength +
                             1f,
                             0f)),
                     new AcceptedMapMovementSegment(
                         0,
                         validStart,
                         validStart),
                     new AcceptedMapMovementSegment(
                         199,
                         validStart,
                         validEnd)
                 })
        {
            Check.True(
                !MapTraversalDetector.TryDetect(
                    catalog,
                    malformed,
                    6f,
                    out _),
                "malformed movement cannot trigger traversal");
        }

        var valid = new AcceptedMapMovementSegment(0, validStart, validEnd);
        foreach (var radius in new[]
                 {
                     0f,
                     float.NaN,
                     MapTraversalLimits.MaximumTriggerRadius + 1f
                 })
        {
            Check.True(
                !MapTraversalDetector.TryDetect(
                    catalog,
                    valid,
                    radius,
                    out _),
                "invalid portal radius is rejected");
        }
    }

    private static void CheckNonTriggerMovement(
        MapTraversalCatalog catalog)
    {
        var farMovement = new AcceptedMapMovementSegment(
            0,
            new MapTraversalPosition(0f, 0f),
            new MapTraversalPosition(5f, 0f));
        Check.True(
            !MapTraversalDetector.TryDetect(
                catalog,
                farMovement,
                6f,
                out _),
            "ordinary movement away from a portal does not transition");

        var specialMovement = new AcceptedMapMovementSegment(
            23,
            new MapTraversalPosition(0f, 0f),
            new MapTraversalPosition(5f, 0f));
        Check.True(
            !MapTraversalDetector.TryDetect(
                catalog,
                specialMovement,
                6f,
                out _),
            "special-instance movement does not auto-transition");
    }
}
