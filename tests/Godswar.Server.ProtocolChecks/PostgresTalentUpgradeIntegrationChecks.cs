using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresTalentUpgradeIntegrationChecks
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
                $"SKIP PostgreSQL talent command integration ({ConnectionStringVariable} is not set)");
            return;
        }

        var token = Guid.NewGuid().ToString("N")[..12];
        var username = $"talent_cmd_{token}";
        var characterName = $"Talent{token}";
        int? accountId = null;

        try
        {
            await using var storeA =
                new PostgresGameStore(connectionString);
            await using var storeB =
                new PostgresGameStore(connectionString);
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
                    TalentPoints = 100
                });

            var wrongOwner = await storeA.UpgradeTalentAsync(
                account.Id + 1,
                character.Id,
                talentId: 0,
                clientRank: 0,
                clientTalentPoints: 100);
            Check.True(
                wrongOwner is null,
                "PostgreSQL talent command binds character ownership");

            var futureRank = await storeA.UpgradeTalentAsync(
                account.Id,
                character.Id,
                talentId: 0,
                clientRank: 99,
                clientTalentPoints: 100);
            Check.True(
                futureRank is null,
                "PostgreSQL rejects a future expected talent rank");

            var raced = await Task.WhenAll(
                storeA.UpgradeTalentAsync(
                    account.Id,
                    character.Id,
                    talentId: 0,
                    clientRank: 0,
                    clientTalentPoints: 100),
                storeB.UpgradeTalentAsync(
                    account.Id,
                    character.Id,
                    talentId: 0,
                    clientRank: 0,
                    clientTalentPoints: 100));
            Check.Equal(
                1,
                raced.Count(static result => result is not null),
                "one concurrent expected-rank transition commits");
            Check.Equal(
                1,
                raced.Count(static result => result is null),
                "the concurrent replay cannot buy another rank");

            var replay = await storeB.UpgradeTalentAsync(
                account.Id,
                character.Id,
                talentId: 0,
                clientRank: 0,
                clientTalentPoints: 100);
            Check.True(
                replay is null,
                "a later exact replay remains rejected");

            var nextRank = await storeA.UpgradeTalentAsync(
                account.Id,
                character.Id,
                talentId: 0,
                clientRank: 1,
                clientTalentPoints: 99)
                ?? throw new InvalidOperationException(
                    "PostgreSQL rejected the legitimate next transition.");
            Check.Equal(
                2,
                nextRank.NewRank,
                "rank N+1 creates a distinct valid operation");
            Check.Equal(
                97,
                nextRank.RemainingTalentPoints,
                "two committed ranks spend one plus two points");

            await using var reopened =
                new PostgresGameStore(connectionString);
            await reopened.EnsureSeedDataAsync();
            var persisted = (await reopened.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            Check.Equal(
                97,
                persisted.TalentPoints,
                "PostgreSQL replay protection preserves talent points");
            var talents = await reopened.GetTalentStatesAsync(
                account.Id,
                character.Id);
            Check.Equal(
                2,
                talents.Single(talent => talent.TalentId == 0).Rank,
                "PostgreSQL persists exactly two intended transitions");
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
        await using var connection =
            new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            DELETE FROM accounts
            WHERE id = @accountId
              AND username = @username;
            """, connection);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("username", username);
        var deleted = await command.ExecuteNonQueryAsync();
        if (deleted != 1)
        {
            throw new InvalidOperationException(
                "PostgreSQL talent fixture cleanup was not exact.");
        }
    }
}
