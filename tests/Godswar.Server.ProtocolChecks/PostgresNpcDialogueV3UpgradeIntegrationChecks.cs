using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresNpcDialogueV3UpgradeIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL NPC dialogue rollback-to-V5 Pet Manager upgrade";
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";

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

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        var beforeRevision = await ReadPublishedRevisionAsync(dataSource);
        if (string.Equals(
                beforeRevision,
                NpcDialogueBaselineV1.ExpectedRevision,
                StringComparison.Ordinal))
        {
            var rollbackReader =
                await PostgresWorldContentReaderLoader.LoadAsync(
                    connectionString);
            var rollbackMentor =
                await rollbackReader.ReadNpcDialogueAsync("Athens_070");
            Check.True(
                rollbackMentor.Routes.Count == 1 &&
                rollbackMentor.Route?.Behavior ==
                NpcDialogueBehavior.GearMentor,
                "schema V3 can still pin the immutable V1 rollback release");
        }
        var publication = await PostgresNpcDialogueBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        Check.Equal(
            NpcDialogueBaselineV5.ExpectedRevision,
            publication.Revision,
            "V5 dialogue revision is published");

        if (beforeRevision is not null &&
            !string.Equals(
                beforeRevision,
                NpcDialogueBaselineV5.ExpectedRevision,
                StringComparison.Ordinal))
        {
            await AssertPreviousReleaseRemainsAsync(
                dataSource,
                beforeRevision);
            Check.True(
                publication.Created,
                "previous publication is promoted to immutable V5");
        }

        await AssertHolyStoneRoutesAsync(dataSource);
        await AssertPetManagerRoutesAsync(dataSource);
        var pinned = await PostgresWorldContentReaderLoader.LoadAsync(
            connectionString);
        foreach (var npcKey in new[] { "Athens_086", "Sparta_086" })
        {
            var dialogue = await pinned.ReadNpcDialogueAsync(npcKey);
            Check.True(
                dialogue.Routes.Count == 1 &&
                dialogue.Routes[0].Behavior ==
                    NpcDialogueBehavior.HolyStone &&
                dialogue.Routes[0].InitialMenuSubIds.SequenceEqual(
                    [101, 201, 301, 401, 501, 601, 701, 801]),
                $"{npcKey} loader pins Mount Gear Drilling action 801");
        }
        foreach (var npcKey in new[] { "Athens_088", "Sparta_088" })
        {
            var dialogue = await pinned.ReadNpcDialogueAsync(npcKey);
            Check.True(
                dialogue.Routes.Count == 2 &&
                dialogue.Routes[0].Behavior ==
                    NpcDialogueBehavior.PetManager &&
                dialogue.Routes[0].DialogIndex ==
                    PetManagerProtocol.DialogIndex &&
                dialogue.Routes[0].InitialMenuSubIds.SequenceEqual(
                    PetManagerProtocol.InitialMenuSubIds) &&
                dialogue.Routes[1].RouteOrder == 1 &&
                dialogue.Routes[1].Behavior ==
                    NpcDialogueBehavior.PetPointReset &&
                dialogue.Routes[1].DialogIndex ==
                    PetManagerProtocol.PointResetDialogIndex &&
                dialogue.Routes[1].InitialMenuSubIds.SequenceEqual(
                    PetManagerProtocol.PointResetInitialMenuSubIds),
                $"{npcKey} loader pins both Pet Manager functions");
        }
        var repeat = await PostgresNpcDialogueBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        Check.True(!repeat.Created, "V5 repeat publication is a no-op");
    }

    private static async Task<string?> ReadPublishedRevisionAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT revision
            FROM npc_dialogue_publication
            WHERE family = 'npc-dialogues';
            """);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task AssertPreviousReleaseRemainsAsync(
        NpgsqlDataSource dataSource,
        string revision)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT COUNT(*)::integer
            FROM npc_dialogue_revisions
            WHERE revision = @revision;
            """);
        command.Parameters.AddWithValue(
            "revision",
            revision);
        Check.Equal(
            1,
            (int)(await command.ExecuteScalarAsync() ?? 0),
            "immutable previous dialogue rows remain available for rollback");
    }

    private static async Task AssertHolyStoneRoutesAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT binding.npc_key,
                   binding.route_order,
                   profile.dialog_index,
                   profile.behavior,
                   ARRAY_AGG(entry.sub_id ORDER BY entry.menu_order)
            FROM npc_dialogue_publication publication
            JOIN npc_dialogue_bindings binding
              ON binding.revision = publication.revision
            JOIN npc_dialogue_profiles profile
              ON profile.revision = binding.revision
             AND profile.profile_key = binding.profile_key
            JOIN npc_dialogue_profile_entries entry
              ON entry.revision = profile.revision
             AND entry.profile_key = profile.profile_key
            WHERE publication.family = 'npc-dialogues'
              AND binding.npc_key IN ('Athens_086', 'Sparta_086')
            GROUP BY binding.npc_key,
                     binding.route_order,
                     profile.dialog_index,
                     profile.behavior
            ORDER BY binding.npc_key, binding.route_order;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = 0;
        while (await reader.ReadAsync())
        {
            Check.Equal(
                0,
                (int)reader.GetInt16(1),
                "Holy Stone Artisan route order");
            Check.True(
                reader.GetInt32(2) == 30 &&
                reader.GetInt16(3) ==
                    (short)NpcDialogueBehavior.HolyStone &&
                reader.GetFieldValue<int[]>(4).SequenceEqual(
                    [101, 201, 301, 401, 501, 601, 701, 801]),
                "Holy Stone Artisan publishes action 801 on dialog 30");

            rows++;
        }

        Check.Equal(2, rows, "both city Holy Stone Artisans expose action 801");
    }

    private static async Task AssertPetManagerRoutesAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT binding.npc_key,
                   binding.route_order,
                   profile.dialog_index,
                   profile.behavior,
                   ARRAY_AGG(entry.sub_id ORDER BY entry.menu_order)
            FROM npc_dialogue_publication publication
            JOIN npc_dialogue_bindings binding
              ON binding.revision = publication.revision
            JOIN npc_dialogue_profiles profile
              ON profile.revision = binding.revision
             AND profile.profile_key = binding.profile_key
            JOIN npc_dialogue_profile_entries entry
              ON entry.revision = profile.revision
             AND entry.profile_key = profile.profile_key
            WHERE publication.family = 'npc-dialogues'
              AND binding.npc_key IN ('Athens_088', 'Sparta_088')
            GROUP BY binding.npc_key,
                     binding.route_order,
                     profile.dialog_index,
                     profile.behavior
            ORDER BY binding.npc_key, binding.route_order;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = 0;
        while (await reader.ReadAsync())
        {
            var routeOrder = (int)reader.GetInt16(1);
            var expectedDialog = routeOrder == 0
                ? PetManagerProtocol.DialogIndex
                : PetManagerProtocol.PointResetDialogIndex;
            var expectedBehavior = routeOrder == 0
                ? NpcDialogueBehavior.PetManager
                : NpcDialogueBehavior.PetPointReset;
            var expectedMenu = routeOrder == 0
                ? PetManagerProtocol.InitialMenuSubIds
                : PetManagerProtocol.PointResetInitialMenuSubIds;
            Check.True(
                routeOrder is 0 or 1 &&
                reader.GetInt32(2) == expectedDialog &&
                reader.GetInt16(3) == (short)expectedBehavior &&
                reader.GetFieldValue<int[]>(4).SequenceEqual(expectedMenu),
                $"Pet Manager publishes ordered route {routeOrder}");
            rows++;
        }

        Check.Equal(4, rows, "both city Pet Managers publish two routes");
    }
}
