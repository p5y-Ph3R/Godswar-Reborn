namespace Godswar.Server.ProtocolChecks;

internal static class DataArchitectureCheckCatalog
{
    public static readonly (string Name, Func<Task> Run)[] All =
    [
        (
            "Data-boundary architecture ratchet",
            DataBoundaryArchitectureChecks.RunAsync),
        (
            B20LegacyPersistenceArchitectureChecks.CheckName,
            B20LegacyPersistenceArchitectureChecks.RunAsync),
        (
            B20LegacyPersistenceTelemetryArchitectureChecks.CheckName,
            B20LegacyPersistenceTelemetryArchitectureChecks.RunAsync),
        (
            LegacyPersistenceMetricsChecks.CheckName,
            LegacyPersistenceMetricsChecks.RunAsync),
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
            JsonSemanticGatewayDataSessionChecks.CheckName,
            JsonSemanticGatewayDataSessionChecks.RunAsync),
        (
            "Single-character slot mutation guard",
            CharacterSlotMutationChecks.RunAsync),
        (
            "Character snapshot query metrics",
            CharacterSnapshotMetricsChecks.RunAsync),
        (
            "Bounded character checkpoint coordinator",
            CharacterCheckpointCoordinatorChecks.RunAsync),
        (
            "Player ownership command-envelope contract",
            PlayerOwnershipContractChecks.RunAsync),
        (
            "Durable player ownership architecture ratchet",
            PlayerOwnershipArchitectureChecks.RunAsync),
        (
            "Fail-closed durable registry composition",
            DurableRegistryCompositionChecks.RunAsync),
        (
            DeferredRedisArchitectureChecks.CheckName,
            DeferredRedisArchitectureChecks.RunAsync),
        (
            B17WorkerCoordinationRuntimeChecks.CheckName,
            B17WorkerCoordinationRuntimeChecks.RunAsync),
        (
            B17CoordinationConfigurationChecks.CheckName,
            B17CoordinationConfigurationChecks.RunAsync),
        (
            RedisCoordinationMetricsChecks.CheckName,
            RedisCoordinationMetricsChecks.RunAsync),
        (
            RedisGameTicketStoreIntegrationChecks.CheckName,
            RedisGameTicketStoreIntegrationChecks.RunAsync),
        (
            RedisWorkerCoordinationIntegrationChecks.CheckName,
            RedisWorkerCoordinationIntegrationChecks.RunAsync),
        (
            RedisSemanticGatewayCoordinationIntegrationChecks.CheckName,
            RedisSemanticGatewayCoordinationIntegrationChecks.RunAsync),
        (
            WorldInstancePlacementChecks.CheckName,
            WorldInstancePlacementChecks.RunAsync),
        (
            WorldInstanceMapIdentityChecks.CheckName,
            WorldInstanceMapIdentityChecks.RunAsync),
        (
            WorldInstanceRuntimeOptionsChecks.CheckName,
            WorldInstanceRuntimeOptionsChecks.RunAsync),
        (
            BoundedSingleOwnerMailboxChecks.CheckName,
            BoundedSingleOwnerMailboxChecks.RunAsync),
        (
            WorldInstanceRuntimeDirectoryChecks.CheckName,
            WorldInstanceRuntimeDirectoryChecks.RunAsync),
        (
            WorldInstanceSessionRoutingChecks.CheckName,
            WorldInstanceSessionRoutingChecks.RunAsync),
        (
            MonsterWorldOwnerRoutingChecks.CheckName,
            MonsterWorldOwnerRoutingChecks.RunAsync),
        (
            WorldInstanceEgressRevalidationChecks.CheckName,
            WorldInstanceEgressRevalidationChecks.RunAsync),
        (
            RelayGatewayChecks.CheckName,
            RelayGatewayChecks.RunAsync),
        (
            SemanticGatewayChecks.CheckName,
            SemanticGatewayChecks.RunAsync),
        (
            SemanticGatewayChecks.LoginLifecycleCheckName,
            SemanticGatewayChecks.RunLoginLifecycleAsync),
        (
            BackhaulProtocolChecks.CheckName,
            BackhaulProtocolChecks.RunAsync),
        (
            BackhaulProtocolChecks.HostIntegrationCheckName,
            BackhaulProtocolChecks.RunHostIntegrationAsync),
        (
            BackhaulProtocolChecks.RuntimeOptionsCheckName,
            BackhaulProtocolChecks.RunRuntimeOptionsAsync),
        (
            "Game-handler checkpoint lifecycle",
            GameHandlerCheckpointLifecycleChecks.RunAsync),
        (
            "Replacement-session leave ownership",
            PlayerOwnershipSessionRaceChecks.RunAsync),
        (
            "Replacement-session gameplay effect ownership",
            PlayerOwnershipGameplayRaceChecks.RunAsync),
        (
            "PostgreSQL character checkpoint migration contract",
            PostgresCharacterCheckpointMigrationChecks.RunAsync),
        (
            "PostgreSQL character lifecycle migration contract",
            PostgresCharacterLifecycleMigrationChecks.RunAsync),
        (
            PostgresRealmMigrationChecks.CheckName,
            PostgresRealmMigrationChecks.RunAsync),
        (
            "Durable character lifecycle command contracts",
            CharacterLifecycleCommandContractChecks.RunAsync),
        (
            CharacterLifecycleDurableHandlerChecks.CheckName,
            CharacterLifecycleDurableHandlerChecks.RunAsync),
        (
            "Monster-death reward migration contract",
            ProgressionRewardMigrationChecks.RunAsync),
        (
            "Durable monster-death reward command contract",
            MonsterDeathRewardCommandContractChecks.RunAsync),
        (
            "Monster-death reward commit-before-delivery boundary",
            MonsterDeathRewardCommitBoundaryChecks.RunAsync),
        (
            "Durable online progression interval settlement",
            ProgressionIntervalSettlementChecks.RunAsync),
        (
            "Durable pet command contracts",
            PetDurableCommandContractChecks.RunAsync),
        (
            "Snapshot-backed character client bootstrap",
            CharacterSnapshotHandlerChecks.RunAsync),
        (
            "Legacy talent command envelope",
            LegacyTalentCommandEnvelopeChecks.RunAsync),
        (
            "Durable Zodiac skill-grid activation command contract",
            ZodiacSkillGridActivationCommandContractChecks.RunAsync),
        (
            "Durable Zodiac skill-grid activation handler",
            ZodiacSkillGridActivationDurableHandlerChecks.RunAsync),
        (
            "Durable Zodiac activation persistence contracts",
            ZodiacSkillGridActivationPersistenceChecks.RunAsync),
        (
            "Durable Zodiac skill-grid upgrade command contract",
            ZodiacSkillGridUpgradeCommandContractChecks.RunAsync),
        (
            "Durable Zodiac skill-grid upgrade handler and replay",
            ZodiacSkillGridUpgradeDurableHandlerChecks.RunAsync),
        (
            "Durable Zodiac upgrade persistence contracts",
            ZodiacSkillGridUpgradePersistenceChecks.RunAsync),
        (
            "Durable Zodiac skill-grid selection contracts",
            ZodiacSkillGridSelectionCommandContractChecks.RunAsync),
        (
            "Durable Zodiac skill-grid selection handler and replay",
            ZodiacSkillGridSelectionDurableHandlerChecks.RunAsync),
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
            "Durable Gear Enhancement command contract",
            GearEnhancementCommandContractChecks.RunAsync),
        (
            "Durable Gear Mentor pre-route replay handler",
            GearMentorDurableReplayHandlerChecks.RunAsync),
        (
            "Durable equipment-forge handler and replay",
            EquipmentForgeDurableHandlerChecks.RunAsync),
        (
            "Durable equipment-forge command contract",
            EquipmentForgeCommandContractChecks.RunAsync),
        (
            "Durable kit-bag item-delete command contract",
            KitBagItemDeleteCommandContractChecks.RunAsync),
        (
            "Durable kit-bag item delete handler and replay",
            KitBagItemDeleteDurableHandlerChecks.RunAsync),
        (
            "Durable kit-bag item-move command contract",
            KitBagItemMoveCommandContractChecks.RunAsync),
        (
            "Durable kit-bag item move handler and replay",
            KitBagItemMoveDurableHandlerChecks.RunAsync),
        (
            "Durable equipment/bag transfer command contract",
            EquipmentBagTransferCommandContractChecks.RunAsync),
        (
            "Durable equipment/bag transfer handler and replay",
            EquipmentBagTransferDurableHandlerChecks.RunAsync),
        (
            "Durable Holy Stone command and exact wire contracts",
            HolyStoneCommandContractChecks.RunAsync),
        (
            "Durable Holy Stone handler and replay",
            HolyStoneDurableHandlerChecks.RunAsync),
        (
            "Shared character-inventory outbox compatibility",
            CharacterInventoryOutboxConsumerChecks.RunAsync),
        (
            "PostgreSQL talent command precondition",
            PostgresTalentUpgradeIntegrationChecks.RunAsync),
        .. B19ReconciliationCheckCatalog.All,
        .. B13OperationsCheckCatalog.All
    ];
}
