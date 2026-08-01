using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.Items;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresItemTemplateContentIntegrationChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

    public static async Task RunAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                "SKIP PostgreSQL item-template publication " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await PostgresSchemaStartup.InitializeAsync(connectionString);
        var first = await PostgresItemTemplateContentBootstrapper
            .LoadAsync(connectionString);
        Check.True(
            first.Revision.EntryCount > 0 &&
            first.Revision.Sha256.Length == 64,
            "item-template bootstrap pins a non-empty SHA-256 revision");

        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await AssertV1UpgradeAsync(
            dataSource,
            first.Revision.Sha256);
        await AssertV2UpgradeAsync(
            dataSource,
            first.Revision.Sha256);
        await AssertV3UpgradeAsync(
            dataSource,
            first.Revision.Sha256);
        await AssertCorruptV1RejectedAsync(
            dataSource,
            first.Revision.Sha256);
        await AssertRuntimeViewsUsePublicationAsync(
            dataSource,
            first.Revision.EntryCount);
        await AssertDefinitionCountGuardAsync(dataSource);
        await AssertPolicyCountGuardsAsync(dataSource);
        await AssertHeaderSealMutationGuardsAsync(dataSource);
        await AssertPublicationCompletenessGuardAsync(dataSource);
        var originalName = await ReadSourceDisplayNameAsync(dataSource, 1000);
        try
        {
            await UpdateSourceDisplayNameAsync(
                dataSource,
                1000,
                originalName + " source-decoy");
            var repeat = await PostgresItemTemplateBaselinePublisher
                .EnsurePublishedAsync(dataSource);
            Check.Equal(
                first.Revision.Sha256,
                repeat.Revision,
                "an existing official publication wins over source-table changes");
            var reloaded = await PostgresItemTemplateCatalogLoader.LoadAsync(
                dataSource);
            Check.True(
                reloaded.TryGet(1000, out var definition) &&
                definition.DisplayName == originalName,
                "runtime loader ignores unversioned source-table mutation");
            Check.Equal(
                originalName,
                await ReadOfficialDisplayNameAsync(dataSource, 1000),
                "database runtime projections ignore staging-table mutation");
        }
        finally
        {
            await UpdateSourceDisplayNameAsync(
                dataSource,
                1000,
                originalName);
        }

        await AssertRejectedAsync(
            dataSource,
            """
            UPDATE item_template_content_definitions
            SET display_name = display_name || ' mutation'
            WHERE revision = (
                SELECT revision
                FROM item_template_content_publication
                WHERE family = 'items')
              AND id = 1000;
            """,
            "published item definitions reject updates");
        await AssertRejectedAsync(
            dataSource,
            """
            DELETE FROM item_template_content_publication
            WHERE family = 'items';
            """,
            "official item publication rejects deletion");
        await AssertRejectedAsync(
            dataSource,
            """
            INSERT INTO item_template_content_definitions (
                revision, id, kind, name_key, display_name,
                equipment_slot, class_ids, texture, icon, stats)
            SELECT revision, 2147483000, 'weapon', 'late', 'Late Insert',
                   10, ARRAY[0]::smallint[], '', '', '{}'::jsonb
            FROM item_template_content_publication
            WHERE family = 'items';
            """,
            "sealed item revision rejects late inserts");
    }

    private static async Task AssertRuntimeViewsUsePublicationAsync(
        NpgsqlDataSource dataSource,
        int expectedCount)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT
                (
                    SELECT count(*)::integer
                    FROM public.official_item_template_content
                ),
                (
                    SELECT count(*)::integer
                    FROM (
                        SELECT DISTINCT view_class.oid
                        FROM pg_rewrite rewrite
                        JOIN pg_class view_class
                          ON view_class.oid = rewrite.ev_class
                        JOIN pg_namespace view_namespace
                          ON view_namespace.oid = view_class.relnamespace
                        JOIN pg_depend dependency
                          ON dependency.classid = 'pg_rewrite'::regclass
                         AND dependency.objid = rewrite.oid
                        WHERE dependency.refobjid =
                                  'public.item_templates'::regclass
                          AND view_class.relkind = 'v'
                          AND view_namespace.nspname = 'public'
                    ) mutable_view
                ),
                (
                    SELECT count(*) = release.attribute_count
                    FROM public.official_item_attribute_content definition
                    JOIN public.item_template_content_publication publication
                      ON publication.family = 'items'
                    JOIN public.item_template_content_revisions release
                      ON release.revision = publication.revision
                    GROUP BY release.attribute_count
                ),
                (
                    SELECT count(*) = release.equipment_rank_count
                    FROM public.official_equipment_rank_content definition
                    JOIN public.item_template_content_publication publication
                      ON publication.family = 'items'
                    JOIN public.item_template_content_revisions release
                      ON release.revision = publication.revision
                    GROUP BY release.equipment_rank_count
                ),
                (
                    SELECT count(*) = release.holy_suit_effect_count
                    FROM public.official_holy_suit_effect_content definition
                    JOIN public.item_template_content_publication publication
                      ON publication.family = 'items'
                    JOIN public.item_template_content_revisions release
                      ON release.revision = publication.revision
                    GROUP BY release.holy_suit_effect_count
                ),
                (
                    SELECT count(*) = release.material_policy_count
                    FROM public.official_item_material_content definition
                    JOIN public.item_template_content_publication publication
                      ON publication.family = 'items'
                    JOIN public.item_template_content_revisions release
                      ON release.revision = publication.revision
                    GROUP BY release.material_policy_count
                ),
                (
                    SELECT count(*) FILTER (
                               WHERE definition.recipe_kind IS NOT NULL) =
                           release.material_recipe_count
                    FROM public.official_item_material_content definition
                    JOIN public.item_template_content_publication publication
                      ON publication.family = 'items'
                    JOIN public.item_template_content_revisions release
                      ON release.revision = publication.revision
                    GROUP BY release.material_recipe_count
                ),
                (
                    SELECT count(*)::integer
                    FROM (
                        SELECT DISTINCT view_class.oid
                        FROM pg_rewrite rewrite
                        JOIN pg_class view_class
                          ON view_class.oid = rewrite.ev_class
                        JOIN pg_namespace view_namespace
                          ON view_namespace.oid = view_class.relnamespace
                        JOIN pg_depend dependency
                          ON dependency.classid = 'pg_rewrite'::regclass
                         AND dependency.objid = rewrite.oid
                        WHERE dependency.refobjid = ANY(ARRAY[
                                  'public.item_attribute_templates'::regclass,
                                  'public.equipment_rank_rules'::regclass,
                                  'public.holy_suit_effect_templates'::regclass
                              ]::oid[])
                          AND view_class.relkind = 'v'
                          AND view_namespace.nspname = 'public'
                    ) mutable_policy_view
                );
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "item runtime-view dependency audit returns one row");
        Check.Equal(
            expectedCount,
            reader.GetInt32(0),
            "official item view exposes the complete publication");
        Check.Equal(
            0,
            reader.GetInt32(1),
            "no public view depends on mutable item_templates");
        Check.True(
            reader.GetBoolean(2) &&
            reader.GetBoolean(3) &&
            reader.GetBoolean(4) &&
            reader.GetBoolean(5) &&
            reader.GetBoolean(6),
            "official item-policy views expose the complete v4 manifest");
        Check.Equal(
            0,
            reader.GetInt32(7),
            "no public view depends on mutable item policy tables");
    }

    private static async Task AssertDefinitionCountGuardAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var revision = new string('F', 63) + "1";
        var rolledBack = false;
        try
        {
            await InsertTestRevisionAsync(
                connection,
                transaction,
                revision,
                entryCount: 1);
            await InsertTestDefinitionAsync(
                connection,
                transaction,
                revision,
                itemId: 2147483001);
            try
            {
                await InsertTestDefinitionAsync(
                    connection,
                    transaction,
                    revision,
                    itemId: 2147483002);
            }
            catch (PostgresException)
            {
                await transaction.RollbackAsync();
                rolledBack = true;
                return;
            }

            throw new InvalidOperationException(
                "Item revision accepted more than its declared count.");
        }
        finally
        {
            if (!rolledBack)
            {
                await transaction.RollbackAsync();
            }
        }
    }

    private static async Task AssertPublicationCompletenessGuardAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var revision = new string('E', 63) + "2";
        var rolledBack = false;
        try
        {
            await InsertTestRevisionAsync(
                connection,
                transaction,
                revision,
                entryCount: 2);
            await InsertTestDefinitionAsync(
                connection,
                transaction,
                revision,
                itemId: 2147483003);
            await using var publish = new NpgsqlCommand("""
                INSERT INTO item_template_content_publication (
                    family, revision)
                VALUES ('items', @revision)
                ON CONFLICT (family) DO UPDATE
                SET revision = EXCLUDED.revision;
                """, connection, transaction);
            publish.Parameters.AddWithValue("revision", revision);
            try
            {
                await publish.ExecuteNonQueryAsync();
            }
            catch (PostgresException)
            {
                await transaction.RollbackAsync();
                rolledBack = true;
                return;
            }

            throw new InvalidOperationException(
                "Item publication accepted an incomplete revision.");
        }
        finally
        {
            if (!rolledBack)
            {
                await transaction.RollbackAsync();
            }
        }
    }

    private static async Task InsertTestRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        int entryCount)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO item_template_content_revisions (
                revision, entry_count, source)
            VALUES (@revision, @entryCount, 'integration-rollback');
            """, connection, transaction);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("entryCount", entryCount);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertTestDefinitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        int itemId)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO item_template_content_definitions (
                revision, id, kind, name_key, display_name,
                equipment_slot, class_ids, texture, icon, stats)
            VALUES (
                @revision, @itemId, 'weapon', 'guard', 'Guard Test',
                10, ARRAY[0]::smallint[], '', '', '{}'::jsonb);
            """, connection, transaction);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("itemId", itemId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadSourceDisplayNameAsync(
        NpgsqlDataSource dataSource,
        int itemId)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT display_name
            FROM item_templates
            WHERE id = @itemId;
            """);
        command.Parameters.AddWithValue("itemId", itemId);
        return (string)(await command.ExecuteScalarAsync() ??
            throw new InvalidOperationException(
                $"Item template {itemId} is missing."));
    }

    private static async Task<string> ReadOfficialDisplayNameAsync(
        NpgsqlDataSource dataSource,
        int itemId)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT display_name
            FROM public.official_item_template_content
            WHERE id = @itemId;
            """);
        command.Parameters.AddWithValue("itemId", itemId);
        return (string)(await command.ExecuteScalarAsync() ??
            throw new InvalidOperationException(
                $"Official item template {itemId} is missing."));
    }

    private static async Task UpdateSourceDisplayNameAsync(
        NpgsqlDataSource dataSource,
        int itemId,
        string displayName)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE item_templates
            SET display_name = @displayName
            WHERE id = @itemId;
            """);
        command.Parameters.AddWithValue("itemId", itemId);
        command.Parameters.AddWithValue("displayName", displayName);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertRejectedAsync(
        NpgsqlDataSource dataSource,
        string sql,
        string message)
    {
        try
        {
            await using var command = dataSource.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }
}
