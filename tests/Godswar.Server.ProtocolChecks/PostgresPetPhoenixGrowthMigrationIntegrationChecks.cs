using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresPetPhoenixGrowthMigrationIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const string MigrationId =
        "20260811_071_pet_phoenix_growth_activation";
    private const string ArchiveRelation =
        "public.pet_phoenix_growth_activation_archive";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                "SKIP PostgreSQL pet Phoenix Growth migration integration " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        if (await IsMigrationAppliedAsync(connectionString))
        {
            Console.WriteLine(
                "SKIP PostgreSQL pet Phoenix Growth migration integration " +
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
            !await RelationExistsAsync(connectionString, ArchiveRelation),
            "prefix through migration 070 has no Phoenix archive");

        var token = Guid.NewGuid().ToString("N")[..10];
        var fixtures = await InsertFixturesAsync(dataSource, token);
        var before = await ReadAllStatesAsync(dataSource, fixtures);

        await runner.InitializeAsync(
            LegacySchemaBootstrap.LoadAsync,
            PostgresSchemaMigrationCatalog.All
                .Take(migrationIndex + 1)
                .ToArray());

        var after = await ReadAllStatesAsync(dataSource, fixtures);
        await AssertConvertedPetAsync(
            dataSource,
            fixtures.Single(static value => value.Kind == FixtureKind.Convert),
            before,
            after);
        AssertPreservedPet(
            fixtures.Single(static value => value.Kind == FixtureKind.Weak),
            before,
            after,
            "already-Weak unrevealed pet");
        AssertPreservedPet(
            fixtures.Single(static value => value.Kind == FixtureKind.Revealed),
            before,
            after,
            "revealed quality-Growth pet");
        await AssertMigrationMetadataAsync(dataSource, expectedCount: 1);

        var stableBeforeRestart = await ReadAllStatesAsync(
            dataSource,
            fixtures);
        await runner.InitializeAsync(
            LegacySchemaBootstrap.LoadAsync,
            PostgresSchemaMigrationCatalog.All
                .Take(migrationIndex + 1)
                .ToArray());
        var stableAfterRestart = await ReadAllStatesAsync(
            dataSource,
            fixtures);
        foreach (var fixture in fixtures)
        {
            AssertStateEqual(
                stableBeforeRestart[fixture.PetId],
                stableAfterRestart[fixture.PetId],
                $"repeat runner pet {fixture.PetId}");
        }
        await AssertMigrationMetadataAsync(dataSource, expectedCount: 1);
        Check.Equal(
            6L,
            await CountArchiveRowsAsync(dataSource, fixtures),
            "repeat runner does not duplicate archive rows");

        await CleanupFixturesAsync(dataSource, token, fixtures);
        Check.Equal(
            0L,
            await CountFixturesAsync(connectionString, token),
            "Phoenix migration fixtures are removed after verification");
    }

    private static async Task AssertConvertedPetAsync(
        NpgsqlDataSource dataSource,
        Fixture fixture,
        IReadOnlyDictionary<long, PetState> before,
        IReadOnlyDictionary<long, PetState> after)
    {
        var oldState = before[fixture.PetId];
        var newState = after[fixture.PetId];
        Check.True(
            !newState.Parent.GrowthRevealed &&
            newState.Parent.GrowthPolicy == "weak-until-phoenix-v1" &&
            newState.Parent.Revision == oldState.Parent.Revision + 1,
            "out-of-Weak unrevealed parent advances exactly once");
        Check.Equal(
            oldState.Parent with
            {
                Revision = newState.Parent.Revision,
                GrowthPolicy = newState.Parent.GrowthPolicy
            },
            newState.Parent,
            "parent mutation is limited to revision and policy stamp");
        Check.Equal(6, newState.Stats.Count, "converted pet retains six stats");

        foreach (var (oldStat, newStat) in oldState.Stats.Zip(newState.Stats))
        {
            Check.True(
                newStat.StatCode == oldStat.StatCode &&
                newStat.BaseGrowthRate == 0.010000m &&
                newStat.AddedSavvy - newStat.BaseGrowthRate ==
                    oldStat.AddedSavvy - oldStat.BaseGrowthRate &&
                newStat.InitialSavvy == oldStat.InitialSavvy &&
                newStat.GrowthAcceleration == oldStat.GrowthAcceleration &&
                newStat.BirthInitialSavvy == oldStat.BirthInitialSavvy &&
                newStat.RarityAddedSavvy == oldStat.RarityAddedSavvy &&
                newStat.Revision == oldStat.Revision + 1,
                $"converted stat {oldStat.StatCode} preserves non-Growth state and compatibility delta");
        }
        Check.Equal(
            0.060000m,
            newState.Stats.Sum(static value => value.BaseGrowthRate),
            "converted pet receives a valid hundredth-total Weak Growth roll");

        await AssertArchiveAsync(dataSource, fixture, oldState);
    }

    private static void AssertPreservedPet(
        Fixture fixture,
        IReadOnlyDictionary<long, PetState> before,
        IReadOnlyDictionary<long, PetState> after,
        string label)
    {
        var oldState = before[fixture.PetId];
        var newState = after[fixture.PetId];
        Check.Equal(
            oldState.Parent with
            {
                GrowthPolicy = newState.Parent.GrowthPolicy
            },
            newState.Parent,
            $"{label} parent fields and revision remain identical");
        Check.True(
            oldState.Stats.SequenceEqual(newState.Stats),
            $"{label} six-stat rows remain byte/value and revision identical");
    }

    private static void AssertStateEqual(
        PetState expected,
        PetState actual,
        string label)
    {
        Check.Equal(expected.Parent, actual.Parent, $"{label} parent");
        Check.True(
            expected.Stats.SequenceEqual(actual.Stats),
            $"{label} stat rows");
    }
}
