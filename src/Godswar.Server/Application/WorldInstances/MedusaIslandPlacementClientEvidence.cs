namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Binary-pinned evidence for the installed client's HMP block-table
/// consumer. This proves only the table query and flat CTerrain projection;
/// it does not certify static-mesh placement or transport triggers.
/// </summary>
internal static partial class MedusaIslandPlacementPolicy
{
    public const string ClientExecutableSha256 =
        "C80FC15418BC1865731105AE05CE96DA3015FEC9E8E51337263D1C475301EEEE";

    public const string ClientTerrainAuditDocumentPath =
        "docs/medusa-island-client-terrain-audit.md";

    public static MedusaIslandClientBlockTableConsumerEvidence
        ClientBlockTableConsumer { get; } = new(
            ClientExecutableSha256,
            "2.46.0.2257",
            0x52AA79CAu,
            ImageBase: 0x00400000,
            BlockAllocationEvidenceRva: 0x000F2F04,
            BlockLoaderRva: 0x000F7550,
            IsBlockRva: 0x000F4EC0,
            PlanarToWorldRva: 0x000F31D0,
            RuntimeTableFieldOffset: 0x688,
            UnblockedValue: 0,
            VerifiedMovementConsumerCount: 3,
            PlanarToWorldYIsZero: true);

    public static bool HasVerifiedClientBlockTableConsumer =>
        ClientBlockTableConsumer is
        {
            ExecutableSha256: ClientExecutableSha256,
            ExecutableFileVersion: "2.46.0.2257",
            ExecutablePeTimestamp: 0x52AA79CAu,
            ImageBase: 0x00400000,
            BlockAllocationEvidenceRva: 0x000F2F04,
            BlockLoaderRva: 0x000F7550,
            IsBlockRva: 0x000F4EC0,
            PlanarToWorldRva: 0x000F31D0,
            RuntimeTableFieldOffset: 0x688,
            UnblockedValue: 0,
            VerifiedMovementConsumerCount: 3,
            PlanarToWorldYIsZero: true
        } &&
        AssetEvidence.All(asset =>
            asset.ClientBlockTableConsumerVerified &&
            asset.HmpBlockTableDecoded &&
            asset.HmpBlockSha256 == CommonHmpBlockSha256);
}
