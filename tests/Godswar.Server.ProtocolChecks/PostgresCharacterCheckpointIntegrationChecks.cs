using Godswar.Server.Application.Characters;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresCharacterCheckpointIntegrationChecks
{
    internal const string CheckName =
        "PostgreSQL versioned character checkpoints";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(
                ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using (var migrationStore =
                         new PostgresGameStore(connectionString))
        {
            await migrationStore.EnsureSeedDataAsync();
        }

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        var fixture = await CreateFixtureAsync(dataSource);
        try
        {
            var checkpoints =
                new PostgresCharacterCheckpointStore(dataSource);
            await AssertDefaultsAndConstraintsAsync(
                dataSource,
                fixture);
            var first = await AssertAcquireAndFacetWritesAsync(
                checkpoints,
                dataSource,
                fixture);
            var second = await AssertReplacementAndReleaseAsync(
                checkpoints,
                dataSource,
                fixture,
                first);
            await AssertReorderedWritesAsync(
                checkpoints,
                dataSource,
                fixture,
                second);
            await AssertMissingIdentityAsync(
                checkpoints,
                fixture,
                second);
        }
        finally
        {
            await DeleteFixtureAsync(dataSource, fixture);
        }
    }

    private static async Task<CharacterCheckpointOwnership>
        AssertAcquireAndFacetWritesAsync(
            PostgresCharacterCheckpointStore store,
            NpgsqlDataSource dataSource,
            CheckpointFixture fixture)
    {
        var ownerId = Guid.NewGuid();
        var acquired = await store.AcquireAsync(
            fixture.AccountId,
            fixture.CharacterId,
            ownerId) ??
            throw new InvalidOperationException(
                "Checkpoint fixture ownership was not acquired.");
        Check.Equal(
            ownerId,
            acquired.Owner.OwnerId,
            "acquire returns the server owner ID");
        Check.Equal(
            1L,
            acquired.Owner.Generation,
            "first owner starts generation one");
        Check.Equal(
            0L,
            acquired.PositionRevision,
            "owner observes opening position revision");
        Check.Equal(
            0L,
            acquired.VitalsRevision,
            "owner observes opening vitals revision");

        var sameOwner = await store.AcquireAsync(
            fixture.AccountId,
            fixture.CharacterId,
            ownerId) ??
            throw new InvalidOperationException(
                "Same checkpoint owner was not reacquired.");
        Check.Equal(
            acquired,
            sameOwner,
            "same-owner acquire is idempotent");

        var position = new CharacterPositionCheckpoint(
            fixture.AccountId,
            fixture.CharacterId,
            acquired.Owner,
            CurrentMap: 7,
            PositionX: 12.5f,
            PositionZ: -33.25f,
            Revision: 1);
        AssertWrite(
            CharacterCheckpointWriteStatus.Applied,
            1,
            await store.WritePositionAsync(position),
            "first position checkpoint applies");
        AssertWrite(
            CharacterCheckpointWriteStatus.AlreadyApplied,
            1,
            await store.WritePositionAsync(position),
            "exact position retry is already applied");
        AssertWrite(
            CharacterCheckpointWriteStatus.RevisionConflict,
            1,
            await store.WritePositionAsync(
                position with { PositionX = 99f }),
            "same position revision with different state conflicts");

        var secondPosition = position with
        {
            PositionX = 22.5f,
            PositionZ = 44.5f,
            Revision = 2
        };
        AssertWrite(
            CharacterCheckpointWriteStatus.Applied,
            2,
            await store.WritePositionAsync(secondPosition),
            "newer position checkpoint applies");
        AssertWrite(
            CharacterCheckpointWriteStatus.Superseded,
            2,
            await store.WritePositionAsync(position),
            "late position checkpoint is superseded");

        var vitals = new CharacterVitalsCheckpoint(
            fixture.AccountId,
            fixture.CharacterId,
            acquired.Owner,
            CurrentHp: 777,
            CurrentMp: 123,
            Revision: 1);
        AssertWrite(
            CharacterCheckpointWriteStatus.Applied,
            1,
            await store.WriteVitalsAsync(vitals),
            "first vitals checkpoint applies");
        AssertWrite(
            CharacterCheckpointWriteStatus.AlreadyApplied,
            1,
            await store.WriteVitalsAsync(vitals),
            "exact vitals retry is already applied");
        AssertWrite(
            CharacterCheckpointWriteStatus.RevisionConflict,
            1,
            await store.WriteVitalsAsync(
                vitals with { CurrentHp = 778 }),
            "same vitals revision with different state conflicts");

        var secondVitals = vitals with
        {
            CurrentHp = 555,
            CurrentMp = 111,
            Revision = 2
        };
        AssertWrite(
            CharacterCheckpointWriteStatus.Applied,
            2,
            await store.WriteVitalsAsync(secondVitals),
            "newer vitals checkpoint applies");
        AssertWrite(
            CharacterCheckpointWriteStatus.Superseded,
            2,
            await store.WriteVitalsAsync(vitals),
            "late vitals checkpoint is superseded");

        var state = await ReadStateAsync(dataSource, fixture);
        Check.Equal(7, (int)state.MapId, "position stores exact map");
        Check.Equal(22.5f, state.PositionX, "position stores exact X");
        Check.Equal(44.5f, state.PositionZ, "position stores exact Z");
        Check.Equal(2L, state.PositionRevision, "position revision advances");
        Check.Equal(555, state.CurrentHp, "vitals store exact HP");
        Check.Equal(111, state.CurrentMp, "vitals store exact MP");
        Check.Equal(2L, state.VitalsRevision, "vitals revision advances");
        return acquired;
    }

    private static void AssertWrite(
        CharacterCheckpointWriteStatus status,
        long? revision,
        CharacterCheckpointWriteResult actual,
        string description)
    {
        Check.Equal(
            (int)status,
            (int)actual.Status,
            description);
        Check.Equal(
            revision ?? -1,
            actual.StoredRevision ?? -1,
            $"{description} reports stored revision");
    }
}
