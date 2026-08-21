using Godswar.Server.Application.Commands;
using Godswar.Server.Application.Inventory;
using Godswar.Server.Infrastructure.Inventory;
using Godswar.Server.Infrastructure.Messaging;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresHolySuitCommandIntegrationChecks
{
    private static async Task AssertRealmQuotaIsolationAsync(
        string connectionString,
        GameplayItemContent itemContent)
    {
        var fixture = await CreateFixtureAsync(
            connectionString,
            realmId: 2);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var executor = new PostgresHolySuitCommandExecutor(
            dataSource,
            new PostgresOutboxDispatcherOptions(),
            itemContent);
        var ownership = PlayerOwnershipTestFences.ForCharacter(
            fixture.CharacterId);
        var initial = await executor.ReadStoreQuotaAsync(
            fixture.Subject,
            ownership);
        await SetDailyStoredExperienceForRealmAsync(
            connectionString,
            fixture.AccountId,
            realmId: 1,
            initial.UsageDay,
            1_990_000_000);

        var isolated = await executor.ReadStoreQuotaAsync(
            fixture.Subject,
            ownership);
        Check.Equal(
            0L,
            isolated.StoredExperienceToday,
            "Dwargon quota ignores the same account's Tempest usage");

        var result = await ExecuteAsync(
            executor,
            fixture,
            new CommandConnectionCorrelation(
                Guid.NewGuid(),
                CommandTransportKind.SecureTlsLegacy),
            Guid.NewGuid(),
            HolySuitCommandOperation.StoreExperience,
            primarySlot: 0,
            primaryState: Item(9023, bound: 1).ToCompactString(),
            experience: 50_000_000);
        Require(
            result,
            HolySuitExecutionDisposition.Committed,
            HolySuitCommandResultStatus.ExperienceStored,
            "Dwargon realm-scoped Holy Suit storage");

        Check.Equal(
            1_990_000_000L,
            await ReadDailyStoredExperienceAsync(
                connectionString,
                fixture.AccountId,
                realmId: 1,
                initial.UsageDay),
            "Dwargon storage does not mutate Tempest daily usage");
        Check.Equal(
            50_000_000L,
            await ReadDailyStoredExperienceAsync(
                connectionString,
                fixture.AccountId,
                realmId: 2,
                initial.UsageDay),
            "Dwargon storage advances only its own daily usage row");
    }

    private static async Task SetDailyStoredExperienceForRealmAsync(
        string connectionString,
        int accountId,
        int realmId,
        DateOnly usageDay,
        long storedExperience)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO holy_suit_daily_exp_storage (
                account_id,
                realm_id,
                usage_day,
                stored_exp,
                operation_count
            )
            VALUES (@accountId, @realmId, @usageDay, @storedExperience, 0)
            ON CONFLICT (account_id, realm_id, usage_day) DO UPDATE
            SET stored_exp = EXCLUDED.stored_exp;
            """,
            connection);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId);
        command.Parameters.AddWithValue("usageDay", usageDay);
        command.Parameters.AddWithValue("storedExperience", storedExperience);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            $"position realm {realmId} Holy Suit daily usage");
    }

    private static async Task<long> ReadDailyStoredExperienceAsync(
        string connectionString,
        int accountId,
        int realmId,
        DateOnly usageDay)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT stored_exp
            FROM holy_suit_daily_exp_storage
            WHERE account_id = @accountId
              AND realm_id = @realmId
              AND usage_day = @usageDay;
            """,
            connection);
        command.Parameters.AddWithValue("accountId", accountId);
        command.Parameters.AddWithValue("realmId", realmId);
        command.Parameters.AddWithValue("usageDay", usageDay);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
