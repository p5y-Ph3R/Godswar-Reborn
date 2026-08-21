using System.Data;
using Godswar.Server.Application.World;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresGameplayV3UpgradeIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL gameplay v2-to-v3 publication upgrade";

    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const string PredecessorMigration =
        "20260814_092_packed_seal_ownership_hardening";
    private const string LegacyPublisher =
        "server-database-promotion-v1";
    private const string UpgradePublisher =
        "server-database-promotion-v2";
    private const string AuthorityPublisher =
        "server-database-champion-talent-authority-v1";

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL gameplay v3 upgrade " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await MigrateAsync(dataSource, Through(PredecessorMigration));
        await PostgresRelationalContentBaselineBootstrapper.EnsureAsync(
            connectionString);
        await PostgresRelationalContentBaselineBootstrapper.EnsureAsync(
            connectionString);
        await AssertCombatColumnsAbsentAsync(dataSource);
        await MigrateAsync(dataSource, PostgresSchemaMigrationCatalog.All);
        await AssertCleanAndDirectPublicationPathsAsync(
            dataSource,
            connectionString);
        await InflateMutableChampionTalentsAsync(dataSource);
        var predecessor = await PublishLegacyV2Async(dataSource);

        await AssertLegacyRowsPreservedAsync(dataSource, predecessor);
        await AssertUnknownPublisherFailsClosedAsync(
            dataSource,
            connectionString,
            predecessor);
        await AssertDriftedSourceFailsClosedAsync(
            dataSource,
            connectionString,
            predecessor);

        var upgraded = await PostgresGameplayContentPublisher
            .EnsurePublishedAsync(connectionString);
        Check.True(
            upgraded.Created &&
            string.Equals(
                upgraded.Publisher,
                AuthorityPublisher,
                StringComparison.Ordinal) &&
            !string.Equals(
                predecessor.Sha256,
                upgraded.Revision,
                StringComparison.Ordinal),
            "an exact v2 predecessor chains to one corrected publication");
        await AssertUpgradeStateAsync(
            dataSource,
            predecessor,
            upgraded);
        await AssertChampionVectorAsync(
            dataSource,
            InflatedV3Revision,
            inflated: true,
            mutable: false);

        var repeated = await PostgresGameplayContentPublisher
            .EnsurePublishedAsync(connectionString);
        Check.True(
            !repeated.Created &&
            string.Equals(
                upgraded.Revision,
                repeated.Revision,
                StringComparison.Ordinal),
            "a repeated authority publication check is an idempotent read");
        await AssertDirectV3AuthorityUpgradeAsync(
            dataSource,
            connectionString,
            upgraded);
    }

    private static IReadOnlyList<PostgresSchemaMigration> Through(string id)
    {
        var index = PostgresSchemaMigrationCatalog.All
            .Select(static (migration, index) => (migration, index))
            .Single(candidate => string.Equals(
                candidate.migration.Id,
                id,
                StringComparison.Ordinal))
            .index;
        return PostgresSchemaMigrationCatalog.All.Take(index + 1).ToArray();
    }

    private static async Task MigrateAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyList<PostgresSchemaMigration> migrations)
    {
        var runner = new PostgresSchemaMigrationRunner(dataSource);
        await runner.InitializeAsync(LegacySchemaBootstrap.LoadAsync, migrations);
    }

    private static async Task AssertCombatColumnsAbsentAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT count(*)::integer
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND (
                  table_name = 'gameplay_map_definitions'
                  AND column_name = 'map_mode'
                  OR table_name = 'gameplay_monster_templates'
                  AND column_name = 'attack_type'
              );
            """,
            connection);
        Check.Equal(
            0,
            Convert.ToInt32(await command.ExecuteScalarAsync()),
            "the historical fixture is genuinely pre-migration-093");
    }

    private static async Task<WorldContentFamilyRevision> PublishLegacyV2Async(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead);
        var canonical = await PostgresGameplayContentPublisher
            .ReadCanonicalSourceContentAsync(connection, transaction);
        var revision =
            WorldContentRevisionHasher.HashGameplayV2ForUpgrade(canonical);
        Check.True(
            await PostgresGameplayContentPublisher.InsertReleaseAsync(
                connection,
                transaction,
                revision,
                canonical,
                CancellationToken.None),
            "the historical publisher creates the legacy v2 release header");
        await PostgresGameplayContentPublisher.CopyLegacyV2DefinitionsAsync(
            connection,
            transaction,
            revision.Sha256,
            canonical,
            CancellationToken.None);

        await using var publish = new NpgsqlCommand(
            """
            INSERT INTO gameplay_content_publication (
                family, revision, published_at, publisher
            )
            VALUES ('gameplay', @revision, now(), @publisher)
            ON CONFLICT (family) DO UPDATE
            SET revision = EXCLUDED.revision,
                published_at = EXCLUDED.published_at,
                publisher = EXCLUDED.publisher;
            """,
            connection,
            transaction);
        publish.Parameters.AddWithValue("revision", revision.Sha256);
        publish.Parameters.AddWithValue("publisher", LegacyPublisher);
        Check.Equal(
            1,
            await publish.ExecuteNonQueryAsync(),
            "the fixture repoints at the complete historical v2 release");
        await transaction.CommitAsync();
        return revision;
    }

    private static async Task AssertLegacyRowsPreservedAsync(
        NpgsqlDataSource dataSource,
        WorldContentFamilyRevision predecessor)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                COUNT(*) FILTER (WHERE map_mode IS NOT NULL)::integer,
                (
                    SELECT COUNT(*)::integer
                    FROM gameplay_monster_templates
                    WHERE revision = @revision
                      AND attack_type IS NOT NULL
                )
            FROM gameplay_map_definitions
            WHERE revision = @revision;
            """,
            connection);
        command.Parameters.AddWithValue("revision", predecessor.Sha256);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(await reader.ReadAsync(), "legacy-row audit returns one row");
        Check.True(
            reader.GetInt32(0) == 0 && reader.GetInt32(1) == 0,
            "migration 093 preserves legacy rows with null v3-only fields");
    }

    private static async Task AssertDriftedSourceFailsClosedAsync(
        NpgsqlDataSource dataSource,
        string connectionString,
        WorldContentFamilyRevision predecessor)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        string originalDisplayName;
        await using (var read = new NpgsqlCommand(
                         """
                         SELECT display_name
                         FROM map_templates
                         WHERE map_id = 0;
                         """,
                         connection))
        {
            originalDisplayName =
                Convert.ToString(await read.ExecuteScalarAsync()) ??
                throw new InvalidDataException(
                    "The upgrade fixture map is missing.");
        }
        await using (var mutate = new NpgsqlCommand(
                         """
                         UPDATE map_templates
                         SET display_name = display_name || ' [v3-drift]'
                         WHERE map_id = 0;
                         """,
                         connection))
        {
            Check.Equal(
                1,
                await mutate.ExecuteNonQueryAsync(),
                "the upgrade fixture drifts one mutable source row");
        }

        try
        {
            WorldContentUnavailableException? rejection = null;
            try
            {
                _ = await PostgresGameplayContentPublisher
                    .EnsurePublishedAsync(connectionString);
            }
            catch (WorldContentUnavailableException error)
            {
                rejection = error;
            }

            Check.True(
                rejection?.Reason ==
                    WorldContentFailureReason.RevisionMismatch,
                "source drift fails closed instead of advancing the pointer");
            Check.Equal(
                predecessor.Sha256,
                await ReadCurrentRevisionAsync(connection),
                "a rejected upgrade leaves the v2 pointer unchanged");
        }
        finally
        {
            await using var restore = new NpgsqlCommand(
                """
                UPDATE map_templates
                SET display_name = @display_name
                WHERE map_id = 0;
                """,
                connection);
            restore.Parameters.AddWithValue(
                "display_name",
                originalDisplayName);
            Check.Equal(
                1,
                await restore.ExecuteNonQueryAsync(),
                "the upgrade fixture restores its exact source row");
        }
    }

    private static async Task AssertUnknownPublisherFailsClosedAsync(
        NpgsqlDataSource dataSource,
        string connectionString,
        WorldContentFamilyRevision predecessor)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using (var mutate = new NpgsqlCommand(
                         """
                         UPDATE gameplay_content_publication
                         SET publisher = 'unknown-v2-publisher'
                         WHERE family = 'gameplay';
                         """,
                         connection))
        {
            Check.Equal(
                1,
                await mutate.ExecuteNonQueryAsync(),
                "the upgrade fixture can model an unknown publisher");
        }

        try
        {
            WorldContentUnavailableException? rejection = null;
            try
            {
                _ = await PostgresGameplayContentPublisher
                    .EnsurePublishedAsync(connectionString);
            }
            catch (WorldContentUnavailableException error)
            {
                rejection = error;
            }

            Check.True(
                rejection?.Reason ==
                    WorldContentFailureReason.RevisionMismatch,
                "an unknown v2 publisher fails closed");
            Check.Equal(
                predecessor.Sha256,
                await ReadCurrentRevisionAsync(connection),
                "an unknown publisher leaves the v2 pointer unchanged");
        }
        finally
        {
            await using var restore = new NpgsqlCommand(
                """
                UPDATE gameplay_content_publication
                SET publisher = @publisher
                WHERE family = 'gameplay';
                """,
                connection);
            restore.Parameters.AddWithValue("publisher", LegacyPublisher);
            Check.Equal(
                1,
                await restore.ExecuteNonQueryAsync(),
                "the upgrade fixture restores the historical publisher");
        }
    }

    private static async Task AssertUpgradeStateAsync(
        NpgsqlDataSource dataSource,
        WorldContentFamilyRevision predecessor,
        GameplayContentPublicationResult upgraded)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        Check.Equal(
            LegacyV2Revision,
            predecessor.Sha256,
            "the fixture recreates the exact reviewed legacy-v2 hash");
        Check.Equal(
            upgraded.Revision,
            await ReadCurrentRevisionAsync(connection),
            "the current pointer advances atomically to Champion authority");
        await using (var audit = new NpgsqlCommand(
                         """
                         WITH legacy_counts(row_count) AS (
                             SELECT COUNT(*)
                             FROM gameplay_map_definitions
                             WHERE revision = @legacy_revision
                             UNION ALL
                             SELECT COUNT(*)
                             FROM gameplay_map_address_points
                             WHERE revision = @legacy_revision
                             UNION ALL
                             SELECT COUNT(*)
                             FROM gameplay_map_links
                             WHERE revision = @legacy_revision
                             UNION ALL
                             SELECT COUNT(*)
                             FROM gameplay_monster_templates
                             WHERE revision = @legacy_revision
                             UNION ALL
                             SELECT COUNT(*)
                             FROM gameplay_world_boss_definitions
                             WHERE revision = @legacy_revision
                             UNION ALL
                             SELECT COUNT(*)
                             FROM gameplay_pending_world_boss_areas
                             WHERE revision = @legacy_revision
                             UNION ALL
                             SELECT COUNT(*)
                             FROM gameplay_class_definitions
                             WHERE revision = @legacy_revision
                             UNION ALL
                             SELECT COUNT(*)
                             FROM gameplay_talent_effect_definitions
                             WHERE revision = @legacy_revision
                             UNION ALL
                             SELECT COUNT(*)
                             FROM gameplay_talent_definitions
                             WHERE revision = @legacy_revision
                             UNION ALL
                             SELECT COUNT(*)
                             FROM gameplay_skill_combat_definitions
                             WHERE revision = @legacy_revision
                             UNION ALL
                             SELECT COUNT(*)
                             FROM gameplay_skill_book_definitions
                             WHERE revision = @legacy_revision
                         )
                         SELECT
                             (SELECT COUNT(*)::integer
                              FROM gameplay_content_revisions),
                             (SELECT SUM(row_count)::bigint
                              FROM legacy_counts),
                             (SELECT COUNT(*)::integer
                              FROM gameplay_map_definitions
                              WHERE revision = @legacy_revision
                                AND map_mode IS NULL),
                             (SELECT COUNT(*)::integer
                              FROM gameplay_map_definitions
                              WHERE revision = @authority_revision
                                AND map_mode IS NOT NULL),
                             (SELECT COUNT(*)::integer
                              FROM gameplay_monster_templates
                              WHERE revision = @authority_revision
                                AND attack_type IS NOT NULL);
                         """,
                         connection))
        {
            audit.Parameters.AddWithValue(
                "legacy_revision",
                predecessor.Sha256);
            audit.Parameters.AddWithValue(
                "authority_revision",
                upgraded.Revision);
            await using var reader = await audit.ExecuteReaderAsync();
            Check.True(
                await reader.ReadAsync(),
                "authority release audit returns one row");
            Check.True(
                reader.GetInt32(0) == 3 &&
                reader.GetInt64(1) == predecessor.EntryCount &&
                reader.GetInt32(2) > 0 &&
                reader.GetInt32(3) > 0 &&
                reader.GetInt32(4) > 0,
                "the chain retains v2 rows and seals corrected v3 authority");
        }

        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead);
        var pinned = await PostgresWorldContentReaderLoader
            .LoadPublishedGameplayContentAsync(
                connection,
                transaction,
                CancellationToken.None);
        var pinnedRevision = WorldContentRevisionHasher.HashGameplay(pinned);
        Check.Equal(
            upgraded.Revision,
            pinnedRevision.Sha256,
            "the runtime reader accepts the corrected publication");
        await transaction.CommitAsync();
    }

    private static async Task<string> ReadCurrentRevisionAsync(
        NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT revision
            FROM gameplay_content_publication
            WHERE family = 'gameplay';
            """,
            connection);
        return Convert.ToString(await command.ExecuteScalarAsync()) ??
            throw new InvalidDataException(
                "The gameplay publication pointer is missing.");
    }
}
