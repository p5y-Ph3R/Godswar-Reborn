using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Candidate-only 74-spawn plan authored against the byte-identical stock
/// map-200/map-204 topology and the client's HMP block table. Static-mesh
/// acceptance and transport-trigger bindings remain unresolved, so live
/// resolution fails closed.
/// </summary>
internal static partial class MedusaIslandPlacementPolicy
{
    public const int TerrainCellsPerAxis = 128;
    public const float TerrainCellSize = 4f;
    public const float WorldHalfExtent = 256f;
    public const int MinimapPixelsPerAxis = 512;
    public const int HmpBlockCellsPerAxis = 2_048;
    public const int HmpBlockByteOffset = 356_684;
    public const int HmpBlockByteLength = 4_194_304;
    public const float HmpBlockCellsPerWorldUnit = 4f;
    public const float MinimumAuthoredSeparation = 4.75f;
    public const float MinimumClientBlockTableClearance = 4f;

    public const string CommonHmpSha256 =
        "2519287645950257306D055B70571B40EA7143A0A051EC77CE027A105EC9B598";

    public const string CommonMinimapSha256 =
        "C4207D34498E564DEAF465432A625C23F1B19BA5C5C4A9D0017671F10AC38D41";

    public const string CommonHmpBlockSha256 =
        "A13395AB9CF89AB3C2B3AF3DFA2DE607574404F9BEA192B29644482AA962419F";

    private static readonly MedusaIslandPlacementFormation FourMemberFormation =
        new(
            "elite-center-three-escort",
            [(0f, 0f), (-4f, -3f), (4f, -3f), (0f, 5f)]);

    private static readonly MedusaIslandPlacementFormation ThreeMemberFormation =
        new(
            "elite-center-two-escort",
            [(0f, 0f), (-4f, -3f), (4f, -3f)]);

    private static readonly ImmutableArray<MedusaIslandMapAssetEvidence>
        AssetEvidence =
        [
            Asset(
                200,
                "Medusa_Island",
                "Map/Medusa_Island.hmp",
                "Localization/en_us/UI/Texture/MinMap/Medusa_Island.gwo"),
            Asset(
                204,
                "Medusa_Island2",
                "Map/Medusa_Island2.hmp",
                "Localization/en_us/UI/Texture/MinMap/Medusa_Island2.gwo")
        ];

    private static readonly FrozenDictionary<short, MedusaIslandMapAssetEvidence>
        AssetsByMap = AssetEvidence.ToFrozenDictionary(asset => asset.MapId);

    private static readonly PlacementContent Content = BuildCapturedContent();

    public static ImmutableArray<MedusaIslandAuthoredPlacement> Placements =>
        Content.Placements;

    public static ImmutableArray<MedusaIslandMapAssetEvidence> Assets =>
        AssetEvidence;

    public static ImmutableArray<MedusaIslandPlacementFormation> Formations =>
        [FourMemberFormation, ThreeMemberFormation];

    public static bool SharesVerifiedClientTopology =>
        AssetEvidence.Length == 2 &&
        AssetEvidence.All(asset =>
            asset.HmpSha256 == CommonHmpSha256 &&
            asset.MinimapSha256 == CommonMinimapSha256 &&
            asset.SharesVerifiedGeometryWithOtherDifficulty);

    public static bool HasClientPlacementAcceptedPlacements =>
        Content.Placements.Any(placement => placement.IsLiveSpawnEligible);

    public static bool HasClientBlockTableUnblockedPlacements =>
        Content.Placements.All(placement =>
            placement.IsClientBlockTableUnblocked &&
            placement.VerifiedBlockedCellClearanceFloor >=
                MinimumClientBlockTableClearance);

    public static bool TryGetAssetEvidence(
        int mapId,
        out MedusaIslandMapAssetEvidence evidence)
    {
        if (mapId is < short.MinValue or > short.MaxValue)
        {
            evidence = default;
            return false;
        }

        return AssetsByMap.TryGetValue((short)mapId, out evidence);
    }

    public static bool TryGetCandidate(
        string? spawnId,
        out MedusaIslandAuthoredPlacement placement)
    {
        if (string.IsNullOrWhiteSpace(spawnId))
        {
            placement = null!;
            return false;
        }

        return Content.BySpawnId.TryGetValue(spawnId, out placement!);
    }

    public static bool TryResolveCandidate(
        MedusaEncounterDifficulty difficulty,
        string? spawnId,
        out MedusaIslandResolvedPlacement resolved)
    {
        if (!TryGetCandidate(spawnId, out var placement) ||
            !TryResolveMap(difficulty, out var asset))
        {
            resolved = default;
            return false;
        }

        resolved = new(difficulty, asset.MapId, asset.SceneKey, placement);
        return true;
    }

