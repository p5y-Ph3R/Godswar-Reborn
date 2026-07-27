using Godswar.Server.State;

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

    private MapTraversalCatalog(
        IReadOnlyList<MapTraversalMap> maps,
        IReadOnlyList<MapTraversalLinkEvidence> evidenceLinks)
    {
        ValidateMapSet(maps);

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
        GatedLinks = EvidenceLinks
            .Where(static link =>
                link.Activation ==
                MapTraversalActivation.ManualApprovalRequired)
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

        ValidateAutomaticReciprocity();
    }

    public static MapTraversalCatalog Default { get; } = CreateDefault();

    public IReadOnlyList<MapTraversalMap> Maps { get; }

    public IReadOnlyList<MapTraversalLinkEvidence> EvidenceLinks { get; }

    public IReadOnlyList<MapTraversalLinkEvidence> AutomaticLinks { get; }

    public IReadOnlyList<MapTraversalLinkEvidence> GatedLinks { get; }

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
    /// Resolves an arrival beside the matching reciprocal portal. The offset
    /// points toward the target map's origin and clears the trigger radius, so
    /// the player cannot immediately bounce back through the same boundary.
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

    private static MapTraversalCatalog CreateDefault()
    {
        var maps = MapTemplateSeeds.Maps
            .Select(static seed => new MapTraversalMap(
                seed.MapId,
                seed.SceneKey,
                seed.DisplayName,
                seed.ClientSceneId,
                Classify(seed.MapId),
                Origin))
            .ToArray();

        var evidence = BuildCapturedLinks();
        evidence.AddRange(CreateNorthernAddressLinks());
        return new MapTraversalCatalog(maps, evidence);
    }

    private static List<MapTraversalLinkEvidence> BuildCapturedLinks()
    {
        var evidence = new List<MapTraversalLinkEvidence>();
        var seen = new HashSet<PortalIdentity>();
        foreach (var seed in MapTemplateSeeds.Links)
        {
            var identity = new PortalIdentity(
                seed.MapId,
                seed.TargetMapId,
                seed.X,
                seed.Z);
            if (!seen.Add(identity))
            {
                continue;
            }

            var gated = seed.MapId == 6 &&
                        seed.TargetMapId is 9 or 15;
            evidence.Add(new MapTraversalLinkEvidence(
                seed.MapId,
                seed.TargetMapId,
                new MapTraversalPosition(seed.X, seed.Z),
                seed.Source,
                gated
                    ? MapTraversalEvidenceConfidence.ConflictingGated
                    : MapTraversalEvidenceConfidence.CapturedSpanMap,
                gated
                    ? MapTraversalActivation.ManualApprovalRequired
                    : MapTraversalActivation.Automatic,
                gated
                    ? "One-way SpanMap evidence conflicts with Mycenae " +
                      "Address.ini labels; requires client observation."
                    : "Captured SpanMap boundary with a matching reciprocal."));
        }

        return evidence;
    }

    private static IEnumerable<MapTraversalLinkEvidence>
        CreateNorthernAddressLinks()
    {
        return
        [
            AddressLink(6, 7, -198f, 0f, "Mycenae_All", "Olympia"),
            AddressLink(7, 6, 212f, -104f, "Olympia_All", "Mycenae"),
            AddressLink(7, 20, -181f, 226f, "Olympia_All", "Delphi Forest"),
            AddressLink(20, 7, 132f, -224f, "Oracle_of_Delphi_All", "Olympia"),
            AddressLink(20, 10, -200f, -4f, "Oracle_of_Delphi_All", "Larissa"),
            AddressLink(10, 20, 216f, -68f, "Larissa_All", "Delphi Forest"),
            AddressLink(10, 22, -195f, 150f, "Larissa_All", "Elasson"),
            AddressLink(22, 10, 208f, -16f, "Elasson_All", "Larissa"),
            AddressLink(22, 21, -208f, 124f, "Elasson_All", "Olympus"),
            AddressLink(21, 22, 212f, 80f, "Olympus_All", "Elasson")
        ];
    }

    private static MapTraversalLinkEvidence AddressLink(
        short sourceMapId,
        short targetMapId,
        float x,
        float z,
        string sceneKey,
        string label) =>
        new(
            sourceMapId,
            targetMapId,
            new MapTraversalPosition(x, z),
            $"./Localization/en_us/Monster/{sceneKey}/Address.ini",
            MapTraversalEvidenceConfidence.ReciprocalAddressPoint,
            MapTraversalActivation.Automatic,
            $"Exact '{label}' address point paired with its reciprocal map label.");

    private static MapTraversalClassification Classify(short mapId) =>
        mapId switch
        {
            0 or 1 => MapTraversalClassification.City,
            >= 2 and <= 22 => MapTraversalClassification.CoreWorld,
            _ => MapTraversalClassification.SpecialInstance
        };

    private static void ValidateMapSet(IReadOnlyList<MapTraversalMap> maps)
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

        if (!actual.SetEquals(expected))
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
