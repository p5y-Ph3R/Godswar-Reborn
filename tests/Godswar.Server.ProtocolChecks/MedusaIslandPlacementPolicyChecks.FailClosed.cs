using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaIslandPlacementPolicyChecks
{
    private static void CheckFailClosedLookups()
    {
        Check.True(!MedusaIslandPlacementPolicy.TryGetCandidate(null, out _) &&
                   !MedusaIslandPlacementPolicy.TryGetCandidate("", out _) &&
                   !MedusaIslandPlacementPolicy.TryGetCandidate(
                       "E21-Elite", out _),
            "unknown candidate identities fail closed");
        Check.True(!MedusaIslandPlacementPolicy.TryGetTraversalAnchor(
                       null, out _) &&
                   !MedusaIslandPlacementPolicy.TryGetTraversalAnchor(
                       "unknown", out _),
            "unknown traversal anchors fail closed");
        Check.True(!MedusaIslandPlacementPolicy.TryGetAssetEvidence(199, out _) &&
                   !MedusaIslandPlacementPolicy.TryGetAssetEvidence(
                       int.MaxValue, out _),
            "unknown map identities fail closed");
        Check.True(!MedusaIslandPlacementPolicy.TryResolveCandidate(
                       (MedusaEncounterDifficulty)byte.MaxValue,
                       "Medusa", out _) &&
                   !MedusaIslandPlacementPolicy.TryResolveCandidate(
                       MedusaEncounterDifficulty.Normal,
                       "unknown", out _),
            "unknown difficulty and spawn fail closed");
        Check.True(!MedusaIslandPlacementPolicy.TryProjectToMinimap(
                       float.NaN, 0f, out _) &&
                   !MedusaIslandPlacementPolicy.TryProjectToMinimap(
                       256f, 0f, out _) &&
                   !MedusaIslandPlacementPolicy.TryProjectToMinimap(
                       0f, -256f, out _),
            "outside minimap coordinates fail closed");
        Check.True(!MedusaIslandPlacementPolicy.TryProjectToHmpBlock(
                       float.NaN, 0f, out _) &&
                   !MedusaIslandPlacementPolicy.TryProjectToHmpBlock(
                       256f, 0f, out _) &&
                   !MedusaIslandPlacementPolicy.TryProjectToHmpBlock(
                       0f, -256f, out _),
            "outside block coordinates fail closed");
    }
}
