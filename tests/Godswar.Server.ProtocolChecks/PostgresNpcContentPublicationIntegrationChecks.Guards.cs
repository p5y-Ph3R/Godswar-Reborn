using Godswar.Server.Domain.World.Content;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresNpcContentPublicationIntegrationChecks
{
    private static async Task DeleteLegacyNpcSourceFixturesAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        await using (var packet = new NpgsqlCommand(
                         """
                         DELETE FROM npc_spawn_packets
                         WHERE map_id = @map_id
                           AND template_key = @template_key
                           AND npc_key = @npc_key
                           AND source = @source;
                         """,
                         connection,
                         transaction))
        {
            AddLegacyFixtureParameters(packet);
            Check.Equal(
                1,
                await packet.ExecuteNonQueryAsync(),
                "one exact legacy NPC packet decoy is cleaned");
        }

        await using (var reference = new NpgsqlCommand(
                         """
                         DELETE FROM npc_spawn_references
                         WHERE quest_id = @quest_id
                           AND role = 'b05b'
                           AND npc_key = @npc_key
                           AND source = @source;
                         """,
                         connection,
                         transaction))
        {
            AddLegacyFixtureParameters(reference);
            Check.Equal(
                1,
                await reference.ExecuteNonQueryAsync(),
                "one exact legacy NPC reference decoy is cleaned");
        }

        await transaction.CommitAsync();
    }

    private static async Task AssertImmutableDatabaseGuardsAsync(
        NpgsqlDataSource dataSource)
    {
        await AssertIncompletePublicationRejectedAsync(dataSource);
        await AssertExtraDefinitionRejectedAsync(dataSource);
        await AssertMutationRejectedAsync(
            dataSource,
            """
            UPDATE npc_content_revisions
            SET source = source || '-forbidden'
            WHERE revision = @revision;
            """,
            "NPC release update");
        await AssertMutationRejectedAsync(
            dataSource,
            """
            DELETE FROM npc_content_revisions
            WHERE revision = @revision;
            """,
            "NPC release delete");
        await AssertMutationRejectedAsync(
            dataSource,
            """
            UPDATE npc_spawn_definitions
            SET pos_x = pos_x + 1
            WHERE revision = @revision;
            """,
            "NPC definition update");
        await AssertMutationRejectedAsync(
            dataSource,
            """
            DELETE FROM npc_spawn_definitions
            WHERE revision = @revision;
            """,
            "NPC definition delete");
        await AssertMutationRejectedAsync(
            dataSource,
            """
            DELETE FROM npc_content_publication
            WHERE family = 'npcs';
            """,
            "NPC publication delete",
            "publication pointer cannot be deleted");
    }

    private static async Task AssertMutationRejectedAsync(
        NpgsqlDataSource dataSource,
        string sql,
        string scope,
        string expectedMessage =
            "revisions and definitions are immutable")
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        try
        {
            await using var command =
                new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(
                "revision",
                NpgsqlDbType.Varchar,
                ExpectedRevision);
            _ = await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex)
        {
            Check.Equal(
                PostgresErrorCodes.RaiseException,
                ex.SqlState,
                $"{scope} rejection SQLSTATE");
            Check.True(
                ex.MessageText.Contains(
                    expectedMessage,
                    StringComparison.Ordinal),
                $"{scope} is rejected by the intended trigger");
            await transaction.RollbackAsync();
            return;
        }

        await transaction.RollbackAsync();
        throw new InvalidOperationException(
            $"{scope} unexpectedly bypassed the immutable-content guard.");
    }

    private static async Task AssertIncompletePublicationRejectedAsync(
        NpgsqlDataSource dataSource)
    {
        const string incompleteRevision =
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        try
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO npc_content_revisions (
                    revision,
                    entry_count,
                    source
                )
                VALUES (
                    @incomplete_revision,
                    1,
                    'b05b-incomplete-fixture'
                );

                UPDATE npc_content_publication
                SET revision = @incomplete_revision,
                    publisher = 'b05b-forbidden'
                WHERE family = 'npcs';
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue(
                "incomplete_revision",
                NpgsqlDbType.Varchar,
                incompleteRevision);
            _ = await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex)
        {
            AssertGuardRejection(
                ex,
                "declares 1 definitions but contains 0",
                "incomplete NPC publication");
            await transaction.RollbackAsync();
            return;
        }

        await transaction.RollbackAsync();
        throw new InvalidOperationException(
            "An incomplete NPC release was published.");
    }

    private static async Task AssertExtraDefinitionRejectedAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        try
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO npc_spawn_definitions (
                    revision,
                    map_id,
                    scene_key,
                    npc_key,
                    template_key,
                    object_id,
                    pos_x,
                    pos_z,
                    interaction_id,
                    appearance_type,
                    facing
                )
                VALUES (
                    @revision,
                    0,
                    'B05B_Forbidden',
                    'b05b_extra_definition',
                    'b05b_extra_definition',
                    4294905007,
                    0,
                    0,
                    4294905007,
                    1,
                    0
                );
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue(
                "revision",
                NpgsqlDbType.Varchar,
                ExpectedRevision);
            _ = await command.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex)
        {
            AssertGuardRejection(
                ex,
                "already contains its declared 383 definitions",
                "extra NPC definition");
            await transaction.RollbackAsync();
            return;
        }

        await transaction.RollbackAsync();
        throw new InvalidOperationException(
            "An extra NPC definition bypassed the release bound.");
    }

    private static void AssertGuardRejection(
        PostgresException exception,
        string expectedMessage,
        string scope)
    {
        Check.Equal(
            PostgresErrorCodes.RaiseException,
            exception.SqlState,
            $"{scope} rejection SQLSTATE");
        Check.True(
            exception.MessageText.Contains(
                expectedMessage,
                StringComparison.Ordinal),
            $"{scope} is rejected by the intended trigger");
    }

    private static NpcSpawnDefinition[] Canonicalize(
        IEnumerable<NpcSpawnDefinition> definitions) =>
        definitions
            .OrderBy(static definition => definition.MapId)
            .ThenBy(
                static definition => definition.NpcKey,
                StringComparer.Ordinal)
            .ThenBy(
                static definition => definition.TemplateKey,
                StringComparer.Ordinal)
            .ThenBy(static definition => definition.ObjectId)
            .ToArray();

    private static void AssertNpcSequence(
        IReadOnlyList<NpcSpawnDefinition> expected,
        IReadOnlyList<NpcSpawnDefinition> actual,
        string scope)
    {
        var canonicalExpected = Canonicalize(expected);
        var canonicalActual = Canonicalize(actual);
        Check.Equal(
            canonicalExpected.Length,
            canonicalActual.Length,
            $"{scope} count");
        for (var index = 0; index < canonicalExpected.Length; index++)
        {
            var left = canonicalExpected[index];
            var right = canonicalActual[index];
            Check.True(
                left.MapId == right.MapId &&
                string.Equals(
                    left.SceneKey,
                    right.SceneKey,
                    StringComparison.Ordinal) &&
                string.Equals(
                    left.NpcKey,
                    right.NpcKey,
                    StringComparison.Ordinal) &&
                string.Equals(
                    left.TemplateKey,
                    right.TemplateKey,
                    StringComparison.Ordinal) &&
                left.ObjectId == right.ObjectId &&
                left.X.Equals(right.X) &&
                left.Z.Equals(right.Z) &&
                left.InteractionId == right.InteractionId &&
                left.AppearanceType == right.AppearanceType &&
                left.Facing.Equals(right.Facing) &&
                left.Detail10077.SequenceEqual(right.Detail10077) &&
                left.Detail10080.SequenceEqual(right.Detail10080),
                $"{scope} row {index} is byte-identical");
        }
    }
}
