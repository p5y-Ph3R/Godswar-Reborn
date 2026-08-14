using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresNpcDialoguePublicationIntegrationChecks
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
                "SKIP PostgreSQL NPC dialogue publication " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        await PostgresSchemaStartup.InitializeAsync(connectionString);
        await using (var store = new PostgresGameStore(connectionString))
        {
            await store.EnsureSeedDataAsync();
        }

        await PostgresRelationalContentBaselineBootstrapper.EnsureAsync(
            connectionString);
        _ = await PostgresGameplayContentPublisher.EnsurePublishedAsync(
            connectionString);
        _ = await PostgresNpcContentBaselinePublisher.EnsurePublishedAsync(
            connectionString);
        await AssertDialogueLoaderFailsClosedAsync(connectionString);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await AssertPrePublicationGuardsAsync(dataSource);

        var coldRace = await Task.WhenAll(
            Enumerable.Range(0, 6)
                .Select(_ =>
                    PostgresNpcDialogueBaselinePublisher
                        .EnsurePublishedAsync(connectionString)));
        Check.Equal(
            1,
            coldRace.Count(static result => result.Created),
            "one cold-race publisher creates the dialogue release");
        foreach (var result in coldRace)
        {
            AssertPublication(result);
        }

        _ = await PostgresMonsterContentBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        _ = await PostgresEnterBootstrapBaselinePublisher
            .EnsurePublishedAsync(connectionString);

        var pinned =
            await PostgresWorldContentReaderLoader.LoadAsync(
                connectionString);
        AssertManifest(pinned.Manifest);
        await AssertRoutesAsync(pinned);

        await AssertDatabaseCountsAsync(dataSource);
        await AssertLegacyMutationIsolationAsync(
            dataSource,
            connectionString,
            pinned);
        await AssertImmutableGuardsAsync(dataSource);

        var repeat =
            await PostgresNpcDialogueBaselinePublisher
                .EnsurePublishedAsync(connectionString);
        Check.True(!repeat.Created, "repeat dialogue publication is a no-op");
        AssertPublication(repeat);
    }

    private static async Task AssertDialogueLoaderFailsClosedAsync(
        string connectionString)
    {
        try
        {
            _ = await PostgresWorldContentReaderLoader.LoadAsync(
                connectionString);
        }
        catch (WorldContentUnavailableException ex)
        {
            Check.Equal(
                "npc-dialogues",
                ex.Family,
                "missing dialogue failure family");
            Check.True(
                ex.Reason == WorldContentFailureReason.Missing,
                "missing dialogue failure reason is typed Missing");
            return;
        }

        throw new InvalidOperationException(
            "The PostgreSQL world loader accepted an unpublished dialogue " +
            "family.");
    }

    private static void AssertPublication(
        NpcDialoguePublicationResult result)
    {
        Check.Equal(
            NpcDialogueBaselineV5.ExpectedRevision,
            result.Revision,
            "dialogue release revision");
        Check.Equal(
            NpcDialogueBaselineV5.ExpectedSpawnRevision,
            result.SpawnRevision,
            "dialogue release spawn dependency");
        Check.Equal(
            NpcDialogueBaselineV5.ExpectedTextCount,
            result.TextCount,
            "dialogue release text count");
        Check.Equal(
            NpcDialogueBaselineV5.ExpectedProfileCount,
            result.ProfileCount,
            "dialogue release profile count");
        Check.Equal(
            NpcDialogueBaselineV5.ExpectedRouteCount,
            result.RouteCount,
            "dialogue release route count");
        Check.Equal(
            NpcDialogueBaselineV5.ExpectedMenuEntryCount,
            result.MenuEntryCount,
            "dialogue release menu count");
    }

    private static void AssertManifest(WorldContentManifest manifest)
    {
        Check.Equal(
            "npc-dialogues",
            manifest.NpcDialogues.Family,
            "dialogue manifest family");
        Check.Equal(
            NpcDialogueBaselineV5.ExpectedRevision,
            manifest.NpcDialogues.Sha256,
            "dialogue manifest revision");
        Check.Equal(
            NpcDialogueBaselineV5.ExpectedHashedEntryCount,
            manifest.NpcDialogues.EntryCount,
            "dialogue manifest hashed entry count");
    }

    private static async Task AssertRoutesAsync(
        IWorldContentReader reader)
    {
        var expected = NpcDialogueBaselineV5.CreateRoutes()
            .GroupBy(static route => route.NpcKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);
        foreach (var pair in expected)
        {
            var content = await reader.ReadNpcDialogueAsync(pair.Key);
            Check.Equal(
                pair.Value.Length,
                content.Routes.Count,
                $"{pair.Key} route count");
            for (var index = 0; index < pair.Value.Length; index++)
            {
                var expectedRoute = pair.Value[index];
                var route = content.Routes[index];
                Check.Equal(
                    expectedRoute.RouteOrder,
                    route.RouteOrder,
                    $"{pair.Key} route order");
                Check.Equal(
                    expectedRoute.ClientScriptKey,
                    route.ClientScriptKey,
                    $"{pair.Key} client script");
                Check.Equal(
                    expectedRoute.DialogIndex,
                    route.DialogIndex,
                    $"{pair.Key} dialog index");
                Check.True(
                    expectedRoute.Behavior == route.Behavior,
                    $"{pair.Key} behavior");
                Check.True(
                    expectedRoute.InitialMenuSubIds.SequenceEqual(
                        route.InitialMenuSubIds),
                    $"{pair.Key} ordered menu");
            }
            Check.True(
                !string.IsNullOrWhiteSpace(content.Text.DisplayName) &&
                !string.IsNullOrWhiteSpace(content.Text.Description),
                $"{pair.Key} has official text");
        }
    }

    private static async Task AssertDatabaseCountsAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT release.text_count,
                   release.profile_count,
                   release.route_count,
                   release.menu_entry_count,
                   (
                       SELECT COUNT(*)::integer
                       FROM npc_dialogue_texts text
                       WHERE text.revision = release.revision
                   ),
                   (
                       SELECT COUNT(*)::integer
                       FROM npc_dialogue_profiles profile
                       WHERE profile.revision = release.revision
                   ),
                   (
                       SELECT COUNT(*)::integer
                       FROM npc_dialogue_bindings binding
                       WHERE binding.revision = release.revision
                   ),
                   (
                       SELECT COUNT(*)::integer
                       FROM npc_dialogue_profile_entries entry
                       WHERE entry.revision = release.revision
                   )
            FROM npc_dialogue_publication publication
            JOIN npc_dialogue_revisions release
              ON release.revision = publication.revision
            WHERE publication.family = 'npc-dialogues';
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "dialogue publication exists");
        var expected = new[]
        {
            NpcDialogueBaselineV5.ExpectedTextCount,
            NpcDialogueBaselineV5.ExpectedProfileCount,
            NpcDialogueBaselineV5.ExpectedRouteCount,
            NpcDialogueBaselineV5.ExpectedMenuEntryCount
        };
        for (var index = 0; index < expected.Length; index++)
        {
            Check.Equal(
                expected[index],
                reader.GetInt32(index),
                $"declared dialogue count {index}");
            Check.Equal(
                expected[index],
                reader.GetInt32(index + expected.Length),
                $"stored dialogue count {index}");
        }
    }

    private static async Task AssertLegacyMutationIsolationAsync(
        NpgsqlDataSource dataSource,
        string connectionString,
        IWorldContentReader pinned)
    {
        const string npcKey = "Sparta_070";
        string originalDescription;
        await using (var read = dataSource.CreateCommand(
                         """
                         SELECT description
                         FROM npc_text_templates
                         WHERE npc_key = 'Sparta_070';
                         """))
        {
            originalDescription =
                (string?)await read.ExecuteScalarAsync() ??
                throw new InvalidOperationException(
                    "Legacy test text is missing.");
        }

        try
        {
            await using (var mutate = dataSource.CreateCommand(
                             """
                             UPDATE npc_text_templates
                             SET description = 'legacy dialogue decoy'
                             WHERE npc_key = 'Sparta_070';
                             """))
            {
                Check.Equal(
                    1,
                    await mutate.ExecuteNonQueryAsync(),
                    "legacy dialogue text decoy inserted");
            }

            var refreshed =
                await PostgresWorldContentReaderLoader.LoadAsync(
                    connectionString);
            AssertManifest(refreshed.Manifest);
            var before = await pinned.ReadNpcDialogueAsync(npcKey);
            var after = await refreshed.ReadNpcDialogueAsync(npcKey);
            Check.Equal(
                before.Text.Description,
                after.Text.Description,
                "legacy text mutation cannot change official dialogue");
        }
        finally
        {
            await using var restore = dataSource.CreateCommand(
                """
                UPDATE npc_text_templates
                SET description = @description
                WHERE npc_key = 'Sparta_070';
                """);
            restore.Parameters.AddWithValue(
                "description",
                originalDescription);
            _ = await restore.ExecuteNonQueryAsync();
        }
    }

    private static async Task AssertImmutableGuardsAsync(
        NpgsqlDataSource dataSource)
    {
        await AssertRejectedAsync(
            dataSource,
            """
            UPDATE npc_dialogue_texts
            SET display_name = 'mutated'
            WHERE revision = (
                SELECT revision
                FROM npc_dialogue_publication
                WHERE family = 'npc-dialogues'
            )
              AND npc_key = 'Sparta_070';
            """,
            "published dialogue text update");
        await AssertRejectedAsync(
            dataSource,
            """
            INSERT INTO npc_dialogue_texts (
                revision, npc_key, scene_key, display_name, description
            )
            VALUES (
                (
                    SELECT revision
                    FROM npc_dialogue_publication
                    WHERE family = 'npc-dialogues'
                ),
                'Sparta_PostPublicationDecoy',
                'Sparta',
                'Decoy',
                'decoy'
            );
            """,
            "published dialogue child insert");
        await AssertRejectedAsync(
            dataSource,
            """
            DELETE FROM npc_dialogue_publication
            WHERE family = 'npc-dialogues';
            """,
            "dialogue publication delete");
    }

    private static async Task AssertRejectedAsync(
        NpgsqlDataSource dataSource,
        string sql,
        string operation)
    {
        try
        {
            await using var command = dataSource.CreateCommand(sql);
            _ = await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{operation} unexpectedly succeeded.");
    }

}
