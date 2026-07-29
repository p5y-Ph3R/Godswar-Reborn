using Godswar.Server.Application.Characters;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

/// <summary>
/// Disposable-PostgreSQL coverage for the B06 character bootstrap boundary.
/// This check is registered in the mandatory B03 PostgreSQL gate.
/// </summary>
internal static partial class PostgresCharacterSnapshotReaderIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL character snapshot reader " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var store = new PostgresGameStore(connectionString);
        await store.EnsureSeedDataAsync();
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var fixtures = new List<SnapshotFixture>();
        var token = Guid.NewGuid().ToString("N")[..10];

        try
        {
            await AssertMissingAccountFailsAsync(
                connectionString,
                dataSource);
            await AssertExistingEmptySlotAsync(
                connectionString,
                store,
                dataSource,
                fixtures,
                token);
            await AssertSingleSlotMutationGuardAsync(
                connectionString,
                store,
                dataSource,
                token);
            await AssertLegacyParityAsync(
                connectionString,
                store,
                dataSource,
                fixtures,
                token);
            await AssertAmbiguousSlotFailsAsync(
                connectionString,
                store,
                dataSource,
                fixtures,
                token);
            await AssertRepeatableReadConsistencyAsync(
                connectionString,
                store,
                dataSource,
                fixtures,
                token);
        }
        finally
        {
            for (var index = fixtures.Count - 1; index >= 0; index--)
            {
                await DeleteFixtureAsync(dataSource, fixtures[index]);
            }
        }
    }

    private static async Task AssertMissingAccountFailsAsync(
        string connectionString,
        NpgsqlDataSource dataSource)
    {
        var missingAccountId = await ReadUnusedAccountIdAsync(dataSource);
        await using var reader =
            new PostgresCharacterSnapshotReader(connectionString);
        var exception = await CaptureFailureAsync(
            () => reader.ReadAsync(missingAccountId));
        Check.Equal(
            (int)CharacterSnapshotFailureReason.AccountNotFound,
            (int)exception.Reason,
            "snapshot reader rejects a missing authenticated account");
    }

    private static async Task AssertExistingEmptySlotAsync(
        string connectionString,
        PostgresGameStore store,
        NpgsqlDataSource dataSource,
        ICollection<SnapshotFixture> fixtures,
        string token)
    {
        var fixture = await CreateAccountFixtureAsync(
            store,
            $"snap_empty_{token}");
        fixtures.Add(fixture);
        await using var reader =
            new PostgresCharacterSnapshotReader(connectionString);
        var snapshot = await reader.ReadAsync(fixture.AccountId);

        Check.Equal(
            fixture.AccountId,
            snapshot.AccountId,
            "empty-slot snapshot preserves its authenticated account");
        Check.True(
            snapshot.Character is null,
            "an existing account without a character has an explicit empty slot");
        Check.True(
            !string.IsNullOrWhiteSpace(snapshot.ProviderSnapshotToken),
            "empty-slot snapshot includes a PostgreSQL diagnostic token");
        Check.Equal(
            TimeSpan.Zero,
            snapshot.ReadAtUtc.Offset,
            "empty-slot snapshot timestamp is UTC");
        Check.Equal(
            0L,
            await CountCharactersAsync(dataSource, fixture.AccountId),
            "empty-slot fixture has no hidden character");
    }

    private static async Task AssertAmbiguousSlotFailsAsync(
        string connectionString,
        PostgresGameStore store,
        NpgsqlDataSource dataSource,
        ICollection<SnapshotFixture> fixtures,
        string token)
    {
        var fixture = await CreateAccountFixtureAsync(
            store,
            $"snap_multi_{token}");
        var first = await CreateCharacterAsync(
            store,
            fixture.AccountId,
            $"SnapMultiA{token}");
        // The production mutation boundary now prevents this state. Insert a
        // legacy-corruption fixture directly so the reader's >1 fail-closed
        // behavior remains covered independently.
        var secondId = await InsertLegacyAdditionalCharacterAsync(
            dataSource,
            fixture.AccountId,
            $"SnapMultiB{token}");
        fixture = fixture with
        {
            CharacterIds = [first.Id, secondId]
        };
        fixtures.Add(fixture);

        Check.Equal(
            2L,
            await CountCharactersAsync(dataSource, fixture.AccountId),
            "ambiguous-slot fixture owns exactly two characters");
        await using var reader =
            new PostgresCharacterSnapshotReader(connectionString);
        var exception = await CaptureFailureAsync(
            () => reader.ReadAsync(fixture.AccountId));
        Check.Equal(
            (int)CharacterSnapshotFailureReason.AmbiguousCharacterSlot,
            (int)exception.Reason,
            "SingleCharacterV1 fails closed for multiple characters");
    }

    private static async Task<CharacterSnapshotUnavailableException>
        CaptureFailureAsync(
            Func<Task<CharacterAccountSnapshot>> action)
    {
        try
        {
            _ = await action();
        }
        catch (CharacterSnapshotUnavailableException exception)
        {
            return exception;
        }

        throw new InvalidOperationException(
            "Expected a typed character snapshot failure.");
    }
}
