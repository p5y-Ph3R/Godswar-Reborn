using System.Data;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.Database;

internal sealed record RelationalContentBootstrapResult(
    bool ItemAttributesCreated,
    bool SkillsCreated,
    bool NpcsCreated,
    bool MapsCreated,
    bool MonstersCreated,
    bool GameplayPolicyApplied);

/// <summary>
/// Explicit, bounded clean-install boundary for reviewed generated SQL assets.
/// It runs after schema migrations and before immutable content publication.
/// Runtime gameplay never reads these assets or their mutable source tables.
/// </summary>
internal static partial class PostgresRelationalContentBaselineBootstrapper
{
    private const int BootstrapLockNamespace = 1_193_657_936;
    private const int BootstrapLockKey = 1_191_384_731;
    private const int MaximumResourceBytes = 2_000_000;

    public static async Task<RelationalContentBootstrapResult>
        EnsureAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
        var result = await EnsureAsync(
            connection,
            transaction,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal static async Task<RelationalContentBootstrapResult>
        EnsureAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (connection.State != ConnectionState.Open ||
            !ReferenceEquals(transaction.Connection, connection))
        {
            throw new ArgumentException(
                "The baseline transaction must belong to the open connection.",
                nameof(transaction));
        }

        await AcquireLockAsync(connection, transaction, cancellationToken);

        var itemAttributesCreated = await EnsureFamilyAsync(
            connection,
            transaction,
            "item attributes",
            ["item_attribute_templates"],
            """
            EXISTS (
                SELECT 1 FROM item_template_content_publication
                WHERE family = 'items'
            )
            """,
            ItemAttributesResource,
            cancellationToken);
        var skillsCreated = await EnsureFamilyAsync(
            connection,
            transaction,
            "skills and talents",
            [
                "class_templates",
                "talent_effect_templates",
                "talent_templates",
                "skill_templates",
                "skill_book_templates"
            ],
            """
            EXISTS (
                SELECT 1 FROM gameplay_content_publication
                WHERE family = 'gameplay'
            )
            """,
            SkillsResource,
            cancellationToken);
        var npcsCreated = await EnsureFamilyAsync(
            connection,
            transaction,
            "NPCs and dialogue",
            [
                "npc_text_templates",
                "npc_appearance_templates",
                "npc_spawn_references",
                "npc_function_templates",
                "npc_dialog_templates"
            ],
            """
            EXISTS (
                SELECT 1 FROM npc_content_publication
                WHERE family = 'npcs'
            ) AND EXISTS (
                SELECT 1 FROM npc_dialogue_publication
                WHERE family = 'npc-dialogues'
            )
            """,
            NpcsResource,
            cancellationToken);
        var mapsCreated = await EnsureFamilyAsync(
            connection,
            transaction,
            "maps and topology",
            ["map_templates", "map_address_points", "map_links"],
            """
            EXISTS (
                SELECT 1 FROM gameplay_content_publication
                WHERE family = 'gameplay'
            )
            """,
            MapsResource,
            cancellationToken);
        var monstersCreated = await EnsureFamilyAsync(
            connection,
            transaction,
            "monster templates",
            ["monster_templates"],
            """
            EXISTS (
                SELECT 1 FROM gameplay_content_publication
                WHERE family = 'gameplay'
            )
            """,
            MonstersResource,
            cancellationToken);

        var gameplayPolicyApplied =
            !await HasGameplayPublicationAsync(
                connection,
                transaction,
                cancellationToken);
        if (gameplayPolicyApplied)
        {
            await PostgresSkillTimingBaselinePublisher.ApplyAsync(
                connection,
                transaction,
                cancellationToken);
            await ApplyGameplayPolicyAsync(
                connection,
                transaction,
                cancellationToken);
        }

        await ValidateRequiredContentAsync(
            connection,
            transaction,
            cancellationToken);
        return new RelationalContentBootstrapResult(
            itemAttributesCreated,
            skillsCreated,
            npcsCreated,
            mapsCreated,
            monstersCreated,
            gameplayPolicyApplied);
    }

