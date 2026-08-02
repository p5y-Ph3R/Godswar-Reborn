using Godswar.Server.Domain.World.Content;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.WorldContent;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static class PostgresNpcDialogueV2UpgradeIntegrationChecks
{
    public const string CheckName =
        "PostgreSQL NPC dialogue V2 multi-route upgrade";
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
                "schema V2 can still pin the immutable V1 rollback release");
        }
        var publication = await PostgresNpcDialogueBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        Check.Equal(
            NpcDialogueBaselineV2.ExpectedRevision,
            publication.Revision,
            "V2 dialogue revision is published");

        if (string.Equals(
                beforeRevision,
                NpcDialogueBaselineV1.ExpectedRevision,
                StringComparison.Ordinal))
        {
            await AssertV1RollbackReleaseRemainsAsync(dataSource);
            Check.True(
                publication.Created,
                "V1 publication is promoted to immutable V2");
        }

        await AssertGearMentorRoutesAsync(dataSource);
        var pinned = await PostgresWorldContentReaderLoader.LoadAsync(
            connectionString);
        foreach (var npcKey in new[] { "Athens_070", "Sparta_070" })
        {
            var dialogue = await pinned.ReadNpcDialogueAsync(npcKey);
            Check.True(
                dialogue.Routes.Count == 2 &&
                dialogue.Routes[0].Behavior ==
                NpcDialogueBehavior.GearMentor &&
                dialogue.Routes[1].Behavior ==
                NpcDialogueBehavior.ClassSuit,
                $"{npcKey} loader pins both ordered functions");
        }
        var repeat = await PostgresNpcDialogueBaselinePublisher
            .EnsurePublishedAsync(connectionString);
        Check.True(!repeat.Created, "V2 repeat publication is a no-op");
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

    private static async Task AssertV1RollbackReleaseRemainsAsync(
        NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT COUNT(*)::integer
            FROM npc_dialogue_revisions
            WHERE revision = @revision;
            """);
        command.Parameters.AddWithValue(
            "revision",
            NpcDialogueBaselineV1.ExpectedRevision);
        Check.Equal(
            1,
            (int)(await command.ExecuteScalarAsync() ?? 0),
            "immutable V1 dialogue rows remain available for rollback");
    }

    private static async Task AssertGearMentorRoutesAsync(
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
              AND binding.npc_key IN ('Athens_070', 'Sparta_070')
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
            var expectedOrder = rows % 2;
            Check.Equal(
                expectedOrder,
                (int)reader.GetInt16(1),
                "Gear Mentor route order");
            if (expectedOrder == 0)
            {
                Check.True(
                    reader.GetInt32(2) == 4 &&
                    reader.GetInt16(3) ==
                    (short)NpcDialogueBehavior.GearMentor,
                    "primary Gear Mentor function remains dialog 4");
            }
            else
            {
                Check.True(
                    reader.GetInt32(2) == 37 &&
                    reader.GetInt16(3) ==
                    (short)NpcDialogueBehavior.ClassSuit &&
                    reader.GetFieldValue<int[]>(4).SequenceEqual(
                        [100, 101, 102, 103, 104, 105, 106, 107, 108]),
                    "secondary Class Suit function uses stock dialog 37");
            }

            rows++;
        }

        Check.Equal(4, rows, "both city Gear Mentors expose two functions");
    }
}
