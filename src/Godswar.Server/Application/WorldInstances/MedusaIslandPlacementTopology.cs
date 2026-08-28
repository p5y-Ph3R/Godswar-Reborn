using System.Collections.Immutable;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Client block-table projection and traversal points confirmed by the
/// captured external Medusa run.
/// </summary>
internal static partial class MedusaIslandPlacementPolicy
{
    private static readonly ImmutableArray<MedusaIslandTraversalAnchor>
        TraversalEvidence = BuildTraversalEvidence();

    public static ImmutableArray<MedusaIslandTraversalAnchor> TraversalAnchors =>
        TraversalEvidence;

    public static bool HasClientCertifiedTraversal =>
        TraversalEvidence.Length == 5 &&
        TraversalEvidence.All(anchor => anchor.ClientTriggerCertified);

    public static bool TryGetTraversalAnchor(
        string? anchorId,
        out MedusaIslandTraversalAnchor anchor)
    {
        if (!string.IsNullOrWhiteSpace(anchorId))
        {
            var candidate = TraversalEvidence.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.AnchorId,
                    anchorId,
                    StringComparison.Ordinal));
            if (candidate is not null)
            {
                anchor = candidate;
                return true;
            }
        }

        anchor = null!;
        return false;
    }

    public static bool TryProjectToHmpBlock(
        float x,
        float z,
        out MedusaIslandHmpBlockCell cell)
    {
        if (!float.IsFinite(x) || !float.IsFinite(z) ||
            x < -WorldHalfExtent || x >= WorldHalfExtent ||
            z <= -WorldHalfExtent || z > WorldHalfExtent)
        {
            cell = default;
            return false;
        }

        cell = new(
            (int)MathF.Floor(
                (x + WorldHalfExtent) * HmpBlockCellsPerWorldUnit),
            (int)MathF.Floor(
                (WorldHalfExtent - z) * HmpBlockCellsPerWorldUnit));
        return cell.X is >= 0 and < HmpBlockCellsPerAxis &&
               cell.Y is >= 0 and < HmpBlockCellsPerAxis;
    }

    private static ImmutableArray<MedusaIslandTraversalAnchor>
        BuildTraversalEvidence()
    {
        ImmutableArray<MedusaIslandTraversalAnchor> anchors =
        [
            Anchor(
                "first-entry",
                MedusaIslandRosterIsland.First,
                MedusaIslandTraversalAnchorRole.EntranceLanding,
                212f,
                -217f,
                "gate-2 static at (221.27,-224.58)",
                "Captured opcode-10018 instance landing."),
            Anchor(
                "first-to-second-source",
                MedusaIslandRosterIsland.First,
                MedusaIslandTraversalAnchorRole.TransferSource,
                -33f,
                51f,
                "ring-3 static at (-34.26,53.53)",
                "Captured first-component ring transition source."),
            Anchor(
                "first-to-second-destination",
                MedusaIslandRosterIsland.Second,
                MedusaIslandTraversalAnchorRole.TransferDestination,
                -83f,
                101f,
                "ring-2 static at (-77.43,95.33)",
                "Captured opcode-10018 second-component landing."),
            Anchor(
                "second-to-final-source",
                MedusaIslandRosterIsland.Second,
                MedusaIslandTraversalAnchorRole.TransferSource,
                -128f,
                139f,
                "ring-1 static at (-130.75,140.66)",
                "Captured second-component ring transition source."),
            Anchor(
                "second-to-final-destination",
                MedusaIslandRosterIsland.Final,
                MedusaIslandTraversalAnchorRole.TransferDestination,
                -145f,
                152f,
                "final-component capture landing",
                "Captured opcode-10018 final-component landing.")
        ];

        if (anchors.Select(anchor => anchor.AnchorId)
                .Distinct(StringComparer.Ordinal).Count() != anchors.Length ||
            anchors.Any(anchor =>
                string.IsNullOrWhiteSpace(anchor.AnchorId) ||
                string.IsNullOrWhiteSpace(anchor.StaticAnchor) ||
                string.IsNullOrWhiteSpace(anchor.Rationale) ||
                anchor.VerifiedBlockedCellClearanceFloor <
                    MinimumClientBlockTableClearance ||
                anchor.BlockEvidenceLevel !=
                    MedusaIslandPlacementEvidenceLevel
                        .ClientPlacementAccepted ||
                !anchor.IsClientBlockTableUnblocked ||
                anchor.DecodedHmpComponent != anchor.Island ||
                !TryProjectToHmpBlock(
                    anchor.X, anchor.Z, out var blockCell) ||
                anchor.HmpBlockCell != blockCell ||
                !anchor.ClientTriggerCertified ||
                anchor.DecodedHmpBlockValue != 0))
        {
            throw new InvalidOperationException(
                "Invalid Medusa candidate traversal evidence.");
        }

        return anchors;
    }

    private static MedusaIslandTraversalAnchor Anchor(
        string anchorId,
        MedusaIslandRosterIsland island,
        MedusaIslandTraversalAnchorRole role,
        float x,
        float z,
        string staticAnchor,
        string rationale)
    {
        if (!TryProjectToHmpBlock(x, z, out var blockCell))
        {
            throw new InvalidOperationException(
                $"Traversal anchor {anchorId} is outside the block table.");
        }

        return new(
            anchorId,
            island,
            role,
            x,
            z,
            staticAnchor,
            rationale,
            MinimumClientBlockTableClearance,
            MedusaIslandPlacementEvidenceLevel.ClientPlacementAccepted,
            blockCell,
            DecodedHmpBlockValue: 0,
            DecodedHmpComponent: island,
            ClientTriggerCertified: true);
    }
}
