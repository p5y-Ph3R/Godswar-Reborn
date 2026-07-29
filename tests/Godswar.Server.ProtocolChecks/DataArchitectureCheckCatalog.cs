namespace Godswar.Server.ProtocolChecks;

internal static class DataArchitectureCheckCatalog
{
    public static readonly (string Name, Func<Task> Run)[] All =
    [
        (
            "Data-boundary architecture ratchet",
            DataBoundaryArchitectureChecks.RunAsync),
        (
            "Pinned immutable world-content reader",
            WorldContentReaderChecks.RunAsync),
        (
            "Frozen database-authoritative NPC content",
            NpcContentAuthorityChecks.RunAsync),
        (
            "Versioned character snapshot contract",
            CharacterSnapshotContractChecks.RunAsync),
        (
            "JSON consistent character snapshot reader",
            JsonCharacterSnapshotReaderChecks.RunAsync),
        (
            "Single-character slot mutation guard",
            CharacterSlotMutationChecks.RunAsync),
        (
            "Character snapshot query metrics",
            CharacterSnapshotMetricsChecks.RunAsync),
        (
            "Snapshot-backed character client bootstrap",
            CharacterSnapshotHandlerChecks.RunAsync),
        (
            "Legacy talent command envelope",
            LegacyTalentCommandEnvelopeChecks.RunAsync),
        (
            "Durable Gear Mentor Make Attribute Stone command contract",
            MakeAttributeStoneCommandContractChecks.RunAsync),
        (
            "Durable Gear Mentor material-conversion command contracts",
            GearMentorMaterialConversionCommandContractChecks.RunAsync),
        (
            "Durable Gear Mentor Decompose command contract",
            GearMentorDecomposeGearCommandContractChecks.RunAsync),
        (
            "Durable Gear Mentor pre-route replay handler",
            GearMentorDurableReplayHandlerChecks.RunAsync),
        (
            "Shared character-inventory outbox compatibility",
            CharacterInventoryOutboxConsumerChecks.RunAsync),
        (
            "PostgreSQL talent command precondition",
            PostgresTalentUpgradeIntegrationChecks.RunAsync)
    ];
}
