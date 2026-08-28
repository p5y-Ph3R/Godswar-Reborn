using System.Collections.Immutable;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Strength of the client evidence behind an authored world position.
/// </summary>
internal enum MedusaIslandPlacementEvidenceLevel : byte
{
    MinimapTopologyCandidate = 1,
    DecodedBlockMaskCandidate = 2,
    ClientBlockTableUnblocked = 3,
    ClientPlacementAccepted = 4
}

internal readonly record struct MedusaIslandClientBlockTableConsumerEvidence(
    string ExecutableSha256,
    string ExecutableFileVersion,
    uint ExecutablePeTimestamp,
    int ImageBase,
    int BlockAllocationEvidenceRva,
    int BlockLoaderRva,
    int IsBlockRva,
    int PlanarToWorldRva,
    int RuntimeTableFieldOffset,
    byte UnblockedValue,
    int VerifiedMovementConsumerCount,
    bool PlanarToWorldYIsZero);

internal readonly record struct MedusaIslandMapAssetEvidence(
    short MapId,
    string SceneKey,
    string HmpAssetPath,
    string HmpSha256,
    string MinimapAssetPath,
    string MinimapSha256,
    int TerrainCellsX,
    int TerrainCellsZ,
    float CellSizeX,
    float CellSizeZ,
    int MinimapWidth,
    int MinimapHeight,
    int HmpBlockCellsX,
    int HmpBlockCellsZ,
    int HmpBlockByteOffset,
    int HmpBlockByteLength,
    string HmpBlockSha256,
    bool SharesVerifiedGeometryWithOtherDifficulty,
    bool MinimapTopologyReviewed,
    bool HmpBlockTableDecoded,
    bool HmpBlockTransformCrossMapValidated,
    bool ClientBlockTableConsumerVerified)
{
    public float WorldWidth => TerrainCellsX * CellSizeX;

    public float WorldDepth => TerrainCellsZ * CellSizeZ;
}

/// <summary>
/// A fixed authored point on the common map-200/map-204 topology. Anchor and
/// rationale are mandatory so a future client acceptance pass can audit every
/// candidate rather than inheriting unexplained coordinates.
/// </summary>
internal sealed record MedusaIslandAuthoredPlacement(
    string SpawnId,
    int? EliteGroupId,
    MedusaIslandRosterIsland Island,
    MedusaIslandRosterLane Lane,
    float X,
    float Z,
    string Anchor,
    string Rationale,
    MedusaIslandPlacementEvidenceLevel EvidenceLevel,
    float VerifiedBlockedCellClearanceFloor,
    MedusaIslandHmpBlockCell HmpBlockCell,
    byte DecodedHmpBlockValue,
    MedusaIslandRosterIsland DecodedHmpComponent)
{
    public bool IsClientBlockTableUnblocked =>
        (EvidenceLevel is
            MedusaIslandPlacementEvidenceLevel.ClientBlockTableUnblocked or
            MedusaIslandPlacementEvidenceLevel.ClientPlacementAccepted) &&
        DecodedHmpBlockValue == 0;

    public bool IsLiveSpawnEligible =>
        EvidenceLevel ==
            MedusaIslandPlacementEvidenceLevel.ClientPlacementAccepted &&
        IsClientBlockTableUnblocked;
}

internal readonly record struct MedusaIslandResolvedPlacement(
    MedusaEncounterDifficulty Difficulty,
    short MapId,
    string SceneKey,
    MedusaIslandAuthoredPlacement Placement);

internal readonly record struct MedusaIslandMinimapPixel(
    int X,
    int Y);

internal readonly record struct MedusaIslandHmpBlockCell(
    int X,
    int Y);

internal enum MedusaIslandTraversalAnchorRole : byte
{
    EntranceLanding = 1,
    TransferSource = 2,
    TransferDestination = 3
}

internal sealed record MedusaIslandTraversalAnchor(
    string AnchorId,
    MedusaIslandRosterIsland Island,
    MedusaIslandTraversalAnchorRole Role,
    float X,
    float Z,
    string StaticAnchor,
    string Rationale,
    float VerifiedBlockedCellClearanceFloor,
    MedusaIslandPlacementEvidenceLevel BlockEvidenceLevel,
    MedusaIslandHmpBlockCell HmpBlockCell,
    byte DecodedHmpBlockValue,
    MedusaIslandRosterIsland DecodedHmpComponent,
    bool ClientTriggerCertified)
{
    public bool IsClientBlockTableUnblocked =>
        (BlockEvidenceLevel is
            MedusaIslandPlacementEvidenceLevel.ClientBlockTableUnblocked or
            MedusaIslandPlacementEvidenceLevel.ClientPlacementAccepted) &&
        DecodedHmpBlockValue == 0;
}

internal readonly record struct MedusaIslandPlacementFormation(
    string Name,
    ImmutableArray<(float X, float Z)> MemberOffsets);
