using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static partial class PostgresWorldContentReaderLoader
{
    private static async Task<CapturedMonsterSpawn[]>
        LoadPublishedMonsterSpawnsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            IReadOnlySet<short> publishedMapIds,
            CancellationToken cancellationToken)
    {
        string revision;
        int expectedEntryCount;
        await using (var headerCommand = new NpgsqlCommand(
                         """
                         SELECT publication.revision,
                                release.entry_count
                         FROM monster_content_publication publication
                         JOIN monster_content_revisions release
                           ON release.revision = publication.revision
                         WHERE publication.family = 'monsters';
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
                    "monsters",
                    WorldContentFailureReason.Missing,
                    "No official monster content revision is published.");
            }

            revision = header.GetString(0);
            expectedEntryCount = header.GetInt32(1);
            if (expectedEntryCount is < 0 or
                > MonsterContentLimits.MaximumDefinitions)
            {
                throw new WorldContentUnavailableException(
                    "monsters",
                    WorldContentFailureReason.Invalid,
                    "The official monster content entry count is outside " +
                    "the supported bounds.");
            }

            if (await header.ReadAsync(cancellationToken))
            {
                throw new WorldContentUnavailableException(
                    "monsters",
                    WorldContentFailureReason.Invalid,
                    "More than one official monster content revision is " +
                    "published.");
            }
        }

        var definitions = new List<CapturedMonsterSpawn>(
            expectedEntryCount);
        try
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT map_id,
                       scene_key,
                       template_key,
                       display_name,
                       object_id,
                       pos_x,
                       pos_z,
                       clear_bytes
                FROM monster_spawn_definitions
                WHERE revision = @revision;
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("revision", revision);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var mapId = reader.GetInt16(0);
                if (!publishedMapIds.Contains(mapId))
                {
                    throw new InvalidDataException(
                        $"Monster definition references unpublished map {mapId}.");
                }

                var definition = new CapturedMonsterSpawn(
                    mapId,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    checked((uint)reader.GetInt64(4)),
                    reader.GetFloat(5),
                    reader.GetFloat(6),
                    (byte[])reader["clear_bytes"]);
                definition.Validate(mapId);
                definitions.Add(definition);
            }
        }
        catch (Exception ex) when (
            ex is InvalidDataException or
                OverflowException or
                InvalidCastException)
        {
            throw new WorldContentUnavailableException(
                "monsters",
                WorldContentFailureReason.Invalid,
                "The published monster content contains a malformed " +
                "definition.",
                ex);
        }

        var canonical = definitions
            .OrderBy(static definition => definition.MapId)
            .ThenBy(static definition => definition.ObjectId)
            .ThenBy(
                static definition => definition.TemplateKey,
                StringComparer.Ordinal)
            .ToArray();
        var computed = WorldContentRevisionHasher.HashMonsters(canonical);
        if (computed.EntryCount != expectedEntryCount ||
            !string.Equals(
                computed.Sha256,
                revision,
                StringComparison.Ordinal))
        {
            throw new WorldContentUnavailableException(
                "monsters",
                WorldContentFailureReason.RevisionMismatch,
                "The published monster content does not match its declared " +
                "revision and entry count.");
        }

        return canonical;
    }
}
