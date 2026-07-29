using Godswar.Server.Infrastructure.WorldContent;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresNpcDialoguePublicationIntegrationChecks
{
    private static async Task AssertPrePublicationGuardsAsync(
        NpgsqlDataSource dataSource)
    {
        await AssertPartialPublicationRejectedAsync(dataSource);
        await AssertOverCountRejectedAsync(dataSource);
        await AssertSpawnMismatchRejectedAsync(dataSource);
    }

    private static async Task AssertPartialPublicationRejectedAsync(
        NpgsqlDataSource dataSource)
    {
        const string revision =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var release = new NpgsqlCommand(
                             """
                             INSERT INTO npc_dialogue_revisions (
                                 revision, spawn_revision, text_count,
                                 profile_count, route_count, menu_entry_count,
                                 source
                             )
                             VALUES (
                                 @revision,
                                 @spawn_revision,
                                 383,
                                 1,
                                 1,
                                 1,
                                 'guard-test'
                             );
                             """,
                             connection,
                             transaction))
            {
                release.Parameters.AddWithValue("revision", revision);
                release.Parameters.AddWithValue(
                    "spawn_revision",
                    NpcDialogueBaselineV1.ExpectedSpawnRevision);
                _ = await release.ExecuteNonQueryAsync();
            }

            await AssertRejectedInTransactionAsync(
                connection,
                transaction,
                """
                INSERT INTO npc_dialogue_publication (
                    family, revision, publisher
                )
                VALUES (
                    'npc-dialogues',
                    'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                    'guard-test'
                );
                """,
                "partial dialogue publication");
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task AssertOverCountRejectedAsync(
        NpgsqlDataSource dataSource)
    {
        const string revision =
            "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var setup = new NpgsqlCommand(
                             """
                             INSERT INTO npc_dialogue_revisions (
                                 revision, spawn_revision, text_count,
                                 profile_count, route_count, menu_entry_count,
                                 source
                             )
                             VALUES (
                                 @revision,
                                 @spawn_revision,
                                 1,
                                 1,
                                 1,
                                 1,
                                 'guard-test'
                             );
                             INSERT INTO npc_dialogue_texts (
                                 revision, npc_key, scene_key,
                                 display_name, description
                             )
                             VALUES (
                                 @revision,
                                 'Sparta_070',
                                 'Sparta',
                                 'Gear Mentor',
                                 'test'
                             );
                             """,
                             connection,
                             transaction))
            {
                setup.Parameters.AddWithValue("revision", revision);
                setup.Parameters.AddWithValue(
                    "spawn_revision",
                    NpcDialogueBaselineV1.ExpectedSpawnRevision);
                _ = await setup.ExecuteNonQueryAsync();
            }

            await AssertRejectedInTransactionAsync(
                connection,
                transaction,
                $"""
                 INSERT INTO npc_dialogue_texts (
                     revision, npc_key, scene_key, display_name, description
                 )
                 VALUES (
                     '{revision}',
                     'Sparta_085',
                     'Sparta',
                     'Master Vestment Forger',
                     'test'
                 );
                 """,
                "over-count dialogue text insert");
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task AssertSpawnMismatchRejectedAsync(
        NpgsqlDataSource dataSource)
    {
        const string spawnRevision =
            "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        const string dialogueRevision =
            "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD";
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using (var setup = new NpgsqlCommand(
                             """
                             INSERT INTO npc_content_revisions (
                                 revision, entry_count, source
                             )
                             VALUES (@spawn_revision, 1, 'guard-test');
                             INSERT INTO npc_spawn_definitions (
                                 revision, map_id, scene_key, npc_key,
                                 template_key, object_id, pos_x, pos_z,
                                 interaction_id, appearance_type, facing
                             )
                             VALUES (
                                 @spawn_revision,
                                 0,
                                 'Sparta',
                                 'Sparta_TestDialogue',
                                 'Sparta_TestDialogue_Male',
                                 4000000000,
                                 0,
                                 0,
                                 4000000000,
                                 17,
                                 1
                             );
                             INSERT INTO npc_dialogue_revisions (
                                 revision, spawn_revision, text_count,
                                 profile_count, route_count, menu_entry_count,
                                 source
                             )
                             VALUES (
                                 @dialogue_revision,
                                 @spawn_revision,
                                 1,
                                 1,
                                 1,
                                 1,
                                 'guard-test'
                             );
                             INSERT INTO npc_dialogue_texts (
                                 revision, npc_key, scene_key,
                                 display_name, description
                             )
                             VALUES (
                                 @dialogue_revision,
                                 'Sparta_TestDialogue',
                                 'Sparta',
                                 'Test Dialogue',
                                 'test'
                             );
                             INSERT INTO npc_dialogue_profiles (
                                 revision, profile_key, dialog_index,
                                 behavior, initial_request_sub_id
                             )
                             VALUES (
                                 @dialogue_revision,
                                 'guard_test',
                                 4,
                                 1,
                                 -1
                             );
                             INSERT INTO npc_dialogue_profile_entries (
                                 revision, profile_key, menu_order, sub_id
                             )
                             VALUES (
                                 @dialogue_revision,
                                 'guard_test',
                                 0,
                                 1
                             );
                             INSERT INTO npc_dialogue_bindings (
                                 revision, npc_key, client_script_key,
                                 profile_key
                             )
                             VALUES (
                                 @dialogue_revision,
                                 'Sparta_TestDialogue',
                                 'Sparta_TestDialogue',
                                 'guard_test'
                             );
                             """,
                             connection,
                             transaction))
            {
                setup.Parameters.AddWithValue(
                    "spawn_revision",
                    spawnRevision);
                setup.Parameters.AddWithValue(
                    "dialogue_revision",
                    dialogueRevision);
                _ = await setup.ExecuteNonQueryAsync();
            }

            await AssertRejectedInTransactionAsync(
                connection,
                transaction,
                $"""
                 INSERT INTO npc_dialogue_publication (
                     family, revision, publisher
                 )
                 VALUES (
                     'npc-dialogues',
                     '{dialogueRevision}',
                     'guard-test'
                 );
                 """,
                "dialogue publication for an unpublished spawn revision");
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task AssertRejectedInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        string operation)
    {
        try
        {
            await using var command = new NpgsqlCommand(
                sql,
                connection,
                transaction);
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
