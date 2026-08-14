using System.Text.RegularExpressions;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Pets;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresPetLearnedSkillContentPublicationIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL learned pet-skill content publication integration";
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const long PublicationLockId = 0x50534B494C4C5631;
    private static readonly Regex DisposableDatabasePattern = new(
        @"^godswar_(?:b03_[a-f0-9]{10}_smoke_[0-9]{2}|b12_[a-z0-9_]{1,40})$",
        RegexOptions.CultureInvariant);

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP {CheckName} ({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        if (!await IsDisposableDatabaseAsync(dataSource))
        {
            return;
        }

        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var concurrent = await PublishConcurrentlyAsync(
            dataSource,
            connectionString);
        var first = concurrent[0];
        Check.True(
            concurrent.Length == 2 &&
            concurrent.All(catalog =>
                catalog.Revision.Sha256 == first.Revision.Sha256),
            "simultaneous first publishers converge after a serializable retry");
        var repeated =
            await PostgresPetLearnedSkillContentBootstrapper.LoadAsync(
                connectionString);
        Check.True(
            first.Revision.Sha256 ==
                PetLearnedSkillContentBaseline.ExpectedRevision &&
            repeated.Revision.Sha256 == first.Revision.Sha256 &&
            first.Curves.Count == 384 &&
            first.Curves.Sum(static curve => curve.Steps.Count) == 1655,
            "fresh PostgreSQL publication round-trips and reloads one pinned revision");

        await AssertPublishedRowsAsync(dataSource, first.Revision.Sha256);
        await AssertRejectedAsync(
            dataSource,
            """
            UPDATE public.pet_skill_curve_definitions
            SET opaque_flag = opaque_flag + 1
            WHERE revision = @revision
              AND family_type = 0
              AND priority = 1;
            """,
            first.Revision.Sha256,
            "sealed learned pet-skill curve mutation");
        await AssertRejectedAsync(
            dataSource,
            """
            DELETE FROM public.pet_skill_content_publication
            WHERE singleton;
            """,
            first.Revision.Sha256,
            "learned pet-skill publication deletion");
    }

    private static async Task<PinnedPetLearnedSkillContentCatalog[]>
        PublishConcurrentlyAsync(
            NpgsqlDataSource dataSource,
            string connectionString)
    {
        var applicationName =
            $"pet-skill-first-publish-{Guid.NewGuid():N}";
        var raceBuilder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = applicationName
        };
        await using var blocker = await dataSource.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(
                         "SELECT pg_advisory_xact_lock(@lockId);",
                         blocker,
                         transaction))
        {
            command.Parameters.AddWithValue("lockId", PublicationLockId);
            await command.ExecuteNonQueryAsync();
        }

        var publishers = new[]
        {
            PostgresPetLearnedSkillContentBootstrapper.LoadAsync(
                raceBuilder.ConnectionString),
            PostgresPetLearnedSkillContentBootstrapper.LoadAsync(
                raceBuilder.ConnectionString)
        };
        try
        {
            await WaitForBlockedPublishersAsync(
                dataSource,
                applicationName,
                publishers);
        }
        finally
        {
            await transaction.CommitAsync();
        }

        return await Task.WhenAll(publishers);
    }

    private static async Task WaitForBlockedPublishersAsync(
        NpgsqlDataSource dataSource,
        string applicationName,
        IReadOnlyCollection<Task<PinnedPetLearnedSkillContentCatalog>>
            publishers)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (publishers.Any(static task => task.IsCompleted))
            {
                throw new InvalidOperationException(
                    "A first publisher completed while the publication lock was held.");
            }

            await using var command = dataSource.CreateCommand(
                """
                SELECT count(*)
                FROM pg_catalog.pg_stat_activity AS activity
                INNER JOIN pg_catalog.pg_locks AS locks
                    ON locks.pid = activity.pid
                WHERE activity.application_name = @applicationName
                  AND locks.locktype = 'advisory'
                  AND NOT locks.granted;
                """);
            command.Parameters.AddWithValue(
                "applicationName", applicationName);
            if (Convert.ToInt64(await command.ExecuteScalarAsync()) == 2)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException(
            "Both first publishers did not reach the advisory-lock wait.");
    }

    private static async Task<bool> IsDisposableDatabaseAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT current_database();");
        var database = await command.ExecuteScalarAsync() as string ??
            string.Empty;
        if (DisposableDatabasePattern.IsMatch(database))
        {
            return true;
        }

        Console.WriteLine(
            $"SKIP {CheckName} requires a disposable B03/B12 database; " +
            $"received '{database}'");
        return false;
    }

    private static async Task AssertPublishedRowsAsync(
        NpgsqlDataSource dataSource,
        string revision)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                (SELECT count(*)
                 FROM public.pet_skill_curve_definitions
                 WHERE revision = @revision),
                (SELECT count(*)
                 FROM public.pet_skill_curve_steps
                 WHERE revision = @revision),
                (SELECT count(*)
                 FROM public.pet_skill_content_publication
                 WHERE revision = @revision AND singleton);
            """);
        command.Parameters.AddWithValue("revision", revision);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(),
            "published learned pet-skill counts are readable");
        Check.True(
            reader.GetInt64(0) == 384 &&
            reader.GetInt64(1) == 1655 &&
            reader.GetInt64(2) == 1,
            "published learned pet-skill rows are complete and singly selected");
    }

    private static async Task AssertRejectedAsync(
        NpgsqlDataSource dataSource,
        string sql,
        string revision,
        string assertion)
    {
        try
        {
            await using var command = dataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("revision", revision);
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException)
        {
            return;
        }

        throw new InvalidOperationException(assertion + " was accepted.");
    }
}
