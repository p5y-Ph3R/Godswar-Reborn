using System.Text.RegularExpressions;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.WorldInstances;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresMedusaDailyEntryLimitChecks
{
    public const string CheckName =
        "PostgreSQL Medusa database-owned daily entry limit";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    private static readonly Regex DisposableDatabasePattern = new(
        "^godswar_(?:medusa_[a-f0-9]{8}|b(?:03|12)_[a-z0-9_]{8,48})$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} ({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var database = await ReadDatabaseNameAsync(dataSource);
        if (!DisposableDatabasePattern.IsMatch(database))
        {
            Console.WriteLine(
                $"SKIP {CheckName} requires a disposable B03/B12 " +
                $"database; received '{database}'");
            return;
        }

        await new PostgresSchemaMigrationRunner(dataSource)
            .InitializeGodswarSchemaAsync();
        await using (var gameStore = new PostgresGameStore(connectionString))
        {
            await gameStore.EnsureSeedDataAsync();
        }

        var accountIds = new List<int>();
        try
        {
            var first = await CreateCharacterAsync(dataSource, accountIds);
            var second = await CreateCharacterAsync(dataSource, accountIds);
            var third = await CreateCharacterAsync(dataSource, accountIds);
            var store = new PostgresMedusaDailyEntryClaimStore(dataSource);
            var day = new DateOnly(2026, 8, 27);
            var realm = new RealmId(1);
            var roster = new[] { first, second };

            Check.Equal(
                0,
                (await store.FindUsedCharacterIdsAsync(
                    realm,
                    day,
                    roster)).Count,
                "fresh Medusa characters have available attempts");

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var result = await store.TryClaimAsync(
                    Request(realm, day, roster, attempt));
                Check.True(
                    result.Status == MedusaDailyEntryClaimStatus.Claimed &&
                    result.DailyEntryLimit == 3,
                    $"Medusa attempt {attempt} uses the seeded database limit");
            }

            var concurrent = await Task.WhenAll(
                store.TryClaimAsync(Request(realm, day, roster, 3)),
                store.TryClaimAsync(Request(realm, day, roster, 4)));
            Check.Equal(
                1,
                concurrent.Count(result =>
                    result.Status == MedusaDailyEntryClaimStatus.Claimed),
                "the third concurrent party attempt has one winner");
            Check.Equal(
                1,
                concurrent.Count(result =>
                    result.Status ==
                        MedusaDailyEntryClaimStatus.AlreadyUsed),
                "the fourth concurrent party attempt is rejected atomically");

            var exhausted = await store.FindUsedCharacterIdsAsync(
                realm,
                day,
                [first, second, third]);
            Check.True(
                exhausted.SetEquals([first, second]) &&
                !exhausted.Contains(third),
                "only characters at the configured limit are exhausted");

            var blockedRoster = await store.TryClaimAsync(
                Request(realm, day, [first, third], 5));
            Check.True(
                blockedRoster.Status ==
                    MedusaDailyEntryClaimStatus.AlreadyUsed &&
                await CountEntriesAsync(dataSource, third) == 0,
                "one exhausted member rejects the whole party claim");

            await SetLimitAsync(dataSource, 4);
            Check.Equal(
                0,
                (await store.FindUsedCharacterIdsAsync(
                    realm,
                    day,
                    roster)).Count,
                "a database edit immediately changes availability");
            var fourthReservation = Guid.NewGuid();
            var fourth = await store.TryClaimAsync(
                Request(
                    realm,
                    day,
                    roster,
                    6,
                    fourthReservation));
            Check.True(
                fourth.Status == MedusaDailyEntryClaimStatus.Claimed &&
                fourth.DailyEntryLimit == 4,
                "the claim receipt and admission gate use the edited limit");

            await store.ReleaseAsync(fourthReservation);
            await SetLimitAsync(dataSource, 3);
            Check.True(
                (await store.FindUsedCharacterIdsAsync(
                    realm,
                    day,
                    roster)).SetEquals(roster),
                "releasing one reservation restores the exact prior count");
        }
        finally
        {
            await DeleteAccountsAsync(dataSource, accountIds);
            await SetLimitAsync(dataSource, 3);
        }
    }

    private static MedusaDailyEntryClaimRequest Request(
        RealmId realm,
        DateOnly day,
        IReadOnlyCollection<int> characterIds,
        int minute,
        Guid? reservationId = null) =>
        new(
            reservationId ?? Guid.NewGuid(),
            realm,
            day,
            MedusaEncounterDifficulty.Normal,
            characterIds,
            new DateTimeOffset(2026, 8, 27, 1, minute, 0, TimeSpan.Zero));

    private static async Task<int> CreateCharacterAsync(
        NpgsqlDataSource dataSource,
        ICollection<int> accountIds)
    {
        var token = Guid.NewGuid().ToString("N")[..12];
        await using var command = dataSource.CreateCommand(
            """
            WITH account AS (
                INSERT INTO public.accounts (username, password)
                VALUES (@username, 'test')
                RETURNING id
            ), character AS (
                INSERT INTO public.character_base (
                    account_id, server_id, name, "Map")
                SELECT id, 1, @characterName, 60
                FROM account
                RETURNING id, account_id
            )
            SELECT id, account_id FROM character;
            """);
        command.Parameters.AddWithValue("username", $"medusa_limit_{token}");
        command.Parameters.AddWithValue("characterName", $"ML{token}");
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(),
            "daily-entry fixture creates one character");
        var characterId = reader.GetInt32(0);
        accountIds.Add(reader.GetInt32(1));
        return characterId;
    }

    private static async Task<long> CountEntriesAsync(
        NpgsqlDataSource dataSource,
        int characterId)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT count(*) FROM public.medusa_daily_entries " +
            "WHERE character_id = @characterId;");
        command.Parameters.AddWithValue("characterId", characterId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task SetLimitAsync(
        NpgsqlDataSource dataSource,
        short limit)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE public.medusa_instance_settings " +
            "SET daily_entry_limit = @limit, " +
            "updated_at = clock_timestamp() " +
            "WHERE instance_key = 'medusa';");
        command.Parameters.AddWithValue("limit", limit);
        Check.Equal(1, await command.ExecuteNonQueryAsync(),
            "Medusa setting row remains present");
    }

    private static async Task DeleteAccountsAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyCollection<int> accountIds)
    {
        if (accountIds.Count == 0)
        {
            return;
        }
        await using var command = dataSource.CreateCommand(
            "DELETE FROM public.accounts WHERE id = ANY(@accountIds);");
        command.Parameters.AddWithValue("accountIds", accountIds.ToArray());
        _ = await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadDatabaseNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT current_database();");
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
    }
}