    /// <summary>
     /// The only placement API suitable for a future spawn adapter. It remains
    /// unavailable until points and traversal carry client acceptance evidence.
    /// </summary>
    public static bool TryResolveLiveCertified(
        MedusaEncounterDifficulty difficulty,
        string? spawnId,
        out MedusaIslandResolvedPlacement resolved)
    {
        if (!HasClientCertifiedTraversal ||
            !TryResolveCandidate(difficulty, spawnId, out resolved) ||
            !resolved.Placement.IsLiveSpawnEligible)
        {
            resolved = default;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Projects world coordinates through the client's scaled 45-degree map
    /// transform. This is an authorship aid, not collision validation.
    /// </summary>
    public static bool TryProjectToMinimap(
        float x,
        float z,
        out MedusaIslandMinimapPixel pixel)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z) ||
            x < -WorldHalfExtent || x >= WorldHalfExtent ||
            z <= -WorldHalfExtent || z > WorldHalfExtent)
        {
            pixel = default;
            return false;
        }

        var projectedX = MathF.Truncate(x) *
            (x > 0f && z < 0f ? 0.58f : 0.57f);
        var projectedZ = MathF.Truncate(z) * (z >= 0f ? 0.54f : 0.57f);
        const float inverseSqrtTwo = 0.7071067811865476f;
        pixel = new(
            (int)(WorldHalfExtent +
                (projectedX + projectedZ) * inverseSqrtTwo),
            (int)(WorldHalfExtent -
                (-projectedX + projectedZ) * inverseSqrtTwo));
        return pixel.X is >= 0 and < MinimapPixelsPerAxis &&
               pixel.Y is >= 0 and < MinimapPixelsPerAxis;
    }

    private static PlacementContent BuildContent()
    {
        var placements = ImmutableArray.CreateBuilder<
            MedusaIslandAuthoredPlacement>(
            MedusaIslandRosterPolicy.TotalSpawnCount);

        AddFirstLaneRow(placements, 1, 5, 9,
            (123f, -206f), (169f, -160f), (207f, -120f),
            "southern lane split");
        AddFirstLaneRow(placements, 2, 6, 10,
            (56f, -154f), (102f, -108f), (148f, -62f),
            "lower obstacle channels");
        AddFirstLaneRow(placements, 3, 7, 11,
            (16f, -114f), (62f, -68f), (108f, -22f),
            "upper obstacle channels");
        AddFirstLaneRow(placements, 4, 8, 12,
            (-2.5f, -92f), (43.5f, -46f), (84.5f, -3f),
            "northern lane convergence");

        AddExact(placements, "E14-Elite", -82f, -11f,
            "first-island/top-left/E14-guard",
            "E14 guards the inside approach to Euryale without adding normal escorts.");
        AddExact(placements, "Euryale", -90f, -11f,
            "first-island/top-left/euryale",
            "Euryale occupies the remembered top-left first-island anchor, outside E14.");
        AddExact(placements, "E15-Elite", 25f, 108f,
            "first-island/top-right/E15-guard",
            "E15 guards the inside approach to Chrysaor without adding normal escorts.");
        AddExact(placements, "Chrysaor", 33f, 108f,
            "first-island/top-right/chrysaor",
            "Chrysaor occupies the remembered top-right first-island anchor, outside E15.");

        AddThreeMemberGroup(placements, 13, -100f, 105f,
            "second-island/ring-2-landing/east",
            "E13 opens the second component west of the ring-2 landing.");
        AddThreeMemberGroup(placements, 16, -130f, 105f,
            "second-island/ring-2-landing/west",
            "E16 holds the west side of the second-component landing shelf.");
        AddThreeMemberGroup(placements, 17, -130f, 125f,
            "second-island/middle/west-rise",
            "E17 controls the westward rise through the second component.");
        AddThreeMemberGroup(placements, 18, -115f, 125f,
            "second-island/middle/east-rise",
            "E18 covers the east branch on the approach to ring 1.");
        AddThreeMemberGroup(placements, 19, -100f, 125f,
            "second-island/ring-1-approach",
            "E19 closes the second component before the ring-1 transfer.");

        AddExact(placements, "Stheno", -160f, 175f,
            "final-island/west-core/stheno",
            "Stheno anchors the physical-damage side of the final component.");
        AddExact(placements, "Medusa", -125f, 185f,
            "final-island/east-core/medusa",
            "Medusa anchors the magical-damage side of the final component.");
        AddExact(placements, "Final-Pikeman-1", -175f, 155f,
            "final-island/west-lower/physical-amplifier",
            "The first Pikeman occupies Stheno's lower-west utility flank.");
        AddExact(placements, "Final-Pikeman-2", -172f, 186f,
            "final-island/west-upper/physical-amplifier",
            "The second Pikeman completes Stheno's west-side utility pair.");
        AddExact(placements, "Final-Axeman-1", -114f, 175f,
            "final-island/east-lower/magical-amplifier",
            "The first Axeman occupies Medusa's lower-east utility flank.");
        AddExact(placements, "Final-Axeman-2", -105f, 195f,
            "final-island/east-upper/magical-amplifier",
            "The second Axeman completes Medusa's east-side utility pair.");
        AddSecondIslandExtraElite(placements);

        var immutable = placements.MoveToImmutable();
        Validate(immutable);
        return new(
            immutable,
            immutable.ToFrozenDictionary(
                placement => placement.SpawnId,
                StringComparer.Ordinal));
    }

    private static void AddFirstLaneRow(
        ImmutableArray<MedusaIslandAuthoredPlacement>.Builder placements,
        int stunGroupId,
        int freezeGroupId,
        int bleedGroupId,
        (float X, float Z) stun,
        (float X, float Z) freeze,
        (float X, float Z) bleed,
        string rowAnchor)
    {
        AddFourMemberGroup(placements, stunGroupId, stun.X, stun.Z,
            $"first-island/stun/{rowAnchor}",
            $"E{stunGroupId} is row {stunGroupId} of the left Stun lane at the {rowAnchor}.");
        AddFourMemberGroup(placements, freezeGroupId, freeze.X, freeze.Z,
            $"first-island/freeze/{rowAnchor}",
            $"E{freezeGroupId} is row {freezeGroupId - 4} of the centre Freeze lane at the {rowAnchor}.");
        AddFourMemberGroup(placements, bleedGroupId, bleed.X, bleed.Z,
            $"first-island/bleed/{rowAnchor}",
            $"E{bleedGroupId} is row {bleedGroupId - 8} of the right Bleed lane at the {rowAnchor}.");
    }

    private static void AddFourMemberGroup(
        ImmutableArray<MedusaIslandAuthoredPlacement>.Builder placements,
        int groupId,
        float centerX,
        float centerZ,
        string anchor,
        string rationale) =>
        AddGroupFormation(
            placements,
            groupId,
            centerX,
            centerZ,
            anchor,
            rationale,
            FourMemberFormation);

    private static void AddThreeMemberGroup(
        ImmutableArray<MedusaIslandAuthoredPlacement>.Builder placements,
        int groupId,
        float centerX,
        float centerZ,
        string anchor,
        string rationale) =>
        AddGroupFormation(
            placements,
            groupId,
            centerX,
            centerZ,
            anchor,
            rationale,
            ThreeMemberFormation);

    private static void AddGroupFormation(
        ImmutableArray<MedusaIslandAuthoredPlacement>.Builder placements,
        int groupId,
        float centerX,
        float centerZ,
        string anchor,
        string rationale,
        MedusaIslandPlacementFormation formation)
    {
        if (!MedusaIslandRosterPolicy.TryGetGroup(groupId, out var group))
        {
            throw new InvalidOperationException(
                $"Placement references missing Medusa group E{groupId}.");
        }

        var members = ImmutableArray.CreateBuilder<string>(
            1 + group.OrdinaryEscortSpawnIds.Length);
        members.Add(group.EliteSpawnId);
        members.AddRange(group.OrdinaryEscortSpawnIds);

        if (members.Count != formation.MemberOffsets.Length)
        {
            throw new InvalidOperationException(
                $"E{groupId} has {members.Count} members but formation " +
                $"{formation.Name} has {formation.MemberOffsets.Length} points.");
        }

        for (var index = 0; index < members.Count; index++)
        {
            var offset = formation.MemberOffsets[index];
            var memberRationale = index == 0
                ? "The elite holds the cluster center."
                : $"Normal escort {index} uses fixed formation offset " +
                  $"({offset.X:0.#},{offset.Z:0.#}).";
            AddExact(
                placements,
                members[index],
                centerX + offset.X,
                centerZ + offset.Z,
                $"{anchor}/E{groupId}",
                $"{rationale} {memberRationale}");
        }
    }

    private static void AddExact(
        ImmutableArray<MedusaIslandAuthoredPlacement>.Builder placements,
        string spawnId,
        float x,
        float z,
        string anchor,
        string rationale)
    {
        if (!MedusaIslandRosterPolicy.TryGetSpawn(spawnId, out var spawn))
        {
            throw new InvalidOperationException(
                $"Placement references missing Medusa spawn {spawnId}.");
        }

        if (!TryProjectToHmpBlock(x, z, out var blockCell))
        {
            throw new InvalidOperationException(
                $"Placement {spawnId} is outside the client block table.");
        }

        placements.Add(new(
            spawn.SpawnId,
            spawn.EliteGroupId,
            spawn.Island,
            spawn.Lane,
            x,
            z,
            anchor,
            rationale,
            MedusaIslandPlacementEvidenceLevel.ClientBlockTableUnblocked,
            MinimumClientBlockTableClearance,
            blockCell,
            DecodedHmpBlockValue: 0,
            DecodedHmpComponent: spawn.Island));
    }

    private static void Validate(
        ImmutableArray<MedusaIslandAuthoredPlacement> placements)
    {
        if (placements.Length != MedusaIslandRosterPolicy.TotalSpawnCount ||
            placements.Select(placement => placement.SpawnId)
                .Distinct(StringComparer.Ordinal).Count() != placements.Length)
        {
            throw new InvalidOperationException(
                "Medusa placement plan must contain 74 unique spawn identities.");
        }

        var rosterIds = MedusaIslandRosterPolicy.Spawns
            .Select(spawn => spawn.SpawnId)
            .Order(StringComparer.Ordinal);
        var placementIds = placements
            .Select(placement => placement.SpawnId)
            .Order(StringComparer.Ordinal);
        if (!placementIds.SequenceEqual(rosterIds, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "Medusa placement plan must cover the fixed roster exactly.");
        }

        foreach (var placement in placements)
        {
            if (!float.IsFinite(placement.X) || !float.IsFinite(placement.Z) ||
                MathF.Abs(placement.X) >= WorldHalfExtent ||
                MathF.Abs(placement.Z) >= WorldHalfExtent ||
                string.IsNullOrWhiteSpace(placement.Anchor) ||
                string.IsNullOrWhiteSpace(placement.Rationale) ||
                placement.EvidenceLevel !=
                    MedusaIslandPlacementEvidenceLevel
                        .ClientBlockTableUnblocked ||
                !placement.IsClientBlockTableUnblocked ||
                placement.VerifiedBlockedCellClearanceFloor <
                    MinimumClientBlockTableClearance ||
                placement.DecodedHmpBlockValue != 0 ||
                placement.DecodedHmpComponent != placement.Island ||
                !TryProjectToHmpBlock(
                    placement.X, placement.Z, out var blockCell) ||
                placement.HmpBlockCell != blockCell ||
                !MedusaIslandRosterPolicy.TryGetSpawn(
                    placement.SpawnId, out var roster) ||
                placement.EliteGroupId != roster.EliteGroupId ||
                placement.Island != roster.Island ||
                placement.Lane != roster.Lane)
            {
                throw new InvalidOperationException(
                    $"Invalid candidate placement for {placement.SpawnId}.");
            }
        }

        var minimumSquared =
            MinimumAuthoredSeparation * MinimumAuthoredSeparation;
        for (var first = 0; first < placements.Length; first++)
        {
            for (var second = first + 1; second < placements.Length; second++)
            {
                var deltaX = placements[first].X - placements[second].X;
                var deltaZ = placements[first].Z - placements[second].Z;
                if ((deltaX * deltaX) + (deltaZ * deltaZ) < minimumSquared)
                {
                    throw new InvalidOperationException(
                        $"Candidate placements {placements[first].SpawnId} and " +
                        $"{placements[second].SpawnId} are too close.");
                }
            }
        }
    }

    private static bool TryResolveMap(
        MedusaEncounterDifficulty difficulty,
        out MedusaIslandMapAssetEvidence asset) =>
        difficulty switch
        {
            MedusaEncounterDifficulty.Normal =>
                AssetsByMap.TryGetValue(204, out asset),
            MedusaEncounterDifficulty.Enhanced or
            MedusaEncounterDifficulty.Mythic =>
                AssetsByMap.TryGetValue(200, out asset),
            _ => FailAsset(out asset)
        };

    private static bool FailAsset(out MedusaIslandMapAssetEvidence asset)
    {
        asset = default;
        return false;
    }

    private static MedusaIslandMapAssetEvidence Asset(
        short mapId,
        string sceneKey,
        string hmpAssetPath,
        string minimapAssetPath) =>
        new(
            mapId,
            sceneKey,
            hmpAssetPath,
            CommonHmpSha256,
            minimapAssetPath,
            CommonMinimapSha256,
            TerrainCellsPerAxis,
            TerrainCellsPerAxis,
            TerrainCellSize,
            TerrainCellSize,
            MinimapPixelsPerAxis,
            MinimapPixelsPerAxis,
            HmpBlockCellsPerAxis,
            HmpBlockCellsPerAxis,
            HmpBlockByteOffset,
            HmpBlockByteLength,
            CommonHmpBlockSha256,
            SharesVerifiedGeometryWithOtherDifficulty: true,
            MinimapTopologyReviewed: true,
            HmpBlockTableDecoded: true,
            HmpBlockTransformCrossMapValidated: true,
            ClientBlockTableConsumerVerified: true);

    private sealed record PlacementContent(
        ImmutableArray<MedusaIslandAuthoredPlacement> Placements,
        FrozenDictionary<string, MedusaIslandAuthoredPlacement> BySpawnId);
}
