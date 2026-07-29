using Godswar.Server.Game;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresCharacterSnapshotReaderIntegrationChecks
{
    private static async Task AssertSingleSlotMutationGuardAsync(
        string connectionString,
        PostgresGameStore storeA,
        NpgsqlDataSource dataSource,
        string token)
    {
        var fixture = await CreateAccountFixtureAsync(
            storeA,
            $"snap_slot_{token}");
        try
        {
            await using var storeB =
                new PostgresGameStore(connectionString);
            var attempts = await Task.WhenAll(
                TryCreateAsync(
                    storeA,
                    fixture.AccountId,
                    $"SnapSlotA{token}"),
                TryCreateAsync(
                    storeB,
                    fixture.AccountId,
                    $"SnapSlotB{token}"));
            Check.Equal(
                1,
                attempts.Count(static attempt => attempt.Created),
                "concurrent PostgreSQL creation commits one character");
            Check.Equal(
                1,
                attempts.Count(static attempt => attempt.Occupied),
                "concurrent PostgreSQL creation rejects the occupied slot");
            Check.Equal(
                1L,
                await CountCharactersAsync(dataSource, fixture.AccountId),
                "PostgreSQL slot lock preserves one durable character");

            var replay = await TryCreateAsync(
                storeA,
                fixture.AccountId,
                $"SnapSlotReplay{token}");
            Check.True(
                replay.Occupied && !replay.Created,
                "replayed PostgreSQL creation rejects an occupied slot");
            Check.Equal(
                1L,
                await CountCharactersAsync(dataSource, fixture.AccountId),
                "replayed creation cannot corrupt PostgreSQL cardinality");
        }
        finally
        {
            await DeleteSingleSlotFixtureAsync(dataSource, fixture);
        }
    }

    private static async Task<CreateAttempt> TryCreateAsync(
        PostgresGameStore store,
        int accountId,
        string name)
    {
        try
        {
            _ = await store.CreateCharacterAsync(
                accountId,
                new GameCharacter
                {
                    Name = name,
                    Camp = GameDefaults.SpartaCamp,
                    Profession = 0,
                    Level = 1,
                    MaxHp = 1_500,
                    MaxMp = 177,
                    CurrentHp = 1_500,
                    CurrentMp = 177
                });
            return new CreateAttempt(Created: true, Occupied: false);
        }
        catch (CharacterSlotOccupiedException)
        {
            return new CreateAttempt(Created: false, Occupied: true);
        }
    }

    private static async Task DeleteSingleSlotFixtureAsync(
        NpgsqlDataSource dataSource,
        SnapshotFixture fixture)
    {
        await using var command = dataSource.CreateCommand(
            """
            DELETE FROM accounts
            WHERE id = @accountId
              AND username = @username;
            """);
        command.Parameters.AddWithValue("accountId", fixture.AccountId);
        command.Parameters.AddWithValue("username", fixture.Username);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "single-slot fixture cleanup deletes one exact account");
    }

    private sealed record CreateAttempt(bool Created, bool Occupied);
}
