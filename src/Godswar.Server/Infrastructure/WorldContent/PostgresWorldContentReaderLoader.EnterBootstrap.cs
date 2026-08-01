using System.Buffers.Binary;
using Godswar.Server.Application.World;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static partial class PostgresWorldContentReaderLoader
{
    private const int MaximumEnterBootstrapPackets = 256;
    private const int MaximumEnterBootstrapBytes = 262_144;

    private static async Task<byte[][]>
        LoadPublishedEnterBootstrapPacketsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        string revision;
        int expectedPacketCount;
        int expectedTotalBytes;
        await using (var headerCommand = new NpgsqlCommand(
                         """
                         SELECT publication.revision,
                                release.packet_count,
                                release.total_bytes
                         FROM enter_bootstrap_publication publication
                         JOIN enter_bootstrap_revisions release
                           ON release.revision = publication.revision
                         WHERE publication.family = 'enter-bootstrap';
                         """,
                         connection,
                         transaction))
        await using (var header =
                     await headerCommand.ExecuteReaderAsync(
                         cancellationToken))
        {
            if (!await header.ReadAsync(cancellationToken))
            {
                throw new WorldContentUnavailableException(
                    "enter-bootstrap",
                    WorldContentFailureReason.Missing,
                    "No official enter-bootstrap revision is published.");
            }

            revision = header.GetString(0);
            expectedPacketCount = header.GetInt32(1);
            expectedTotalBytes = header.GetInt32(2);
            if (expectedPacketCount is < 0 or
                    > MaximumEnterBootstrapPackets ||
                expectedTotalBytes is < 0 or
                    > MaximumEnterBootstrapBytes)
            {
                throw new WorldContentUnavailableException(
                    "enter-bootstrap",
                    WorldContentFailureReason.Invalid,
                    "The official enter-bootstrap bounds are invalid.");
            }

            if (await header.ReadAsync(cancellationToken))
            {
                throw new WorldContentUnavailableException(
                    "enter-bootstrap",
                    WorldContentFailureReason.Invalid,
                    "More than one official enter-bootstrap revision is " +
                    "published.");
            }
        }

        var packets = new List<byte[]>(expectedPacketCount);
        var totalBytes = 0;
        try
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT sequence, opcode, clear_bytes
                FROM enter_bootstrap_packets
                WHERE revision = @revision
                ORDER BY sequence;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("revision", revision);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var sequence = reader.GetInt16(0);
                var opcode = reader.GetInt32(1);
                var packet = (byte[])reader["clear_bytes"];
                if (sequence != packets.Count ||
                    packet.Length < 4 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(packet) !=
                        packet.Length ||
                    BinaryPrimitives.ReadUInt16LittleEndian(
                        packet.AsSpan(2, 2)) != opcode)
                {
                    throw new InvalidDataException(
                        "An enter-bootstrap packet is malformed or out of sequence.");
                }

                totalBytes = checked(totalBytes + packet.Length);
                if (packets.Count >= MaximumEnterBootstrapPackets ||
                    totalBytes > MaximumEnterBootstrapBytes)
                {
                    throw new InvalidDataException(
                        "The enter-bootstrap packet set exceeds its bounds.");
                }

                packets.Add(packet);
            }
        }
        catch (Exception ex) when (
            ex is InvalidDataException or
                OverflowException or
                InvalidCastException)
        {
            throw new WorldContentUnavailableException(
                "enter-bootstrap",
                WorldContentFailureReason.Invalid,
                "The published enter-bootstrap content is malformed.",
                ex);
        }

        var canonical = packets.ToArray();
        var computed =
            WorldContentRevisionHasher.HashEnterBootstrap(canonical);
        if (computed.EntryCount != expectedPacketCount ||
            totalBytes != expectedTotalBytes ||
            !string.Equals(
                computed.Sha256,
                revision,
                StringComparison.Ordinal))
        {
            throw new WorldContentUnavailableException(
                "enter-bootstrap",
                WorldContentFailureReason.RevisionMismatch,
                "The published enter-bootstrap content does not match its " +
                "declared revision and bounds.");
        }

        return canonical;
    }
}
