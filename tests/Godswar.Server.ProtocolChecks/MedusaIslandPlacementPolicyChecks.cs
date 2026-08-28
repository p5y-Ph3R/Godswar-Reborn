using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaIslandPlacementPolicyChecks
{
    public const string CheckName =
        "Medusa Island candidate topology placement plan";

    public static Task RunAsync()
    {
        CheckExactRosterCoverage();
        CheckLaneAndIslandLayout();
        CheckAssetEvidence();
        CheckTraversalEvidence();
        CheckLiveTraversalDetector();
        CheckDifficultyResolutionAndLiveGate();
        CheckAuthorshipProjection();
        CheckFailClosedLookups();
        return Task.CompletedTask;
    }

    private static void CheckExactRosterCoverage()
    {
        var placements = MedusaIslandPlacementPolicy.Placements;
        Check.Equal(136, placements.Length, "captured placement count");
        Check.Equal(136, placements.Select(placement => placement.SpawnId)
            .Distinct(StringComparer.Ordinal).Count(),
            "captured placement identities are unique");
        Check.True(placements.Select(placement => placement.SpawnId)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(MedusaIslandRosterPolicy.Spawns
                    .Select(spawn => spawn.SpawnId)
                    .Order(StringComparer.Ordinal), StringComparer.Ordinal),
            "captured placements cover the fixed roster exactly");
        Check.True(placements.All(placement =>
                !string.IsNullOrWhiteSpace(placement.Anchor) &&
                !string.IsNullOrWhiteSpace(placement.Rationale) &&
                placement.EvidenceLevel ==
                    MedusaIslandPlacementEvidenceLevel
                        .ClientPlacementAccepted &&
                placement.IsClientBlockTableUnblocked &&
                placement.VerifiedBlockedCellClearanceFloor >=
                    MedusaIslandPlacementPolicy
                        .MinimumClientBlockTableClearance &&
                placement.DecodedHmpBlockValue == 0 &&
                placement.DecodedHmpComponent == placement.Island &&
                MedusaIslandPlacementPolicy.TryProjectToHmpBlock(
                    placement.X, placement.Z, out var blockCell) &&
                placement.HmpBlockCell == blockCell &&
                placement.IsLiveSpawnEligible),
            "every captured point is block-clear and client-accepted for live use");

        foreach (var placement in placements)
        {
            var captured = MedusaIslandCapturedLayout.Spawns.Single(spawn =>
                spawn.SpawnId == placement.SpawnId);
            Check.True(MedusaIslandRosterPolicy.TryGetSpawn(
                    placement.SpawnId, out var roster) &&
                placement.EliteGroupId == roster.EliteGroupId &&
                placement.Island == roster.Island &&
                placement.Lane == roster.Lane &&
                placement.X == captured.X &&
                placement.Z == captured.Z,
                $"{placement.SpawnId} retains captured coordinates and roster metadata");
        }
    }

    private static void CheckLaneAndIslandLayout()
    {
        var placements = MedusaIslandPlacementPolicy.Placements;
        Check.True(
            placements.Count(placement => placement.Island ==
                MedusaIslandRosterIsland.First) == 54 &&
            placements.Count(placement => placement.Island ==
                MedusaIslandRosterIsland.Second) == 72 &&
            placements.Count(placement => placement.Island ==
                MedusaIslandRosterIsland.Final) == 10,
            "captured placements retain the 54/72/10 component split");

        CheckPoint("First-Normal-01", 164.247f, -169.563f);
        CheckPoint("E2-Elite", 146.058f, -207.949f);
        CheckPoint("Euryale", -100.369f, -12.834f);
        CheckPoint("Chrysaor", 30.527f, 112.286f);
        CheckPoint("E13-Elite", -91.855f, 109.868f);
        CheckPoint("E16-Elite", -100.915f, 113.748f);
        CheckPoint("Second-Axeman-01", -101.807f, 102.240f);
        CheckPoint("Second-Axeman-70", -115.848f, 136.126f);
        CheckPoint("Stheno", -172.951f, 175.696f);
        CheckPoint("Medusa", -169.270f, 190.744f);
    }

    private static void CheckAssetEvidence()
    {
        Check.Equal(2, MedusaIslandPlacementPolicy.Assets.Length,
            "map-200 and map-204 evidence records");
        Check.True(MedusaIslandPlacementPolicy.SharesVerifiedClientTopology,
            "shared HMP and minimap assets");
        Check.True(MedusaIslandPlacementPolicy.Assets.All(asset =>
                asset.HmpSha256 ==
                    MedusaIslandPlacementPolicy.CommonHmpSha256 &&
                asset.MinimapSha256 ==
                    MedusaIslandPlacementPolicy.CommonMinimapSha256 &&
                asset.TerrainCellsX == 128 &&
                asset.TerrainCellsZ == 128 &&
                asset.CellSizeX == 4f &&
                asset.CellSizeZ == 4f &&
                asset.WorldWidth == 512f &&
                asset.WorldDepth == 512f &&
                asset.MinimapWidth == 512 &&
                asset.MinimapHeight == 512 &&
                asset.HmpBlockCellsX == 2_048 &&
                asset.HmpBlockCellsZ == 2_048 &&
                asset.HmpBlockByteOffset == 356_684 &&
                asset.HmpBlockByteLength == 4_194_304 &&
                asset.HmpBlockSha256 ==
                    MedusaIslandPlacementPolicy.CommonHmpBlockSha256 &&
                asset.MinimapTopologyReviewed &&
                asset.HmpBlockTableDecoded &&
                asset.HmpBlockTransformCrossMapValidated &&
                asset.ClientBlockTableConsumerVerified),
            "asset evidence pins the client-consumed HMP block table");

        var consumer = MedusaIslandPlacementPolicy.ClientBlockTableConsumer;
        Check.True(MedusaIslandPlacementPolicy
                .HasVerifiedClientBlockTableConsumer &&
            consumer.ExecutableSha256 ==
                MedusaIslandPlacementPolicy.ClientExecutableSha256 &&
            consumer.ExecutableFileVersion == "2.46.0.2257" &&
            consumer.ExecutablePeTimestamp == 0x52AA79CAu &&
            consumer.ImageBase == 0x00400000 &&
            consumer.BlockLoaderRva == 0x000F7550 &&
            consumer.IsBlockRva == 0x000F4EC0 &&
            consumer.RuntimeTableFieldOffset == 0x688 &&
            consumer.UnblockedValue == 0 &&
            consumer.VerifiedMovementConsumerCount == 3 &&
            consumer.PlanarToWorldYIsZero,
            "binary-pinned consumer proves zero means is_block false");
    }

    private static void CheckTraversalEvidence()
    {
        var anchors = MedusaIslandPlacementPolicy.TraversalAnchors;
        Check.Equal(5, anchors.Length, "candidate traversal hard points");
        Check.True(anchors.All(anchor =>
                anchor.VerifiedBlockedCellClearanceFloor >=
                    MedusaIslandPlacementPolicy
                        .MinimumClientBlockTableClearance &&
                anchor.BlockEvidenceLevel ==
                    MedusaIslandPlacementEvidenceLevel
                        .ClientPlacementAccepted &&
                anchor.IsClientBlockTableUnblocked &&
                anchor.DecodedHmpBlockValue == 0 &&
                anchor.DecodedHmpComponent == anchor.Island &&
                MedusaIslandPlacementPolicy.TryProjectToHmpBlock(
                    anchor.X, anchor.Z, out var blockCell) &&
                anchor.HmpBlockCell == blockCell &&
                anchor.ClientTriggerCertified) &&
            MedusaIslandPlacementPolicy.HasClientCertifiedTraversal,
            "all five hard points are capture-backed and client-certified");
        Check.True(MedusaIslandPlacementPolicy.TryGetTraversalAnchor(
                "first-entry", out var entrance) &&
            entrance.Island == MedusaIslandRosterIsland.First &&
            entrance.Role ==
                MedusaIslandTraversalAnchorRole.EntranceLanding &&
            entrance.X == 212f && entrance.Z == -217f &&
            entrance.HmpBlockCell ==
                new MedusaIslandHmpBlockCell(1_872, 1_892),
            "captured entry landing is explicit");
        Check.True(MedusaIslandPlacementPolicy.TryGetTraversalAnchor(
                "first-to-second-source", out var ring3) &&
            ring3.Island == MedusaIslandRosterIsland.First &&
            MedusaIslandPlacementPolicy.TryGetTraversalAnchor(
                "first-to-second-destination", out var ring2) &&
            ring2.Island == MedusaIslandRosterIsland.Second &&
            MedusaIslandPlacementPolicy.TryGetTraversalAnchor(
                "second-to-final-source", out var ring1) &&
            ring1.Island == MedusaIslandRosterIsland.Second &&
            MedusaIslandPlacementPolicy.TryGetTraversalAnchor(
                "second-to-final-destination", out var finalLanding) &&
            finalLanding.Island == MedusaIslandRosterIsland.Final &&
            ring3.HmpBlockCell == new MedusaIslandHmpBlockCell(892, 820) &&
            ring2.HmpBlockCell == new MedusaIslandHmpBlockCell(692, 620) &&
            ring1.HmpBlockCell == new MedusaIslandHmpBlockCell(512, 468) &&
            finalLanding.HmpBlockCell ==
                new MedusaIslandHmpBlockCell(444, 416),
            "captured ring transitions retain component identity");
    }

    private static void CheckDifficultyResolutionAndLiveGate()
    {
        CheckResolved(MedusaEncounterDifficulty.Normal, 204,
            "Medusa_Island2");
        CheckResolved(MedusaEncounterDifficulty.Enhanced, 200,
            "Medusa_Island");
        CheckResolved(MedusaEncounterDifficulty.Mythic, 200,
            "Medusa_Island");

        Check.True(MedusaIslandPlacementPolicy
                       .HasClientBlockTableUnblockedPlacements &&
                   MedusaIslandPlacementPolicy
                       .HasClientPlacementAcceptedPlacements &&
                   MedusaIslandPlacementPolicy.HasClientCertifiedTraversal &&
                   MedusaIslandPlacementPolicy.Placements.All(placement =>
                       MedusaIslandPlacementPolicy.TryResolveLiveCertified(
                           MedusaEncounterDifficulty.Normal,
                           placement.SpawnId,
                           out _)) &&
                   MedusaIslandPlacementPolicy.Placements.All(placement =>
                       MedusaIslandPlacementPolicy.TryResolveLiveCertified(
                           MedusaEncounterDifficulty.Enhanced,
                           placement.SpawnId,
                           out _)) &&
                   MedusaIslandPlacementPolicy.Placements.All(placement =>
                       MedusaIslandPlacementPolicy.TryResolveLiveCertified(
                           MedusaEncounterDifficulty.Mythic,
                           placement.SpawnId,
                           out _)),
            "all captured points resolve through the live placement gate");
    }

    private static void CheckAuthorshipProjection()
    {
        Check.True(MedusaIslandPlacementPolicy.TryProjectToMinimap(
            212f, -217f, out var entrance) &&
            entrance == new MedusaIslandMinimapPixel(255, 430),
            "captured entrance projects onto the decoded minimap");
        Check.True(MedusaIslandPlacementPolicy.TryProjectToMinimap(
                -160f, 175f, out var finalCore) &&
            finalCore == new MedusaIslandMinimapPixel(258, 124),
            "final-island core projects onto the decoded minimap");
        Check.True(MedusaIslandPlacementPolicy.TryProjectToMinimap(
                -256f, 256f, out var northWest) &&
            northWest == new MedusaIslandMinimapPixel(250, 55),
            "world envelope uses the client map transform");
        Check.True(MedusaIslandPlacementPolicy.TryProjectToHmpBlock(
                0f, 0f, out var origin) &&
            origin == new MedusaIslandHmpBlockCell(1_024, 1_024) &&
            MedusaIslandPlacementPolicy.TryProjectToHmpBlock(
                -256f, 256f, out var hmpNorthWest) &&
            hmpNorthWest == new MedusaIslandHmpBlockCell(0, 0),
            "world coordinates use the validated four-block-cells-per-unit transform");
    }

    private static void CheckResolved(
        MedusaEncounterDifficulty difficulty,
        short mapId,
        string sceneKey)
    {
        Check.True(MedusaIslandPlacementPolicy.TryResolveCandidate(
                difficulty, "Medusa", out var resolved) &&
            resolved.Difficulty == difficulty &&
            resolved.MapId == mapId &&
            resolved.SceneKey == sceneKey &&
            resolved.Placement.SpawnId == "Medusa" &&
            resolved.Placement.IsLiveSpawnEligible,
            $"{difficulty} resolves captured coordinates through explicit instance metadata");
    }

    private static void CheckPoint(string spawnId, float x, float z)
    {
        var placement = Placement(spawnId);
        Check.True(placement.X == x && placement.Z == z,
            $"{spawnId} has deterministic authored coordinates");
    }

    private static MedusaIslandAuthoredPlacement Placement(string spawnId)
    {
        Check.True(MedusaIslandPlacementPolicy.TryGetCandidate(
                spawnId, out var placement),
            $"candidate {spawnId} resolves");
        return placement;
    }
}
