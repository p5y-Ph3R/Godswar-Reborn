using Godswar.Server.Application.World;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static partial class PostgresGameplayContentPublisher
{
    private const string V3UpgradePublisher =
        "server-database-promotion-v2";

    private static async Task<GameplayContentPublicationResult>
        UpgradeLegacyV2PublicationAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            GameplayContentPublicationResult current,
            CancellationToken cancellationToken)
    {
        if (!string.Equals(current.Source, Source, StringComparison.Ordinal) ||
            !string.Equals(
                current.Publisher,
                Publisher,
                StringComparison.Ordinal))
        {
            throw UpgradeUnavailable(
                "The current gameplay publication has an unknown source " +
                "or publisher.");
        }

        await AssertLegacyCombatColumnsAreEmptyAsync(
            connection,
            transaction,
            current.Revision,
            cancellationToken);
        var predecessor = await PostgresWorldContentReaderLoader
            .LoadPublishedGameplayV2ForUpgradeAsync(
                connection,
                transaction,
                cancellationToken);
        var predecessorRevision =
            WorldContentRevisionHasher.HashGameplayV2ForUpgrade(predecessor);
        if (!string.Equals(
                predecessorRevision.Sha256,
                current.Revision,
                StringComparison.Ordinal) ||
            predecessorRevision.EntryCount != current.EntryCount)
        {
            throw UpgradeUnavailable(
                "The current gameplay publication is not an exact v2 " +
                "predecessor.");
        }

        var canonical = await ReadCanonicalSourceContentAsync(
            connection,
            transaction,
            cancellationToken);
        var sourceV2 =
            WorldContentRevisionHasher.HashGameplayV2ForUpgrade(canonical);
        if (!string.Equals(
                sourceV2.Sha256,
                current.Revision,
                StringComparison.Ordinal) ||
            sourceV2.EntryCount != current.EntryCount)
        {
            throw UpgradeUnavailable(
                "The mutable gameplay source drifted from the exact v2 " +
                "publication; refusing to republish it.");
        }

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
        await AdvancePublicationAsync(
            connection,
            transaction,
            current.Revision,
            revision.Sha256,
            cancellationToken);
        return new GameplayContentPublicationResult(
            revision.Sha256,
            revision.EntryCount,
            Source,
            Created: true,
            Publisher: V3UpgradePublisher);
    }

    private static async Task AssertLegacyCombatColumnsAreEmptyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string revision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM gameplay_map_definitions
                    WHERE revision = @revision
                      AND map_mode IS NOT NULL
                ) OR EXISTS (
                    SELECT 1
                    FROM gameplay_monster_templates
                    WHERE revision = @revision
                      AND attack_type IS NOT NULL
                );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            revision);
        if (Convert.ToBoolean(
                await command.ExecuteScalarAsync(cancellationToken)))
        {
            throw UpgradeUnavailable(
                "The current gameplay publication already contains " +
                "unhashed combat-authority values.");
        }
    }

    private static async Task AdvancePublicationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string predecessorRevision,
        string revision,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            UPDATE gameplay_content_publication
            SET revision = @revision,
                published_at = now(),
                publisher = @publisher
            WHERE family = 'gameplay'
              AND revision = @predecessor_revision;
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
            V3UpgradePublisher);
        command.Parameters.AddWithValue(
            "predecessor_revision",
            NpgsqlDbType.Varchar,
            predecessorRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw UpgradeUnavailable(
                "The gameplay publication pointer changed during the v3 " +
                "upgrade.");
        }
    }

    private static WorldContentUnavailableException UpgradeUnavailable(
        string message) =>
        new(
            "gameplay",
            WorldContentFailureReason.RevisionMismatch,
            message);
}
