using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetScaledAddedValueMigrationIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const string MigrationId =
        "20260811_078_pet_scaled_added_value_v3";
    private const string ArchiveRelation =
        "public.pet_scaled_added_value_v3_archive";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                "SKIP PostgreSQL pet scaled Added-value V3 integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }
        if (await IsMigrationAppliedAsync(connectionString))
        {
            Console.WriteLine(
                "SKIP PostgreSQL pet scaled Added-value V3 integration " +
                $"({MigrationId} is already applied)");
            return;
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var migrationIndex = PostgresSchemaMigrationCatalog.All
            .Select((migration, index) => (migration, index))
            .Single(entry => entry.migration.Id == MigrationId)
            .index;
        var runner = new PostgresSchemaMigrationRunner(dataSource);
        await runner.InitializeAsync(
            LegacySchemaBootstrap.LoadAsync,
            PostgresSchemaMigrationCatalog.All
                .Take(migrationIndex)
                .ToArray());

        Check.True(
            !await RelationExistsAsync(dataSource, ArchiveRelation),
            "prefix through migration 077 has no V3 Added-value archive");

        var token = Guid.NewGuid().ToString("N")[..10];
        var fixture = await InsertFixtureAsync(dataSource, token);
        var before = await ReadPetStateAsync(
            dataSource,
            fixture.EligiblePetId);

        var blocked = false;
        try
        {
            await runner.InitializeAsync(
                LegacySchemaBootstrap.LoadAsync,
                PostgresSchemaMigrationCatalog.All
                    .Take(migrationIndex + 1)
                    .ToArray());
        }
        catch (PostgresException error) when (
            error.SqlState == PostgresErrorCodes.RaiseException)
        {
            blocked = error.MessageText.Contains(
                "historical Merge gains",
                StringComparison.Ordinal);
        }
        Check.True(
            blocked,
            "migration fails closed when a V2 pet has historical Merge gains");
        Check.True(
            !await IsMigrationAppliedAsync(connectionString) &&
            !await RelationExistsAsync(dataSource, ArchiveRelation),
            "failed migration rolls back its metadata and archive DDL");
        AssertStateEqual(
            before,
            await ReadPetStateAsync(dataSource, fixture.EligiblePetId),
            "failed migration preserves the eligible pet");
        Check.Equal(
            1L,
            await CountOwnerMergeBonusRowsAsync(
                dataSource,
                fixture.EligiblePetId),
            "failed migration rolls back owner-Merge bonus invalidation");

        await DeletePetAsync(dataSource, fixture.BlockedPetId);
        await runner.InitializeAsync(
            LegacySchemaBootstrap.LoadAsync,
            PostgresSchemaMigrationCatalog.All
                .Take(migrationIndex + 1)
                .ToArray());

        var after = await ReadPetStateAsync(
            dataSource,
            fixture.EligiblePetId);
        Check.True(
            after.SourceVersion == "basic-plus-scaled-growth-v3" &&
            after.Revision == before.Revision + 1 &&
            after.Level == before.Level &&
            after.CompletedPetMerges == 0,
            "eligible pet advances once into scaled Added-value V3");
        Check.Equal(6, after.Stats.Count, "converted pet retains six stats");
        foreach (var (oldStat, newStat) in before.Stats.Zip(after.Stats))
        {
            Check.True(
                newStat.StatCode == oldStat.StatCode &&
                newStat.InitialSavvy == oldStat.BirthInitialSavvy &&
                newStat.AddedSavvy ==
                    (oldStat.BaseGrowthRate +
                     oldStat.GrowthAcceleration) * before.Level &&
                newStat.BaseGrowthRate == oldStat.BaseGrowthRate &&
                newStat.GrowthAcceleration ==
                    oldStat.GrowthAcceleration &&
                newStat.BirthInitialSavvy ==
                    oldStat.BirthInitialSavvy &&
                newStat.RarityAddedSavvy ==
                    oldStat.RarityAddedSavvy &&
                newStat.Revision == oldStat.Revision + 1,
                $"converted stat {oldStat.StatCode} has exact scaled Added value");
        }
        await AssertArchiveAsync(dataSource, before);
        Check.Equal(
            0L,
            await CountOwnerMergeBonusRowsAsync(
                dataSource,
                fixture.EligiblePetId),
            "successful migration invalidates stale owner-Merge bonuses");
        Check.True(
            await IsMigrationAppliedAsync(connectionString),
            "successful V3 migration records its metadata");

        var stable = await ReadPetStateAsync(
            dataSource,
            fixture.EligiblePetId);
        await runner.InitializeAsync(
            LegacySchemaBootstrap.LoadAsync,
            PostgresSchemaMigrationCatalog.All
                .Take(migrationIndex + 1)
                .ToArray());
        AssertStateEqual(
            stable,
            await ReadPetStateAsync(dataSource, fixture.EligiblePetId),
            "repeat runner leaves the V3 pet unchanged");
        Check.Equal(
            6L,
            await CountArchiveRowsAsync(dataSource, fixture.EligiblePetId),
            "repeat runner does not duplicate archive rows");
        Check.Equal(
            0L,
            await CountOwnerMergeBonusRowsAsync(
                dataSource,
                fixture.EligiblePetId),
            "repeat runner does not restore stale owner-Merge bonuses");

        await CleanupFixtureAsync(dataSource, fixture, token);
    }

    private static void AssertStateEqual(
        PetState expected,
        PetState actual,
        string label)
    {
        Check.True(
            expected.PetId == actual.PetId &&
            expected.Level == actual.Level &&
            expected.CompletedPetMerges == actual.CompletedPetMerges &&
            expected.Revision == actual.Revision &&
            expected.SourceVersion == actual.SourceVersion &&
            expected.Stats.SequenceEqual(actual.Stats),
            label);
    }
}
