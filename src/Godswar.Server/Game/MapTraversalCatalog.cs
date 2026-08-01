using Godswar.Server.Application.World;

namespace Godswar.Server.Game;

/// <summary>
/// Immutable client-evidence catalog for authoritative walking transitions.
/// It deliberately keeps conflicting evidence visible without activating it.
/// </summary>
internal sealed class MapTraversalCatalog
{
    private static readonly MapTraversalPosition Origin = new(0f, 0f);

    private readonly IReadOnlyDictionary<short, MapTraversalMap> _mapsById;
    private readonly IReadOnlyDictionary<
        (short SourceMapId, short TargetMapId),
        MapTraversalLinkEvidence> _automaticByPair;
    private readonly IReadOnlyDictionary<
        short,
        IReadOnlyList<MapTraversalLinkEvidence>> _automaticBySource;
    private readonly IReadOnlyDictionary<
        (short SourceMapId, short TargetMapId),
        MapTraversalArrivalEvidence> _arrivalsByPair;

    private MapTraversalCatalog(
        IReadOnlyList<MapTraversalMap> maps,
        IReadOnlyList<MapTraversalLinkEvidence> evidenceLinks,
        IReadOnlyList<MapTraversalArrivalEvidence> arrivalEvidence,
        bool requireCompleteMapSet = true)
    {
        ValidateMapSet(maps, requireCompleteMapSet);

        _mapsById = maps.ToDictionary(static map => map.MapId);
        Maps = maps.OrderBy(static map => map.MapId).ToArray();
        EvidenceLinks = evidenceLinks
            .OrderBy(static link => link.SourceMapId)
            .ThenBy(static link => link.TargetMapId)
            .ThenBy(static link => link.Portal.X)
            .ThenBy(static link => link.Portal.Z)
            .ToArray();
        AutomaticLinks = EvidenceLinks
            .Where(static link =>
                link.Activation == MapTraversalActivation.Automatic)
            .ToArray();
        DisabledLinks = EvidenceLinks
            .Where(static link =>
                link.Activation ==
                MapTraversalActivation.DisabledByWorldTopology)
            .ToArray();

        ValidateLinks(EvidenceLinks);

        _automaticByPair = AutomaticLinks.ToDictionary(
            static link => (link.SourceMapId, link.TargetMapId));
        _automaticBySource = AutomaticLinks
            .GroupBy(static link => link.SourceMapId)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<MapTraversalLinkEvidence>)
                    group.OrderBy(static link => link.TargetMapId).ToArray());
        ArrivalEvidence = arrivalEvidence
            .OrderBy(static arrival => arrival.SourceMapId)
            .ThenBy(static arrival => arrival.TargetMapId)
            .ToArray();
        _arrivalsByPair = ArrivalEvidence.ToDictionary(
            static arrival =>
                (arrival.SourceMapId, arrival.TargetMapId));

        ValidateAutomaticReciprocity();
        ValidateArrivals();
    }

    public static MapTraversalCatalog Empty { get; } = new(
        [],
        [],
        [],
        requireCompleteMapSet: false);

    public IReadOnlyList<MapTraversalMap> Maps { get; }

    public IReadOnlyList<MapTraversalLinkEvidence> EvidenceLinks { get; }

    public IReadOnlyList<MapTraversalLinkEvidence> AutomaticLinks { get; }

    public IReadOnlyList<MapTraversalLinkEvidence> DisabledLinks { get; }

    public IReadOnlyList<MapTraversalArrivalEvidence> ArrivalEvidence
    {
        get;
    }

    public bool TryGetMap(short mapId, out MapTraversalMap map) =>
        _mapsById.TryGetValue(mapId, out map!);

    public IReadOnlyList<MapTraversalLinkEvidence> GetAutomaticLinks(
        short sourceMapId) =>
        _automaticBySource.TryGetValue(sourceMapId, out var links)
            ? links
            : [];

    public bool TryGetAutomaticLink(
        short sourceMapId,
        short targetMapId,
        out MapTraversalLinkEvidence link) =>
        _automaticByPair.TryGetValue(
            (sourceMapId, targetMapId),
            out link!);

    /// <summary>
    /// Resolves a reviewed authored arrival beside the matching reciprocal
    /// portal when available. Otherwise, the bounded fallback points toward
    /// the target map's origin. Every arrival clears all portal triggers so
    /// the player cannot immediately bounce through the same boundary.
    /// </summary>
    public bool TryResolveTargetArrival(
        MapTraversalLinkEvidence sourceLink,
        float triggerRadius,
        out MapTraversalResolution resolution)
    {
        resolution = null!;

        if (sourceLink is null ||
            !MapTraversalLimits.IsValidTriggerRadius(triggerRadius) ||
            sourceLink.Activation != MapTraversalActivation.Automatic ||
            !_automaticByPair.TryGetValue(
                (sourceLink.SourceMapId, sourceLink.TargetMapId),
                out var canonicalSource) ||
            canonicalSource != sourceLink ||
            !_automaticByPair.TryGetValue(
                (sourceLink.TargetMapId, sourceLink.SourceMapId),
                out var reciprocal) ||
            !_mapsById.TryGetValue(sourceLink.TargetMapId, out var targetMap))
        {
            return false;
        }

        var towardCenterX = targetMap.Center.X - reciprocal.Portal.X;
        var towardCenterZ = targetMap.Center.Z - reciprocal.Portal.Z;
        if (_arrivalsByPair.TryGetValue(
                (sourceLink.SourceMapId, sourceLink.TargetMapId),
                out var authoredArrival) &&
            !IsInsideAnyPortalTrigger(
                sourceLink.TargetMapId,
                authoredArrival.Arrival,
                triggerRadius))
        {
            resolution = new MapTraversalResolution(
                sourceLink.SourceMapId,
                sourceLink.TargetMapId,
                sourceLink.Portal,
                reciprocal.Portal,
                authoredArrival.Arrival,
                triggerRadius,
                authoredArrival.Source,
                authoredArrival.Confidence);
            return true;
        }

        var length = Math.Sqrt(
            (double)towardCenterX * towardCenterX +
            (double)towardCenterZ * towardCenterZ);
        if (!double.IsFinite(length) ||
            length <= MapTraversalLimits.MinimumAcceptedSegmentLength)
        {
            return false;
        }

        var offset = triggerRadius + MapTraversalLimits.ArrivalClearance;
        var arrival = new MapTraversalPosition(
            reciprocal.Portal.X + (float)(towardCenterX / length * offset),
            reciprocal.Portal.Z + (float)(towardCenterZ / length * offset));
        if (!MapTraversalLimits.IsFiniteAndBounded(arrival) ||
            IsInsideAnyPortalTrigger(
                sourceLink.TargetMapId,
                arrival,
                triggerRadius))
        {
            return false;
        }

        resolution = new MapTraversalResolution(
            sourceLink.SourceMapId,
            sourceLink.TargetMapId,
            sourceLink.Portal,
            reciprocal.Portal,
            arrival,
            triggerRadius,
            sourceLink.Source,
            sourceLink.Confidence);
        return true;
    }

    public static MapTraversalCatalog Create(
        GameplayContentCatalog gameplay)
    {
        ArgumentNullException.ThrowIfNull(gameplay);
        if (gameplay.Maps.Count == 0)
        {
            if (gameplay.AddressPoints.Count != 0 ||
                gameplay.Links.Count != 0)
            {
                throw new InvalidDataException(
                    "Map gameplay content cannot contain links or address " +
                    "points without map definitions.");
            }

            return Empty;
        }

        var maps = gameplay.Maps
            .Select(static seed => new MapTraversalMap(
                seed.MapId,
                seed.SceneKey,
                seed.DisplayName,
                seed.ClientSceneId,
                Classify(seed.MapId),
                Origin))
            .ToArray();
        return new MapTraversalCatalog(
            maps,
            BuildPublishedLinks(gameplay),
            BuildPublishedArrivals(gameplay));
    }

    private static IReadOnlyList<MapTraversalArrivalEvidence>
        BuildPublishedArrivals(GameplayContentCatalog gameplay)
    {
        var spartaGate = gameplay.AddressPoints.SingleOrDefault(
            static point =>
                point.MapId == 0 &&
                point.GroupIndex == 0 &&
                point.PointIndex == 1 &&
                point.Name == "Suburb of Sparta");
        if (spartaGate is null)
        {
            throw new InvalidDataException(
                "Published gameplay content is missing the reviewed Sparta " +
                "suburb arrival anchor.");
        }

        return
        [
            new MapTraversalArrivalEvidence(
                SourceMapId: 4,
                TargetMapId: 0,
                new MapTraversalPosition(
                    spartaGate.X,
                    spartaGate.Z),
                spartaGate.Source,
                MapTraversalEvidenceConfidence
                    .ReciprocalAddressPoint,
                "Client-authored Sparta gate anchor, corroborated by the " +
                "accepted outbound walking corridor.")
        ];
    }

    private static IReadOnlyList<MapTraversalLinkEvidence>
        BuildPublishedLinks(GameplayContentCatalog gameplay)
    {
        return gameplay.Links
            .Select(static link => new MapTraversalLinkEvidence(
                link.SourceMapId,
                link.TargetMapId,
                new MapTraversalPosition(link.X, link.Z),
                link.Source,
                link.Confidence switch
                {
                    GameplayMapLinkConfidence.CapturedSpanMap =>
                        MapTraversalEvidenceConfidence.CapturedSpanMap,
                    GameplayMapLinkConfidence.ReciprocalAddressPoint =>
                        MapTraversalEvidenceConfidence.ReciprocalAddressPoint,
                    GameplayMapLinkConfidence.ExcludedByObservedTopology =>
                        MapTraversalEvidenceConfidence
                            .ExcludedByObservedTopology,
                    _ => throw new InvalidDataException(
                        $"Unsupported published map-link confidence " +
                        $"{link.Confidence}.")
                },
                link.Activation switch
                {
                    GameplayMapLinkActivation.Automatic =>
                        MapTraversalActivation.Automatic,
                    GameplayMapLinkActivation.DisabledByWorldTopology =>
                        MapTraversalActivation.DisabledByWorldTopology,
                    _ => throw new InvalidDataException(
                        $"Unsupported published map-link activation " +
                        $"{link.Activation}.")
                },
                link.Note))
            .ToArray();
    }

    private static MapTraversalClassification Classify(short mapId) =>
        mapId switch
        {
            0 or 1 => MapTraversalClassification.City,
            >= 2 and <= 22 => MapTraversalClassification.CoreWorld,
            _ => MapTraversalClassification.SpecialInstance
        };

    private static void ValidateMapSet(
        IReadOnlyList<MapTraversalMap> maps,
        bool requireCompleteMapSet)
    {
        var expected = Enumerable.Range(0, 70)
            .Concat(Enumerable.Range(200, 11))
            .Select(static id => (short)id)
            .ToHashSet();
        var actual = new HashSet<short>();
        foreach (var map in maps)
        {
            if (!actual.Add(map.MapId) ||
                string.IsNullOrWhiteSpace(map.SceneKey) ||
                string.IsNullOrWhiteSpace(map.DisplayName) ||
                !MapTraversalLimits.IsFiniteAndBounded(map.Center))
            {
                throw new InvalidDataException(
                    $"Invalid or duplicate map traversal entry {map.MapId}.");
            }
        }

        if (requireCompleteMapSet && !actual.SetEquals(expected))
        {
            throw new InvalidDataException(
                "Map traversal catalog must contain IDs 0-69 and 200-210 exactly.");
        }
    }

    private void ValidateLinks(
        IReadOnlyList<MapTraversalLinkEvidence> links)
    {
        var identities = new HashSet<PortalIdentity>();
        var automaticPairs = new HashSet<(short, short)>();
        foreach (var link in links)
        {
            if (!_mapsById.ContainsKey(link.SourceMapId) ||
                !_mapsById.ContainsKey(link.TargetMapId) ||
                link.SourceMapId == link.TargetMapId ||
                !MapTraversalLimits.IsFiniteAndBounded(link.Portal) ||
                string.IsNullOrWhiteSpace(link.Source) ||
                string.IsNullOrWhiteSpace(link.Note) ||
                !identities.Add(new PortalIdentity(
                    link.SourceMapId,
                    link.TargetMapId,
                    link.Portal.X,
                    link.Portal.Z)))
            {
                throw new InvalidDataException(
                    $"Invalid map traversal evidence {link.SourceMapId} -> " +
                    $"{link.TargetMapId}.");
            }

            if (link.Activation == MapTraversalActivation.Automatic)
            {
                if (!automaticPairs.Add(
                        (link.SourceMapId, link.TargetMapId)) ||
                    _mapsById[link.SourceMapId].Classification ==
                        MapTraversalClassification.SpecialInstance ||
                    _mapsById[link.TargetMapId].Classification ==
                        MapTraversalClassification.SpecialInstance)
                {
                    throw new InvalidDataException(
                        "Automatic traversal links must be unique and remain " +
                        "inside the city/core-world graph.");
                }
            }
        }
    }

    private void ValidateAutomaticReciprocity()
    {
        foreach (var link in AutomaticLinks)
        {
            if (!_automaticByPair.ContainsKey(
                    (link.TargetMapId, link.SourceMapId)))
            {
                throw new InvalidDataException(
                    $"Automatic map link {link.SourceMapId} -> " +
                    $"{link.TargetMapId} has no reciprocal boundary.");
            }
        }
    }

    private void ValidateArrivals()
    {
        foreach (var arrival in ArrivalEvidence)
        {
            if (!_automaticByPair.TryGetValue(
                    (arrival.SourceMapId, arrival.TargetMapId),
                    out var sourceLink) ||
                !_automaticByPair.TryGetValue(
                    (arrival.TargetMapId, arrival.SourceMapId),
                    out var reciprocal) ||
                !MapTraversalLimits.IsFiniteAndBounded(
                    arrival.Arrival) ||
                string.IsNullOrWhiteSpace(arrival.Source) ||
                string.IsNullOrWhiteSpace(arrival.Note))
            {
                throw new InvalidDataException(
                    $"Invalid map arrival evidence " +
                    $"{arrival.SourceMapId}->{arrival.TargetMapId}.");
            }

            var deltaX =
                (double)arrival.Arrival.X - reciprocal.Portal.X;
            var deltaZ =
                (double)arrival.Arrival.Z - reciprocal.Portal.Z;
            if (deltaX * deltaX + deltaZ * deltaZ <=
                (double)MapTraversalLimits.MinimumTriggerRadius *
                MapTraversalLimits.MinimumTriggerRadius)
            {
                throw new InvalidDataException(
                    $"Map arrival evidence remains on its target portal " +
                    $"{sourceLink.SourceMapId}->{sourceLink.TargetMapId}.");
            }
        }
    }

    private bool IsInsideAnyPortalTrigger(
        short mapId,
        in MapTraversalPosition position,
        float radius)
    {
        var radiusSquared = (double)radius * radius;
        foreach (var link in GetAutomaticLinks(mapId))
        {
            var deltaX = (double)position.X - link.Portal.X;
            var deltaZ = (double)position.Z - link.Portal.Z;
            if (deltaX * deltaX + deltaZ * deltaZ <= radiusSquared)
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct PortalIdentity(
        short SourceMapId,
        short TargetMapId,
        float X,
        float Z);
}
