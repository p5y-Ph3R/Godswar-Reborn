using Godswar.Server.Application.Realms;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Infrastructure.Realms;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresRealmCalendarIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL realm calendar reader and CAS authority";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL realm calendar ({ConnectionStringVariable} " +
                "is not set)");
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        RealmId? realmId = null;
        try
        {
            realmId = await InsertRealmAsync(connection, transaction);
            var updated = await PostgresRealmCalendarSettingsStore
                .TryUpdateAsync(
                new RealmCalendarUpdate(
                    realmId.Value,
                    "Asia/Manila",
                    ExpectedRevision: 1,
                    UpdatedBy: "integration-check"),
                connection,
                transaction);
            Check.True(
                updated.Status == RealmCalendarUpdateStatus.Updated &&
                updated.Calendar?.TimeZoneId == "Asia/Manila" &&
                updated.Calendar.Revision == 2,
                "CAS publishes the next Manila calendar revision");

            var conflict = await PostgresRealmCalendarSettingsStore
                .TryUpdateAsync(
                new RealmCalendarUpdate(
                    realmId.Value,
                    "America/New_York",
                    ExpectedRevision: 1,
                    UpdatedBy: "stale-integration-check"),
                connection,
                transaction);
            Check.True(
                conflict.Status == RealmCalendarUpdateStatus.RevisionConflict &&
                conflict.Calendar?.Revision == 2 &&
                conflict.Calendar.TimeZoneId == "Asia/Manila",
                "stale management writes return the current calendar");

            var unchanged = await PostgresRealmCalendarSettingsStore
                .TryUpdateAsync(
                new RealmCalendarUpdate(
                    realmId.Value,
                    "Asia/Manila",
                    ExpectedRevision: 2,
                    UpdatedBy: "integration-check"),
                connection,
                transaction);
            Check.True(
                unchanged.Status == RealmCalendarUpdateStatus.Unchanged &&
                unchanged.Calendar?.Revision == 2,
                "same-zone management writes do not manufacture revisions");

            var catalog = await PostgresRealmCalendarCatalogReader.ReadAsync(
                connection,
                transaction);
            Check.True(
                catalog.Require(realmId.Value).Revision == 2 &&
                catalog.Require(realmId.Value).TimeZoneId == "Asia/Manila",
                "startup reader observes the durable per-realm calendar");
            Check.Equal(
                2,
                await ReadAuditCountAsync(
                    connection,
                    transaction,
                    realmId.Value),
                "realm creation and one update produce an exact audit chain");

            await AssertUnfencedUpdateRejectedAsync(
                connection,
                transaction,
                realmId.Value);
            await AssertForgedAuditRejectedAsync(
                connection,
                transaction,
                realmId.Value);
        }
        finally
        {
            await transaction.RollbackAsync();
        }

        if (realmId is { } rolledBackRealmId)
        {
            Check.Equal(
                0,
                await ReadAuditCountAsync(dataSource, rolledBackRealmId),
                "rollback leaves no realm calendar audit evidence");
            Check.Equal(
                0,
                await ReadRealmCountAsync(dataSource, rolledBackRealmId),
                "rollback leaves no temporary realm row");
        }
    }

    private static async Task<RealmId> InsertRealmAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        var token = Guid.NewGuid().ToString("N");
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.server (
                name,
                identifier,
                ip_address,
                server_limit,
                enabled,
                display_order,
                game_port,
                recommended
            ) VALUES (
                @name,
                @identifier,
                '127.0.0.1',
                1,
                false,
                1000,
                65000,
                false
            )
            RETURNING id;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("name", $"Calendar-{token[..8]}");
        command.Parameters.AddWithValue("identifier", token[..25]);
        return new RealmId(Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    private static async Task<int> ReadAuditCountAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RealmId realmId)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)::integer
            FROM public.server_time_zone_audit
            WHERE realm_id = @realmId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> ReadAuditCountAsync(
        NpgsqlDataSource dataSource,
        RealmId realmId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)::integer
            FROM public.server_time_zone_audit
            WHERE realm_id = @realmId;
            """);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> ReadRealmCountAsync(
        NpgsqlDataSource dataSource,
        RealmId realmId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*)::integer
            FROM public.server
            WHERE id = @realmId;
            """);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task AssertUnfencedUpdateRejectedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RealmId realmId)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE public.server
            SET time_zone_id = 'Etc/UTC'
            WHERE id = @realmId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        var exception = await ExpectPostgresExceptionAsync(
            transaction,
            "unfenced_calendar_update",
            async () => _ = await command.ExecuteNonQueryAsync(),
            "direct writes cannot bypass calendar CAS metadata");
        Check.Equal(
            PostgresErrorCodes.CheckViolation,
            exception.SqlState,
            "unfenced calendar write fails with a constraint violation");
    }

    private static async Task AssertForgedAuditRejectedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RealmId realmId)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public.server_time_zone_audit (
                realm_id,
                previous_revision,
                revision,
                previous_time_zone_id,
                time_zone_id,
                changed_at,
                changed_by
            ) VALUES (
                @realmId,
                2,
                3,
                'Asia/Manila',
                'Etc/UTC',
                clock_timestamp(),
                'forged-integration-check'
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("realmId", realmId.Value);
        var exception = await ExpectPostgresExceptionAsync(
            transaction,
            "forged_calendar_audit",
            async () => _ = await command.ExecuteNonQueryAsync(),
            "audit evidence cannot be forged ahead of the realm row");
        Check.Equal(
            PostgresErrorCodes.CheckViolation,
            exception.SqlState,
            "forged audit row fails with a constraint violation");
    }

    private static async Task<PostgresException>
        ExpectPostgresExceptionAsync(
            NpgsqlTransaction transaction,
            string savepoint,
            Func<Task> action,
            string description)
    {
        await transaction.SaveAsync(savepoint);
        try
        {
            await action();
        }
        catch (PostgresException exception)
        {
            await transaction.RollbackAsync(savepoint);
            return exception;
        }
        await transaction.RollbackAsync(savepoint);
        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected PostgresException.");
    }
}
