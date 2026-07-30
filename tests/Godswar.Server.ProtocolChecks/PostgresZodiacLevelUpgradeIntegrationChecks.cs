using Godswar.Server.Application.Characters;
using Godswar.Server.Infrastructure.Characters;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresZodiacLevelUpgradeIntegrationChecks
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
                $"SKIP PostgreSQL Zodiac level-up integration ({ConnectionStringVariable} is not set)");
            return;
        }

        var token = Guid.NewGuid().ToString("N")[..12];
        var username = $"zodiac_up_{token}";
        var characterName = $"Zodiac{token}";
        int? accountId = null;

        try
        {
            await using var storeA = new PostgresGameStore(connectionString);
            await using var storeB = new PostgresGameStore(connectionString);
            await storeA.EnsureSeedDataAsync();

            var account = await storeA.LoginOrCreateAccountAsync(
                username,
                string.Empty);
            accountId = account.Id;
            var character = await storeA.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = characterName,
                    Camp = GameDefaults.SpartaCamp,
                    Profession = 0,
                    Level = 80,
                    ZodiacLevel = 1,
                    ZodiacEnergy = 1_000
                });
            await using var checkpoints =
                new PostgresCharacterCheckpointStore(
                    connectionString);
            var acquired = await checkpoints.AcquireAsync(
                account.Id,
                character.Id,
                Guid.NewGuid()) ??
                throw new InvalidOperationException(
                    "PostgreSQL Zodiac fixture could not acquire ownership.");
            var ownership = acquired.Owner;

            var wrongOwner = await storeA.UpgradeZodiacLevelAsync(
                account.Id + 1,
                character.Id,
                ownership);
            Check.True(
                wrongOwner is null,
                "PostgreSQL Zodiac upgrade binds character ownership");

            var raced = await Task.WhenAll(
                storeA.UpgradeZodiacLevelAsync(
                    account.Id,
                    character.Id,
                    ownership),
                storeB.UpgradeZodiacLevelAsync(
                    account.Id,
                    character.Id,
                    ownership));
            Check.Equal(
                1,
                raced.Count(result => result is { Committed: true }),
                "only one concurrent PostgreSQL Zodiac upgrade commits");
            Check.Equal(
                1,
                raced.Count(result => result is { Committed: false }),
                "duplicate PostgreSQL Zodiac upgrade is rejected");

            var rejected = raced.Single(result => result is { Committed: false })
                ?? throw new InvalidOperationException(
                    "PostgreSQL Zodiac rejection unexpectedly disappeared");
            Check.Equal(
                (int)ZodiacLevelUpgradeStatus.InsufficientEnergy,
                (int)rejected.Status,
                "concurrent PostgreSQL duplicate sees committed energy");

            var replacement = await checkpoints.AcquireAsync(
                account.Id,
                character.Id,
                Guid.NewGuid()) ??
                throw new InvalidOperationException(
                    "PostgreSQL Zodiac fixture could not replace ownership.");
            var staleRejected = false;
            try
            {
                await storeA.UpgradeZodiacLevelAsync(
                    account.Id,
                    character.Id,
                    ownership);
            }
            catch (PlayerOwnershipValidationException error)
            {
                staleRejected =
                    error.Status ==
                    PlayerOwnershipValidationStatus.OwnershipLost;
            }
            Check.True(
                staleRejected,
                "replaced PostgreSQL Zodiac owner fails closed");
            Check.True(
                replacement.Owner.Generation > ownership.Generation,
                "Zodiac ownership replacement advances generation");

            await using var reopenedStore = new PostgresGameStore(
                connectionString);
            await reopenedStore.EnsureSeedDataAsync();
            var persisted = (await reopenedStore.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            Check.Equal(
                2,
                (int)persisted.ZodiacLevel,
                "PostgreSQL Zodiac level advances exactly once");
            Check.Equal(
                500,
                persisted.ZodiacEnergy,
                "PostgreSQL Zodiac energy is deducted exactly once");
            Check.Equal(
                0,
                persisted.ZodiacEnergyRemainderX100,
                "PostgreSQL Zodiac fractional energy persists");
        }
        finally
        {
            if (accountId.HasValue)
            {
                await DeleteTestAccountAsync(
                    connectionString,
                    accountId.Value,
                    username);
            }
        }
    }

    private static async Task DeleteTestAccountAsync(
        string connectionString,
        int accountId,
        string username)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            DELETE FROM accounts
            WHERE id = @accountId
              AND username = @username;
            """, connection);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("username", username);
        await command.ExecuteNonQueryAsync();
    }
}
