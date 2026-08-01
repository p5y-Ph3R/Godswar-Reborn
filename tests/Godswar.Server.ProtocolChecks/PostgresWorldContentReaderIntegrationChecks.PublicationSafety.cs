using System.Data;
using Godswar.Server.Application.World;
using Godswar.Server.Infrastructure.Database;
using Godswar.Server.Infrastructure.WorldContent;
using Npgsql;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresWorldContentReaderIntegrationChecks
{
    private static async Task AssertPartialRelationalBaselineRejectedAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable);
        try
        {
            await using (var remove = new NpgsqlCommand(
                             "DELETE FROM skill_book_templates;",
                             connection,
                             transaction))
            {
                Check.True(
                    await remove.ExecuteNonQueryAsync() > 0,
                    "partial-baseline fixture removes skill books");
            }

            InvalidDataException? rejection = null;
            try
            {
                _ = await PostgresRelationalContentBaselineBootstrapper
                    .EnsureAsync(connection, transaction);
            }
            catch (InvalidDataException error)
            {
                rejection = error;
            }

            Check.True(
                rejection?.Message.Contains(
                    "relational baseline is partial",
                    StringComparison.Ordinal) == true,
                "partial mutable source family fails closed before publication");
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task
        AssertPublishedGameplayPermitsMissingSourceAsync(
            NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable);
        try
        {
            await using (var remove = new NpgsqlCommand(
                             "DELETE FROM skill_book_templates;",
                             connection,
                             transaction))
            {
                Check.True(
                    await remove.ExecuteNonQueryAsync() > 0,
                    "published-authority fixture removes mutable skill books");
            }

            var result = await PostgresRelationalContentBaselineBootstrapper
                .EnsureAsync(connection, transaction);
            Check.True(
                !result.SkillsCreated && !result.GameplayPolicyApplied,
                "published gameplay prevents mutable-source repair and policy edits");

            await using var count = new NpgsqlCommand(
                "SELECT COUNT(*)::integer FROM skill_book_templates;",
                connection,
                transaction);
            Check.Equal(
                0,
                Convert.ToInt32(await count.ExecuteScalarAsync()),
                "published authority never repopulates a missing source table");
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task
        AssertPublishedGameplayIgnoresSourceMutationAsync(
            NpgsqlDataSource dataSource,
            string connectionString,
            IWorldContentReader pinned)
    {
        var skill = pinned.Gameplay.SkillCombatDefinitions
            .OrderBy(static definition => definition.SkillId)
            .First();
        string? originalDisplayName = null;
        var sourceMutated = false;
        await using var connection = await dataSource.OpenConnectionAsync();
        try
        {
            await using (var read = new NpgsqlCommand(
                             """
                             SELECT display_name
                             FROM skill_templates
                             WHERE skill_id = @skillId;
                             """,
                             connection))
            {
                read.Parameters.AddWithValue("skillId", skill.SkillId);
                originalDisplayName =
                    Convert.ToString(await read.ExecuteScalarAsync());
            }

            Check.True(
                !string.IsNullOrWhiteSpace(originalDisplayName),
                "gameplay source mutation fixture locates its skill");
            await using (var mutate = new NpgsqlCommand(
                             """
                             UPDATE skill_templates
                             SET display_name = @displayName
                             WHERE skill_id = @skillId;
                             """,
                             connection))
            {
                mutate.Parameters.AddWithValue(
                    "displayName",
                    originalDisplayName + " [mutable-source-decoy]");
                mutate.Parameters.AddWithValue("skillId", skill.SkillId);
                Check.Equal(
                    1,
                    await mutate.ExecuteNonQueryAsync(),
                    "gameplay source mutation changes one skill row");
                sourceMutated = true;
            }

            var reloaded = await PostgresWorldContentReaderLoader.LoadAsync(
                connectionString);
            Check.Equal(
                pinned.Manifest.Gameplay.Sha256,
                reloaded.Manifest.Gameplay.Sha256,
                "published gameplay revision ignores mutable source edits");
            Check.Equal(
                skill.DisplayName,
                reloaded.Gameplay.SkillCombatDefinitions.Single(
                    definition => definition.SkillId == skill.SkillId)
                    .DisplayName,
                "published skill data ignores mutable source edits");
        }
        finally
        {
            if (sourceMutated)
            {
                await using var restore = new NpgsqlCommand(
                    """
                    UPDATE skill_templates
                    SET display_name = @displayName
                    WHERE skill_id = @skillId;
                    """,
                    connection);
                restore.Parameters.AddWithValue(
                    "displayName",
                    originalDisplayName!);
                restore.Parameters.AddWithValue("skillId", skill.SkillId);
                Check.Equal(
                    1,
                    await restore.ExecuteNonQueryAsync(),
                    "gameplay source mutation fixture restores its skill");
            }
        }
    }

    private static async Task AssertPoisonedGameplayReleaseRejectedAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead);
        try
        {
            var canonical = await PostgresGameplayContentPublisher
                .ReadCanonicalSourceContentAsync(
                    connection,
                    transaction);
            var revision = WorldContentRevisionHasher.HashGameplay(canonical);
            Check.True(
                await PostgresGameplayContentPublisher.InsertReleaseAsync(
                    connection,
                    transaction,
                    revision,
                    canonical,
                    CancellationToken.None),
                "poison fixture creates an unpublished release header");

            await using (var mutate = new NpgsqlCommand(
                             """
                             UPDATE map_templates
                             SET display_name = display_name || ' [poison]'
                             WHERE map_id = 0;
                             """,
                             connection,
                             transaction))
            {
                Check.Equal(
                    1,
                    await mutate.ExecuteNonQueryAsync(),
                    "poison fixture changes one source row");
            }

            await PostgresGameplayContentPublisher.CopyDefinitionsAsync(
                connection,
                transaction,
                revision.Sha256,
                canonical,
                CancellationToken.None);

            await using (var restore = new NpgsqlCommand(
                             """
                             UPDATE map_templates
                             SET display_name = @displayName
                             WHERE map_id = 0;
                             """,
                             connection,
                             transaction))
            {
                restore.Parameters.AddWithValue(
                    "displayName",
                    canonical.Maps.Single(
                        static map => map.MapId == 0).DisplayName);
                Check.Equal(
                    1,
                    await restore.ExecuteNonQueryAsync(),
                    "poison fixture restores the authoritative source");
            }

            WorldContentUnavailableException? rejection = null;
            try
            {
                _ = await PostgresGameplayContentPublisher
                    .EnsurePublishedAsync(
                        connection,
                        transaction);
            }
            catch (WorldContentUnavailableException error)
            {
                rejection = error;
            }

            Check.True(
                rejection?.Reason ==
                    WorldContentFailureReason.RevisionMismatch,
                "same-count wrong-content release is rejected by hash");
            await using var pointer = new NpgsqlCommand(
                """
                SELECT COUNT(*)::integer
                FROM gameplay_content_publication
                WHERE family = 'gameplay';
                """,
                connection,
                transaction);
            Check.Equal(
                0,
                Convert.ToInt32(await pointer.ExecuteScalarAsync()),
                "poisoned release never moves the publication pointer");
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }
}
