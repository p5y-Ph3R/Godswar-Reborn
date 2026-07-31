using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Godswar.Server.Application.Reconciliation;
using Godswar.Server.Infrastructure.Reconciliation;
using Godswar.Server.State;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresB19ReconciliationIntegrationChecks
{
    private const string FixtureUsername = "b19_recovery_fixture";
    private const string FixtureCharacterName = "B19Recover";
    private const string TruncationSentinelUsername =
        "b19_truncation_sentinel";
    private const string TruncationSentinelCharacterName =
        "B19Bounded";

    private static string? ReadConnectionString()
    {
        var value =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static async Task RequireDisposableDatabaseAsync(
        NpgsqlDataSource dataSource,
        string expectedSuffix)
    {
        await using var command =
            dataSource.CreateCommand("SELECT current_database();");
        var database = Convert.ToString(
            await command.ExecuteScalarAsync())
            ?? throw new InvalidDataException(
                "PostgreSQL returned no current database.");
        Check.True(
            Regex.IsMatch(
                database,
                $"^godswar_b19_[a-f0-9]{{12}}_{expectedSuffix}$",
                RegexOptions.CultureInvariant),
            "B19 integration targets an owned disposable database");

        var builder = new NpgsqlConnectionStringBuilder(
            ReadConnectionString()!);
        Check.True(
            IsLoopback(builder.Host) && !builder.Pooling,
            "B19 integration is loopback-only with pooling disabled");
    }

    private static bool IsLoopback(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (string.Equals(
                host,
                "localhost",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) &&
               IPAddress.IsLoopback(address);
    }

    private static async Task AssertCurrentMigrationManifestAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT migration_id, checksum
            FROM public.schema_migrations
            ORDER BY migration_id;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        var index = 0;
        while (await reader.ReadAsync())
        {
            Check.True(
                index < PostgresSchemaMigrationCatalog.All.Count,
                "database has no unknown migration suffix");
            var expected = PostgresSchemaMigrationCatalog.All[index];
            Check.Equal(
                expected.Id,
                reader.GetString(0),
                $"migration {index} identity");
            Check.Equal(
                expected.Checksum,
                reader.GetString(1).TrimEnd(),
                $"migration {index} checksum");
            index++;
        }

        Check.Equal(
            PostgresSchemaMigrationCatalog.All.Count,
            index,
            "database has the exact current migration manifest");
    }

    private static async Task<GameCharacter> EnsureEconomyFixtureAsync(
        PostgresGameStore store)
    {
        var account = await store.LoginOrCreateAccountAsync(
            FixtureUsername,
            string.Empty);
        var existing = (await store.GetCharactersAsync(account.Id))
            .SingleOrDefault(character => string.Equals(
                character.Name,
                FixtureCharacterName,
                StringComparison.Ordinal));
        if (existing is not null)
        {
            return existing;
        }

        return await store.CreateCharacterAsync(
            account.Id,
            new GameCharacter
            {
                Name = FixtureCharacterName,
                Camp = GameDefaults.SpartaCamp,
                Profession = 0,
                Level = 1,
                Silver = 424_242,
                Gold = 4_242
            });
    }

    private static async Task EnsureCliTruncationSentinelAsync(
        PostgresGameStore store)
    {
        var account = await store.LoginOrCreateAccountAsync(
            TruncationSentinelUsername,
            string.Empty);
        var exists = (await store.GetCharactersAsync(account.Id))
            .Any(character => string.Equals(
                character.Name,
                TruncationSentinelCharacterName,
                StringComparison.Ordinal));
        if (exists)
        {
            return;
        }

        _ = await store.CreateCharacterAsync(
            account.Id,
            new GameCharacter
            {
                Name = TruncationSentinelCharacterName,
                Camp = GameDefaults.AthensCamp,
                Profession = 1,
                Level = 1
            });
    }

    private static async Task<EconomyFixture>
        ReadEconomyFixtureAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                account_row.id,
                character_row.id,
                item_row.id,
                character_row."Money",
                character_row."Stone",
                character_row.wallet_revision,
                character_row.inventory_revision,
                baseline_row.item_count,
                item_row.item_exp,
                item_row.updated_at
            FROM public.accounts account_row
            JOIN public.character_base character_row
              ON character_row.account_id = account_row.id
            JOIN public.character_economy_baseline baseline_row
              ON baseline_row.character_id = character_row.id
             AND baseline_row.account_id = account_row.id
            JOIN LATERAL (
                SELECT id, item_exp, updated_at
                FROM public.character_items
                WHERE user_id = character_row.id
                ORDER BY id
                LIMIT 1
            ) item_row ON true
            WHERE account_row.username = @username
              AND character_row.name = @character_name;
            """);
        command.Parameters.AddWithValue("username", FixtureUsername);
        command.Parameters.AddWithValue(
            "character_name",
            FixtureCharacterName);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new InvalidDataException(
                "The deterministic B19 economy fixture is missing.");
        }

        return new EconomyFixture(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt64(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetDateTime(9));
    }

    private static async Task ApplyEconomyDriftAsync(
        NpgsqlDataSource dataSource,
        EconomyFixture fixture)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using (var wallet = new NpgsqlCommand("""
            UPDATE public.character_base
            SET "Money" = "Money" + 37
            WHERE id = @character_id;
            """, connection, transaction))
        {
            wallet.Parameters.AddWithValue(
                "character_id",
                fixture.CharacterId);
            Check.Equal(
                1,
                await wallet.ExecuteNonQueryAsync(),
                "one wallet row is deliberately drifted");
        }

        await using (var item = new NpgsqlCommand("""
            UPDATE public.character_items
            SET item_exp = item_exp + 1,
                updated_at = updated_at + interval '1 second'
            WHERE id = @item_id
              AND user_id = @character_id;
            """, connection, transaction))
        {
            item.Parameters.AddWithValue("item_id", fixture.ItemId);
            item.Parameters.AddWithValue(
                "character_id",
                fixture.CharacterId);
            Check.Equal(
                1,
                await item.ExecuteNonQueryAsync(),
                "one inventory row is deliberately drifted");
        }

        await transaction.CommitAsync();

        await ExecuteDisposableCorruptionAsync(
            dataSource,
            async (connection, corruptionTransaction) =>
            {
                await using var baseline = new NpgsqlCommand("""
                    UPDATE public.character_economy_baseline
                    SET item_count = item_count + 1
                    WHERE character_id = @character_id
                      AND account_id = @account_id;
                    """,
                    connection,
                    corruptionTransaction);
                baseline.Parameters.AddWithValue(
                    "character_id",
                    fixture.CharacterId);
                baseline.Parameters.AddWithValue(
                    "account_id",
                    fixture.AccountId);
                Check.Equal(
                    1,
                    await baseline.ExecuteNonQueryAsync(),
                    "one immutable inventory baseline count is " +
                    "deliberately drifted");
            });
    }

    private static async Task RestoreEconomyFixtureAsync(
        NpgsqlDataSource dataSource,
        EconomyFixture fixture)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await using (var wallet = new NpgsqlCommand("""
            UPDATE public.character_base
            SET "Money" = @money,
                "Stone" = @stone,
                wallet_revision = @wallet_revision,
                inventory_revision = @inventory_revision
            WHERE id = @character_id;
            """, connection, transaction))
        {
            wallet.Parameters.AddWithValue("money", fixture.Money);
            wallet.Parameters.AddWithValue("stone", fixture.Stone);
            wallet.Parameters.AddWithValue(
                "wallet_revision",
                fixture.WalletRevision);
            wallet.Parameters.AddWithValue(
                "inventory_revision",
                fixture.InventoryRevision);
            wallet.Parameters.AddWithValue(
                "character_id",
                fixture.CharacterId);
            Check.Equal(
                1,
                await wallet.ExecuteNonQueryAsync(),
                "one wallet fixture is restored");
        }

        await using (var item = new NpgsqlCommand("""
            UPDATE public.character_items
            SET item_exp = @item_exp,
                updated_at = @updated_at
            WHERE id = @item_id
              AND user_id = @character_id;
            """, connection, transaction))
        {
            item.Parameters.AddWithValue("item_exp", fixture.ItemExperience);
            item.Parameters.AddWithValue(
                "updated_at",
                fixture.ItemUpdatedAt);
            item.Parameters.AddWithValue("item_id", fixture.ItemId);
            item.Parameters.AddWithValue(
                "character_id",
                fixture.CharacterId);
            Check.Equal(
                1,
                await item.ExecuteNonQueryAsync(),
                "one inventory fixture is restored");
        }

        await transaction.CommitAsync();

        await ExecuteDisposableCorruptionAsync(
            dataSource,
            async (connection, corruptionTransaction) =>
            {
                await using var baseline = new NpgsqlCommand("""
                    UPDATE public.character_economy_baseline
                    SET item_count = @item_count
                    WHERE character_id = @character_id
                      AND account_id = @account_id;
                    """,
                    connection,
                    corruptionTransaction);
                baseline.Parameters.AddWithValue(
                    "item_count",
                    fixture.BaselineItemCount);
                baseline.Parameters.AddWithValue(
                    "character_id",
                    fixture.CharacterId);
                baseline.Parameters.AddWithValue(
                    "account_id",
                    fixture.AccountId);
                Check.Equal(
                    1,
                    await baseline.ExecuteNonQueryAsync(),
                    "one immutable inventory baseline count is restored");
            });
    }

    private static async Task<string> ReadFixtureFingerprintAsync(
        NpgsqlDataSource dataSource,
        EconomyFixture fixture)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT jsonb_build_object(
                'character', to_jsonb(character_row),
                'item', to_jsonb(item_row),
                'inventoryBaseline', to_jsonb(baseline_row)
            )::text
            FROM public.character_base character_row
            JOIN public.character_items item_row
              ON item_row.user_id = character_row.id
             AND item_row.id = @item_id
            JOIN public.character_economy_baseline baseline_row
              ON baseline_row.character_id = character_row.id
            WHERE character_row.id = @character_id;
            """);
        command.Parameters.AddWithValue("item_id", fixture.ItemId);
        command.Parameters.AddWithValue(
            "character_id",
            fixture.CharacterId);
        return Convert.ToString(await command.ExecuteScalarAsync())
               ?? throw new InvalidDataException(
                   "Could not fingerprint the B19 fixture.");
    }

    private static async Task AssertAllEconomyViewsCleanAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                (SELECT count(*)::bigint
                 FROM public.character_wallet_reconciliation
                 WHERE character_present
                   AND is_reconciled IS DISTINCT FROM true)
              + (SELECT count(*)::bigint
                 FROM public.character_inventory_reconciliation
                 WHERE character_present
                   AND is_reconciled IS DISTINCT FROM true),
                (SELECT count(*)::bigint
                 FROM public.character_wallet_reconciliation
                 WHERE baseline_present
                   AND NOT character_present)
              + (SELECT count(*)::bigint
                 FROM public.character_inventory_reconciliation
                 WHERE baseline_present
                   AND NOT character_present);
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "economy-view verification returns one summary row");
        Check.Equal(
            0L,
            reader.GetInt64(0),
            "all active-character economy views are reconciled");
        Check.Equal(
            2L,
            reader.GetInt64(1),
            "one proven purge baseline remains visible in both immutable " +
            "economy evidence views");
    }

    private sealed record EconomyFixture(
        int AccountId,
        int CharacterId,
        long ItemId,
        int Money,
        int Stone,
        long WalletRevision,
        long InventoryRevision,
        int BaselineItemCount,
        int ItemExperience,
        DateTime ItemUpdatedAt);
}
