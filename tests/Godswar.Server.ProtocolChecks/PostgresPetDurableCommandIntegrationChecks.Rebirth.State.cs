namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetDurableCommandIntegrationChecks
{
    private sealed record PetRebirthState(
        short Level,
        long Experience,
        short CompletedRebirths,
        short RebirthsRemaining,
        long PetRevision,
        long InventoryRevision,
        long StandardSpiritCount,
        long RestrictedSpiritCount,
        decimal[] AddedSavvy,
        decimal[] BaseGrowth,
        decimal[] GrowthAcceleration,
        long[] StatRevisions,
        long RebirthAuditCount,
        long RebirthCommittedAuditCount,
        long RebirthRejectedAuditCount,
        int ConsumedStackCount,
        long ConsumedQuantity,
        long CommandAuditCount,
        long CommandInboxCount,
        long CommandOutboxCount,
        long InventoryLedgerCount,
        long InventoryReasonCount,
        long DuplicateCount,
        long ConflictCount,
        int SelectedMaterialTemplateId,
        int SelectedMaterialQuantity,
        int SurplusLevelCount,
        long CarriedExperience,
        long HistoricalSurplusExperience,
        long PreRebirthUnspentExperience);
}
