namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static (string Name, Func<Task> Run)[]
        DataArchitectureIntegrationChecks() =>
        [
            .. DataArchitectureCheckCatalog.All,
            (
                "PostgreSQL talent inbox/outbox transaction",
                PostgresTalentInboxOutboxIntegrationChecks.RunAsync),
            (
                "PostgreSQL developer-item grant transaction",
                PostgresDeveloperItemGrantIntegrationChecks.RunAsync),
            (
                PostgresDeveloperBagClearCommandIntegrationChecks
                    .CheckName,
                PostgresDeveloperBagClearCommandIntegrationChecks
                    .RunAsync),
            (
                PostgresMakeAttributeStoneCommandIntegrationChecks
                    .CheckName,
                PostgresMakeAttributeStoneCommandIntegrationChecks
                    .RunAsync),
            (
                PostgresGearMentorMaterialConversionIntegrationChecks
                    .CheckName,
                PostgresGearMentorMaterialConversionIntegrationChecks
                    .RunAsync),
            (
                PostgresGearMentorDecomposeIntegrationChecks.CheckName,
                PostgresGearMentorDecomposeIntegrationChecks.RunAsync),
            (
                PostgresKitBagItemDeleteCommandIntegrationChecks
                    .CheckName,
                PostgresKitBagItemDeleteCommandIntegrationChecks
                    .RunAsync),
            (
                PostgresKitBagItemMoveCommandIntegrationChecks
                    .CheckName,
                PostgresKitBagItemMoveCommandIntegrationChecks
                    .RunAsync),
            (
                PostgresCharacterCreationEconomyBaselineIntegrationChecks
                    .CheckName,
                PostgresCharacterCreationEconomyBaselineIntegrationChecks
                    .RunAsync),
            (
                "PostgreSQL outbox dispatcher recovery and ordering",
                PostgresOutboxDispatcherIntegrationChecks.RunAsync),
            (
                "PostgreSQL migration safety foundation",
                PostgresMigrationFoundationChecks.RunAsync),
            (
                PostgresEconomyLedgerMigrationIntegrationChecks.CheckName,
                PostgresEconomyLedgerMigrationIntegrationChecks.RunAsync),
            (
                "PostgreSQL schema release migration paths",
                PostgresSchemaReleaseIntegrationChecks.RunAsync),
            (
                "PostgreSQL migration-prefix fixture",
                PostgresMigrationPrefixFixtureChecks.RunAsync),
            (
                "PostgreSQL forward-only database cleanup",
                PostgresDatabaseCleanupIntegrationChecks.RunAsync),
            (
                "PostgreSQL official NPC content publication",
                PostgresNpcContentPublicationIntegrationChecks.RunAsync),
            (
                "PostgreSQL official NPC dialogue publication",
                PostgresNpcDialoguePublicationIntegrationChecks.RunAsync),
            (
                "PostgreSQL captured-monster ECS parity",
                PostgresMonsterEcsParityIntegrationChecks.RunAsync),
            (
                "PostgreSQL pinned world-content baseline",
                PostgresWorldContentReaderIntegrationChecks.RunAsync),
            (
                "PostgreSQL consistent character snapshot reader",
                PostgresCharacterSnapshotReaderIntegrationChecks.RunAsync)
        ];
}
