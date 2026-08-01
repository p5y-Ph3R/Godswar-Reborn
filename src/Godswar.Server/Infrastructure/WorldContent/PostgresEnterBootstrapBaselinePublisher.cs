using System.Data;
using Godswar.Server.Application.World;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.Infrastructure.WorldContent;

internal sealed record EnterBootstrapPublicationResult(
    string Revision,
    int PacketCount,
    int TotalBytes,
    string Source,
    bool Created);

internal static class PostgresEnterBootstrapBaselinePublisher
{
    private const int PublicationLockNamespace = 1_193_657_936;
    private const int PublicationLockKey = 1_448_298_804;
    private const string Publisher = "server-baseline-v1";
    private const string Source = "explicit-safe-empty-v1";

    public static async Task<EnterBootstrapPublicationResult>
        EnsurePublishedAsync(
            string connectionString,
            CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        await using var dataSource =
            NpgsqlDataSource.Create(connectionString);
        await using var connection =
            await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(
                IsolationLevel.ReadCommitted,
                cancellationToken);

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
            await transaction.CommitAsync(cancellationToken);
            return current with { Created = false };
        }

        var revision = WorldContentRevisionHasher.HashEnterBootstrap([]);
        await using (var release = new NpgsqlCommand(
                         """
                         INSERT INTO enter_bootstrap_revisions (
                             revision,
                             packet_count,
                             total_bytes,
                             source
                         )
                         VALUES (@revision, 0, 0, @source)
                         ON CONFLICT (revision) DO NOTHING;
                         """,
                         connection,
                         transaction))
        {
            release.Parameters.AddWithValue(
                "revision",
                NpgsqlDbType.Varchar,
                revision.Sha256);
            release.Parameters.AddWithValue(
                "source",
                NpgsqlDbType.Varchar,
                Source);
            await release.ExecuteNonQueryAsync(cancellationToken);
        }

        await VerifyStoredReleaseAsync(
            connection,
            transaction,
            revision,
            cancellationToken);
        await using (var publish = new NpgsqlCommand(
                         """
                         INSERT INTO enter_bootstrap_publication (
                             family,
                             revision,
                             published_at,
                             publisher
                         )
                         VALUES (
                             'enter-bootstrap',
                             @revision,
                             now(),
                             @publisher
                         );
                         """,
                         connection,
                         transaction))
        {
            publish.Parameters.AddWithValue(
                "revision",
                NpgsqlDbType.Varchar,
                revision.Sha256);
            publish.Parameters.AddWithValue(
                "publisher",
                NpgsqlDbType.Varchar,
                Publisher);
            if (await publish.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidDataException(
                    "The enter-bootstrap publication pointer was not created.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new EnterBootstrapPublicationResult(
            revision.Sha256,
            PacketCount: 0,
            TotalBytes: 0,
            Source,
            Created: true);
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

    private static async Task<EnterBootstrapPublicationResult?>
        ReadCurrentPublicationAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT publication.revision,
                   release.packet_count,
                   release.total_bytes,
                   release.source
            FROM enter_bootstrap_publication publication
            JOIN enter_bootstrap_revisions release
              ON release.revision = publication.revision
            WHERE publication.family = 'enter-bootstrap';
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new EnterBootstrapPublicationResult(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetString(3),
            Created: false);
    }

    private static async Task VerifyStoredReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        WorldContentFamilyRevision expected,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT packet_count, total_bytes, source,
                   (
                       SELECT COUNT(*)::integer
                       FROM enter_bootstrap_packets packets
                       WHERE packets.revision = release.revision
                   )
            FROM enter_bootstrap_revisions release
            WHERE revision = @revision;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "revision",
            NpgsqlDbType.Varchar,
            expected.Sha256);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetInt32(0) != 0 ||
            reader.GetInt32(1) != 0 ||
            !string.Equals(reader.GetString(2), Source, StringComparison.Ordinal) ||
            reader.GetInt32(3) != 0)
        {
            throw new InvalidDataException(
                "The stored enter-bootstrap baseline release failed verification.");
        }
    }
}
