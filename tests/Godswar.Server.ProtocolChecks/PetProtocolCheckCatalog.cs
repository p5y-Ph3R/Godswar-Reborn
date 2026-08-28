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
            "PostgreSQL pet growth/Savvy v2 migration safety",
            PostgresPetGrowthSavvySemanticsV2MigrationChecks.RunAsync),
        (
            "PostgreSQL pet growth/Savvy v2 migration integration",
            PostgresPetGrowthSavvySemanticsV2MigrationIntegrationChecks.RunAsync),
        (
            "PostgreSQL pet initial-Savvy V3 migration safety",
            PostgresPetInitialSavvyV3MigrationChecks.RunAsync),
        (
            "PostgreSQL pet initial-Savvy V3 migration integration",
            PostgresPetInitialSavvyV3MigrationIntegrationChecks.RunAsync),
        (
            "PostgreSQL pet Phoenix Growth migration safety",
            PostgresPetPhoenixGrowthMigrationChecks.RunAsync),
        (
            "PostgreSQL pet Phoenix Growth migration integration",
            PostgresPetPhoenixGrowthMigrationIntegrationChecks.RunAsync),
        (
            "PostgreSQL pet scaled Added-value V3 migration safety",
            PostgresPetScaledAddedValueMigrationChecks.RunAsync),
        (
            "PostgreSQL pet scaled Added-value V3 migration integration",
            PostgresPetScaledAddedValueMigrationIntegrationChecks.RunAsync),
        (
            PostgresPetBasicSavvyPreviewMigrationChecks.CheckName,
            PostgresPetBasicSavvyPreviewMigrationChecks.RunAsync),
        (
            PostgresPetRankContentMigrationChecks.CheckName,
            PostgresPetRankContentMigrationChecks.RunAsync),
        (
            PostgresPetRankContentMigrationIntegrationChecks.CheckName,
            PostgresPetRankContentMigrationIntegrationChecks.RunAsync),
        (
            PostgresPetHatchEvidenceHardeningMigrationChecks.CheckName,
            PostgresPetHatchEvidenceHardeningMigrationChecks.RunAsync),
        (
            PostgresPetHatchEvidenceHardeningIntegrationChecks.CheckName,
            PostgresPetHatchEvidenceHardeningIntegrationChecks.RunAsync),
        (
            PostgresPetLearnedSkillContentMigrationChecks.CheckName,
            PostgresPetLearnedSkillContentMigrationChecks.RunAsync),
        (
            PostgresPetLearnedSkillContentPublicationIntegrationChecks.CheckName,
            PostgresPetLearnedSkillContentPublicationIntegrationChecks.RunAsync),
        (
            PostgresPetMergeSavvyLookupContentMigrationChecks.CheckName,
            PostgresPetMergeSavvyLookupContentMigrationChecks.RunAsync),
        (
            PostgresPetMagicJadeAppearanceMigrationChecks.CheckName,
            PostgresPetMagicJadeAppearanceMigrationChecks.RunAsync),
        (
            PostgresPetBindMigrationChecks.CheckName,
            PostgresPetBindMigrationChecks.RunAsync),
        (
            PostgresPetSoulContractMigrationChecks.CheckName,
            PostgresPetSoulContractMigrationChecks.RunAsync),
        (
            PostgresPetManagerUtilityMigrationChecks.CheckName,
            PostgresPetManagerUtilityMigrationChecks.RunAsync),
        (
            PostgresPackedSealOwnershipMigrationChecks.CheckName,
            PostgresPackedSealOwnershipMigrationChecks.RunAsync),
        (
            PostgresPetDurableCommandIntegrationChecks
                .PackedSealOwnershipCheckName,
            PostgresPetDurableCommandIntegrationChecks
                .RunPackedSealOwnershipOnlyAsync),
        (
            PostgresBagConsumableCooldownMigrationChecks.CheckName,
            PostgresBagConsumableCooldownMigrationChecks.RunAsync),
        (
            PostgresPetPhoenixRebirthBracketMigrationChecks.CheckName,
            PostgresPetPhoenixRebirthBracketMigrationChecks.RunAsync),
        (
            "PostgreSQL innate pet-talent migration safety",
            PostgresPetInnateTalentMigrationChecks.RunAsync),
        (
            "PostgreSQL pet-consumable projection migration safety",
            PostgresPetConsumableProjectionMigrationChecks.RunAsync),
        (
            "PostgreSQL pet owner-Merge content migration safety",
            PostgresPetOwnerMergeContentMigrationChecks.RunAsync),
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
            "Database-pinned pet merge Savvy policy",
            PetMergeSavvyPolicyChecks.RunAsync),
        (
            PetMergeSavvyLookupContentChecks.CheckName,
            PetMergeSavvyLookupContentChecks.RunAsync),
        (
            "Database-pinned pet Merge rank policy",
            PetMergeRankPolicyChecks.RunAsync),
        (
            PetHatchRankPolicyChecks.CheckName,
            PetHatchRankPolicyChecks.RunAsync),
        (
            "Native durable pet-to-pet Merge",
            PetToPetMergeProtocolChecks.RunAsync),
        (
            "Server-authoritative pet owner Merge",
            PetOwnerMergeChecks.RunAsync),
        (
            "Database-owned pet owner-Merge baseline",
            PetOwnerMergeContentBaselineChecks.RunAsync),
        (
            "Native innate owner-Merge request boundary",
            PetOwnerMergeNativeRequestChecks.RunAsync),
        (
            "Native pet owner-Merge lifecycle packets",
            PetOwnerMergeLifecyclePacketChecks.RunAsync),
        (
            "Native pet owner-Merge handler projection",
            PetOwnerMergeProjectionChecks.RunAsync),
        (
            "Native pet-energy projection",
            PetEnergyPacketChecks.RunAsync),
        (
            "Authoritative pet aptitude catalog",
            PetAptitudeCatalogChecks.RunAsync),
        (
            "Stock innate pet-talent mask catalog",
            PetTalentCatalogChecks.RunAsync),
        (
            "Quality-derived innate pet talents",
            PetInnateTalentPolicyChecks.RunAsync),
        (
            "Authoritative pet skill-cell policy",
            PetSkillSlotPolicyChecks.RunAsync),
        (
            PetLearnedSkillContentChecks.CheckName,
            PetLearnedSkillContentChecks.RunAsync),
        (
            PetLearnedSkillOwnerStatProjectionChecks.CheckName,
            PetLearnedSkillOwnerStatProjectionChecks.RunAsync),
        (
            PetSkillBookActivationPolicyChecks.CheckName,
            PetSkillBookActivationPolicyChecks.RunAsync),
        (
            "Stock Pet Manager dialogue protocol",
            PetManagerProtocolChecks.RunAsync),
        (
            PetManagerUtilityProtocolChecks.CheckName,
            PetManagerUtilityProtocolChecks.RunAsync),
        (
            PetManagerUtilityHandlerChecks.CheckName,
            PetManagerUtilityHandlerChecks.RunAsync),
        (
            PetAppearanceChangeProtocolChecks.CheckName,
            PetAppearanceChangeProtocolChecks.RunAsync),
        (
            PetAppearanceRefreshProtocolChecks.CheckName,
            PetAppearanceRefreshProtocolChecks.RunAsync),
        (
            PetAppearanceChangeHandlerChecks.CheckName,
            PetAppearanceChangeHandlerChecks.RunAsync),
        (
            PetBindProtocolChecks.CheckName,
            PetBindProtocolChecks.RunAsync),
        (
            PetBindHandlerChecks.CheckName,
            PetBindHandlerChecks.RunAsync),
        (
            "Authoritative Pet Manager skill-unlearn handler",
            PetManagerSkillUnlearnHandlerChecks.RunAsync),
        (
            PetBasicSavvyResetHandlerChecks.CheckName,
            PetBasicSavvyResetHandlerChecks.RunAsync),
        (
            "Native species aptitude profiles",
            PetNativeAptitudeProfileChecks.RunAsync),
        (
            "Quality-derived pet growth policy",
            PetGrowthPolicyChecks.RunAsync),
        (
            "Quality-derived pet Savvy policy",
            PetInitialSavvyPolicyChecks.RunAsync),
        (
            PetBasicSavvyRedistributionPolicyChecks.CheckName,
            PetBasicSavvyRedistributionPolicyChecks.RunAsync),
        (
            "Historical compatibility-only added-savvy policy",
            PetAddedSavvyPolicyChecks.RunAsync),
        (
            "Cumulative pet rebirth growth policy",
            PetRebirthGrowthPolicyChecks.RunAsync),
        (
            "Phoenix completed-Rebirth bracket policy",
            PetPhoenixRebirthModifierPolicyChecks.RunAsync),
        (
            "Native durable pet rebirth protocol",
            PetRebirthProtocolChecks.RunAsync),
        (
            PetSoulContractProtocolChecks.CheckName,
            PetSoulContractProtocolChecks.RunAsync),
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
            "Raw-local stock-client pet hatch boundary",
            PetRawLocalProtocolChecks.RunAsync),
        (
            PetCaptureContractChecks.CheckName,
            PetCaptureContractChecks.RunAsync),
        (
            PostgresPetDurableCommandIntegrationChecks
                .CaptureRarityCheckName,
            PostgresPetDurableCommandIntegrationChecks
                .RunCaptureRarityOnlyAsync),
        (
            "Authoritative pet skill-cell item activation",
            PetSkillCellItemProtocolChecks.RunAsync),
        (
            "Authoritative Morning Dew pet EXP activation",
            PetExperienceItemProtocolChecks.RunAsync),
        (
            "Authoritative Special Pet Shed expansion",
            PetShedExpansionProtocolChecks.RunAsync),
        (
            "PostgreSQL pet-egg hatch transaction",
            PetEggHatchPersistenceChecks.RunAsync),
        (
            PostgresPetDurableCommandIntegrationChecks.HatchRankCheckName,
            PostgresPetDurableCommandIntegrationChecks
                .RunHatchRankOnlyAsync),
        (
            PostgresPetDurableCommandIntegrationChecks
                .ConsumableCooldownCheckName,
            PostgresPetDurableCommandIntegrationChecks
                .RunConsumableCooldownOnlyAsync),
        (
            PostgresPetDurableCommandIntegrationChecks
                .PetMergeRankCheckName,
            PostgresPetDurableCommandIntegrationChecks
                .RunPetMergeRankOnlyAsync),
        (
            PostgresPetDurableCommandIntegrationChecks.SkillBookCheckName,
            PostgresPetDurableCommandIntegrationChecks
                .RunSkillBookOnlyAsync),
        (
            PostgresPetDurableCommandIntegrationChecks.BoundSealCheckName,
            PostgresPetDurableCommandIntegrationChecks
                .RunBoundSealOnlyAsync),
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
