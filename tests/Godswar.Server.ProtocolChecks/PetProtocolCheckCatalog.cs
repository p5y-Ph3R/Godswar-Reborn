namespace Godswar.Server.ProtocolChecks;

internal static class PetProtocolCheckCatalog
{
    public static readonly (string Name, Func<Task> Run)[] All =
    [
        (
            "PostgreSQL pet migration safety",
            PostgresPetMigrationChecks.RunAsync),
        (
            "PostgreSQL pet growth archive safety",
            PostgresPetGrowthArchiveMigrationChecks.RunAsync),
        (
            "PostgreSQL pet growth v2 migration",
            PostgresPetGrowthV2MigrationIntegrationChecks.RunAsync),
        (
            "PostgreSQL pet initial-savvy migration safety",
            PostgresPetInitialSavvyMigrationChecks.RunAsync),
        (
            "PostgreSQL pet initial-savvy migration integration",
            PostgresPetInitialSavvyMigrationIntegrationChecks.RunAsync),
        (
            "PostgreSQL pet savvy semantics migration safety",
            PostgresPetSavvySemanticsMigrationChecks.RunAsync),
        (
            "PostgreSQL pet savvy hardening migration safety",
            PostgresPetSavvyHardeningMigrationChecks.RunAsync),
        (
            "PostgreSQL pet savvy hardening migration integration",
            PostgresPetSavvyHardeningMigrationIntegrationChecks.RunAsync),
        (
            "PostgreSQL pet-level migration safety",
            PostgresPetLevelMigrationChecks.RunAsync),
        (
            "PostgreSQL pet-level migration rollback",
            PostgresPetLevelMigrationIntegrationChecks.RunAsync),
        (
            "PostgreSQL pet savvy semantics migration integration",
            PostgresPetSavvySemanticsMigrationIntegrationChecks.RunAsync),
        (
            "Authoritative pet-system foundation",
            PetSystemFoundationChecks.RunAsync),
        (
            "Authoritative pet aptitude catalog",
            PetAptitudeCatalogChecks.RunAsync),
        (
            "Native species aptitude profiles",
            PetNativeAptitudeProfileChecks.RunAsync),
        (
            "Quality-derived pet growth policy",
            PetGrowthPolicyChecks.RunAsync),
        (
            "Historical pet initial-savvy policy",
            PetInitialSavvyPolicyChecks.RunAsync),
        (
            "Rarity-derived pet added-savvy policy",
            PetAddedSavvyPolicyChecks.RunAsync),
        (
            "Cumulative pet rebirth growth policy",
            PetRebirthGrowthPolicyChecks.RunAsync),
        (
            "Native pet level-up curve and protocol",
            PetLevelUpgradeProtocolChecks.RunAsync),
        (
            "PostgreSQL authoritative pet level-up",
            PostgresPetLevelUpgradeIntegrationChecks.RunAsync),
        (
            "Pet savvy runtime safety",
            PetSavvyRuntimeSafetyChecks.RunAsync),
        (
            "Authoritative pet-egg hatch protocol",
            PetEggHatchProtocolChecks.RunAsync),
        (
            "PostgreSQL pet-egg hatch transaction",
            PetEggHatchPersistenceChecks.RunAsync),
        (
            "Owned-pet bootstrap persistence",
            PetBootstrapPersistenceChecks.RunAsync),
        (
            "Owned-pet native protocol and login ordering",
            OwnedPetListProtocolChecks.RunAsync),
        (
            "Authoritative pet carry, summon, and recall",
            PetPresenceProtocolChecks.RunAsync)
    ];
}
