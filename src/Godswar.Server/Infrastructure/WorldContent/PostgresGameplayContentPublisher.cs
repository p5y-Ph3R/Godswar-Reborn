using System.Data;
using Godswar.Server.Application.World;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldContent;

internal sealed record GameplayContentPublicationResult(
    string Revision,
    int EntryCount,
    string Source,
    bool Created);

/// <summary>
/// One-time promotion boundary from the reviewed legacy relational catalog to
/// immutable gameplay revision rows. Runtime readers never consult the source
/// tables; later content changes must publish a new revision.
/// </summary>
internal static partial class PostgresGameplayContentPublisher
{
    private const int PublicationLockNamespace = 1_193_657_936;
    private const int PublicationLockKey = 1_731_245_911;
    private const string Publisher = "server-database-promotion-v1";
    private const string Source = "reviewed-database-promotion-v1";

    public static async Task<GameplayContentPublicationResult>
        EnsurePublishedAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead,
                cancellationToken);

        var result = await EnsurePublishedAsync(
            connection,
            transaction,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal static async Task<GameplayContentPublicationResult>
        EnsurePublishedAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        await AcquirePublicationLockAsync(
            connection,
            transaction,
            cancellationToken);
        var current = await ReadCurrentPublicationAsync(
            connection,
            transaction,
            cancellationToken);
        if (current is not null)
        {
            _ = await PostgresWorldContentReaderLoader
                .LoadPublishedGameplayContentAsync(
                    connection,
                    transaction,
                    cancellationToken);
            return current with { Created = false };
        }

        var canonical = await ReadCanonicalSourceContentAsync(
            connection,
            transaction,
            cancellationToken);
        var revision = WorldContentRevisionHasher.HashGameplay(canonical);

        if (await InsertReleaseAsync(
                connection,
                transaction,
                revision,
                canonical,
                cancellationToken))
        {
            await CopyDefinitionsAsync(
                connection,
                transaction,
                revision.Sha256,
                canonical,
                cancellationToken);
        }

        _ = await PostgresWorldContentReaderLoader.LoadGameplayRevisionAsync(
            connection,
            transaction,
            revision.Sha256,
            canonical,
            cancellationToken);

        await PublishAsync(
            connection,
            transaction,
            revision.Sha256,
            cancellationToken);
        return new GameplayContentPublicationResult(
            revision.Sha256,
            revision.EntryCount,
            Source,
            Created: true);
    }

    /// <summary>
    /// Reads and canonicalizes the relational authoring snapshot used by the
    /// publisher. The internal seam lets the PostgreSQL poison-release test
    /// target the exact source revision instead of a generated test fixture.
    /// </summary>
    internal static async Task<GameplayContentCatalog>
        ReadCanonicalSourceContentAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken = default)
    {
        var sourceContent = await ReadSourceContentAsync(
            connection,
            transaction,
            cancellationToken);
        return PinnedWorldContentReader.Create(
            "gameplay-publication-validation-v1",
            sourceContent.Maps.Select(static value => value.MapId),
            [],
            [],
            [],
            gameplay: sourceContent).Gameplay;
    }

    private static async Task AcquirePublicationLockAsync(
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
            PublicationLockNamespace);
        command.Parameters.AddWithValue(
            "key",
            NpgsqlDbType.Integer,
            PublicationLockKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<GameplayContentPublicationResult?>
        ReadCurrentPublicationAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT publication.revision,
                   release.map_count + release.address_point_count +
                       release.link_count + release.monster_template_count +
                       release.world_boss_count +
                       release.pending_world_boss_count +
                       release.class_count + release.talent_effect_count +
                       release.talent_count + release.skill_count +
                       release.skill_book_count,
                   release.source
            FROM gameplay_content_publication publication
            JOIN gameplay_content_revisions release
              ON release.revision = publication.revision
            WHERE publication.family = 'gameplay';
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new GameplayContentPublicationResult(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetString(2),
            Created: false);
    }

    internal static async Task<bool> InsertReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldContentFamilyRevision revision,
        GameplayContentCatalog content,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO gameplay_content_revisions (
                revision,
                map_count,
                address_point_count,
                link_count,
                monster_template_count,
                world_boss_count,
                pending_world_boss_count,
                class_count,
                talent_effect_count,
                talent_count,
                skill_count,
                skill_book_count,
                source
            )
            VALUES (
                @revision,
                @map_count,
                @address_point_count,
                @link_count,
                @monster_template_count,
                @world_boss_count,
                @pending_world_boss_count,
                @class_count,
                @talent_effect_count,
                @talent_count,
                @skill_count,
                @skill_book_count,
                @source
            )
            ON CONFLICT (revision) DO NOTHING;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            revision.Sha256);
        command.Parameters.AddWithValue(
            "map_count",
            NpgsqlDbType.Integer,
            content.Maps.Count);
        command.Parameters.AddWithValue(
            "address_point_count",
            NpgsqlDbType.Integer,
            content.AddressPoints.Count);
        command.Parameters.AddWithValue(
            "link_count",
            NpgsqlDbType.Integer,
            content.Links.Count);
        command.Parameters.AddWithValue(
            "monster_template_count",
            NpgsqlDbType.Integer,
            content.MonsterTemplates.Count);
        command.Parameters.AddWithValue(
            "world_boss_count",
            NpgsqlDbType.Integer,
            content.WorldBosses.Count);
        command.Parameters.AddWithValue(
            "pending_world_boss_count",
            NpgsqlDbType.Integer,
            content.PendingWorldBossAreas.Count);
        command.Parameters.AddWithValue(
            "class_count",
            NpgsqlDbType.Integer,
            content.Classes.Count);
        command.Parameters.AddWithValue(
            "talent_effect_count",
            NpgsqlDbType.Integer,
            content.TalentEffects.Count);
        command.Parameters.AddWithValue(
            "talent_count",
            NpgsqlDbType.Integer,
            content.Talents.Count);
        command.Parameters.AddWithValue(
            "skill_count",
            NpgsqlDbType.Integer,
            content.SkillCombatDefinitions.Count);
        command.Parameters.AddWithValue(
            "skill_book_count",
            NpgsqlDbType.Integer,
            content.SkillBooks.Count);
        command.Parameters.AddWithValue(
            "source",
            NpgsqlDbType.Varchar,
            Source);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task PublishAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO gameplay_content_publication (
                family,
                revision,
                published_at,
                publisher
            )
            VALUES ('gameplay', @revision, now(), @publisher);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            revision);
        command.Parameters.AddWithValue(
            "publisher",
            NpgsqlDbType.Varchar,
            Publisher);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidDataException(
                "The gameplay publication pointer was not created.");
        }
    }
}
