using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresZodiacSkillGridIntegrationChecks
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
                $"SKIP PostgreSQL Zodiac grid integration ({ConnectionStringVariable} is not set)");
            return;
        }

        var token = Guid.NewGuid().ToString("N")[..12];
        var username = $"zodiac_grid_{token}";
        var characterName = $"Grid{token}";
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
                    Gold = 5_000,
                    ZodiacLevel = 2,
                    ZodiacEnergy = 16,
                    TalentPoints = 22
                });

            var wrongOwner = await storeA.ActivateZodiacSkillGridAsync(
                account.Id + 1,
                character.Id,
                1);
            Check.True(
                wrongOwner is null,
                "PostgreSQL grid activation binds character ownership");

            var raced = await Task.WhenAll(
                storeA.ActivateZodiacSkillGridAsync(
                    account.Id,
                    character.Id,
                    1),
                storeB.ActivateZodiacSkillGridAsync(
                    account.Id,
                    character.Id,
                    1));
            Check.Equal(
                1,
                raced.Count(result => result is { Committed: true }),
                "only one concurrent PostgreSQL grid activation commits");
            var rejected = raced.Single(
                result => result is { Committed: false })
                ?? throw new InvalidOperationException(
                    "PostgreSQL duplicate grid result disappeared");
            Check.Equal(
                (int)ZodiacSkillGridActivationStatus.AlreadyActive,
                (int)rejected.Status,
                "concurrent duplicate observes committed grid");
            Check.Equal(
                2_700,
                rejected.CurrentGold,
                "concurrent duplicate observes once-deducted premium gold");

            var wrongUpgradeOwner =
                await storeA.UpgradeZodiacSkillGridAsync(
                    account.Id + 1,
                    character.Id,
                    1);
            Check.True(
                wrongUpgradeOwner is null,
                "PostgreSQL grid upgrade binds character ownership");

            var upgradeRace = await Task.WhenAll(
                storeA.UpgradeZodiacSkillGridAsync(
                    account.Id,
                    character.Id,
                    1),
                storeB.UpgradeZodiacSkillGridAsync(
                    account.Id,
                    character.Id,
                    1));
            Check.Equal(
                1,
                upgradeRace.Count(result => result is { Committed: true }),
                "only one resource-limited PostgreSQL grid upgrade commits");
            var rejectedUpgrade = upgradeRace.Single(
                result => result is { Committed: false })
                ?? throw new InvalidOperationException(
                    "PostgreSQL rejected grid upgrade disappeared");
            Check.Equal(
                (int)ZodiacSkillGridUpgradeStatus.InsufficientEnergy,
                (int)rejectedUpgrade.Status,
                "serialized duplicate observes the committed energy spend");
            Check.Equal(
                11,
                rejectedUpgrade.CurrentEnergy,
                "rejected duplicate sees once-deducted Zodiac energy");
            Check.Equal(
                15,
                rejectedUpgrade.CurrentTalentPoints,
                "rejected duplicate sees once-deducted Talent Points");

            await using var reopened = new PostgresGameStore(
                connectionString);
            await reopened.EnsureSeedDataAsync();
            var persisted = (await reopened.GetCharactersAsync(account.Id))
                .Single(candidate => candidate.Id == character.Id);
            Check.Equal(2_700, persisted.Gold, "premium gold is deducted exactly once");
            Check.Equal(2, persisted.ZodiacSkillGridLevels[1], "upgraded grid level persists");
            Check.Equal(-1, persisted.ZodiacSkillGridSkillIds[1], "selected-skill sentinel persists");
            Check.Equal(11, persisted.ZodiacEnergy, "grid energy spend persists once");
            Check.Equal(15, persisted.TalentPoints, "grid Talent Point spend persists once");
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