    private static async Task AcquireLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@namespace, @key);",
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "namespace",
            NpgsqlDbType.Integer,
            BootstrapLockNamespace);
        command.Parameters.AddWithValue(
            "key",
            NpgsqlDbType.Integer,
            BootstrapLockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> EnsureFamilyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string family,
        IReadOnlyList<string> tableNames,
        string publishedSql,
        BaselineResource resource,
        CancellationToken cancellationToken)
    {
        if (tableNames.Count == 0 ||
            tableNames.Any(static name =>
                name.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) &&
                    character != '_')))
        {
            throw new InvalidOperationException(
                "A relational baseline family has an invalid table list.");
        }

        var presentExpression = string.Join(
            " + ",
            tableNames.Select(static table =>
                $"CASE WHEN EXISTS (SELECT 1 FROM {table}) THEN 1 ELSE 0 END"));
        await using (var state = new NpgsqlCommand(
                         $"""
                         SELECT ({presentExpression})::integer,
                                ({publishedSql});
                         """,
                         connection,
                         transaction))
        {
            await using var reader =
                await state.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidDataException(
                    $"Could not inspect the {family} baseline family.");
            }

            var presentCount = reader.GetInt32(0);
            var published = reader.GetBoolean(1);
            if (published || presentCount == tableNames.Count)
            {
                return false;
            }
            if (presentCount != 0)
            {
                throw new InvalidDataException(
                    $"The {family} relational baseline is partial " +
                    $"({presentCount}/{tableNames.Count} tables contain rows). " +
                    "Automatic repair is forbidden after partial loss.");
            }
        }

        var sql = LoadReviewedSql(resource);
        await using var command = new NpgsqlCommand(
            sql,
            connection,
            transaction)
        {
            CommandTimeout = 120
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    private static string LoadReviewedSql(BaselineResource resource)
    {
        var assembly = typeof(PostgresRelationalContentBaselineBootstrapper)
            .Assembly;
        using var stream = assembly.GetManifestResourceStream(resource.Name) ??
            throw new InvalidDataException(
                $"Reviewed database baseline '{resource.Name}' is missing.");
        if (stream.Length is <= 0 or > MaximumResourceBytes)
        {
            throw new InvalidDataException(
                $"Reviewed database baseline '{resource.Name}' has an " +
                "invalid size.");
        }

        using var memory = new MemoryStream(checked((int)stream.Length));
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(
                actualHash,
                resource.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Reviewed database baseline '{resource.Name}' failed its " +
                "SHA-256 check.");
        }

        return new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true).GetString(bytes)
            .TrimStart('\uFEFF');
    }

    internal static string LoadReviewedItemAttributeSeedSql()
    {
        var sql = LoadReviewedSql(ItemAttributesResource);
        var viewBoundary = sql.IndexOf(
            "CREATE OR REPLACE VIEW",
            StringComparison.Ordinal);
        if (viewBoundary <= 0)
        {
            throw new InvalidDataException(
                "The reviewed item-attribute baseline has no view boundary.");
        }

        return sql[..viewBoundary];
    }

    private static async Task<bool> HasGameplayPublicationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM gameplay_content_publication
                WHERE family = 'gameplay'
            );
            """,
            connection,
            transaction);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task ValidateRequiredContentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1 FROM item_template_content_publication
                    WHERE family = 'items'
                ) OR EXISTS (SELECT 1 FROM item_attribute_templates),
                EXISTS (
                    SELECT 1 FROM gameplay_content_publication
                    WHERE family = 'gameplay'
                ) OR (
                    EXISTS (SELECT 1 FROM class_templates) AND
                    EXISTS (SELECT 1 FROM talent_effect_templates) AND
                    EXISTS (SELECT 1 FROM talent_templates) AND
                    EXISTS (SELECT 1 FROM skill_templates) AND
                    EXISTS (SELECT 1 FROM skill_book_templates) AND
                    EXISTS (SELECT 1 FROM map_templates) AND
                    EXISTS (SELECT 1 FROM map_address_points) AND
                    EXISTS (SELECT 1 FROM map_links) AND
                    EXISTS (SELECT 1 FROM monster_templates) AND
                    EXISTS (SELECT 1 FROM world_boss_areas WHERE enabled) AND
                    EXISTS (SELECT 1 FROM pending_world_boss_areas)
                ),
                (
                    EXISTS (
                        SELECT 1 FROM npc_content_publication
                        WHERE family = 'npcs'
                    ) AND EXISTS (
                        SELECT 1 FROM npc_dialogue_publication
                        WHERE family = 'npc-dialogues'
                    )
                ) OR (
                    EXISTS (SELECT 1 FROM npc_text_templates) AND
                    EXISTS (SELECT 1 FROM npc_appearance_templates) AND
                    EXISTS (SELECT 1 FROM npc_spawn_references) AND
                    EXISTS (SELECT 1 FROM npc_function_templates) AND
                    EXISTS (SELECT 1 FROM npc_dialog_templates)
                );
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            Enumerable.Range(0, 3).Any(index => !reader.GetBoolean(index)))
        {
            throw new InvalidDataException(
                "The reviewed relational content baseline is incomplete.");
        }
    }

    private sealed record BaselineResource(string Name, string Sha256);
}
