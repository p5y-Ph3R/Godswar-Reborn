using Godswar.Server.Application.World;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static partial class PostgresWorldContentReaderLoader
{
    private static async Task<NpcSpawnDefinition[]>
        LoadPublishedNpcDefinitionsAsync(
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
                         FROM npc_content_publication publication
                         JOIN npc_content_revisions release
                           ON release.revision = publication.revision
                         WHERE publication.family = 'npcs';
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
                    "npcs",
                    WorldContentFailureReason.Missing,
                    "No official NPC content revision is published.");
            }

            revision = header.GetString(0);
            expectedEntryCount = header.GetInt32(1);
            if (expectedEntryCount is < 0 or
                > NpcContentLimits.MaximumDefinitions)
            {
                throw new WorldContentUnavailableException(
                    "npcs",
                    WorldContentFailureReason.Invalid,
                    "The official NPC content entry count is outside " +
                    "the supported bounds.");
            }

            if (await header.ReadAsync(cancellationToken))
            {
                throw new WorldContentUnavailableException(
                    "npcs",
                    WorldContentFailureReason.Invalid,
                    "More than one official NPC content revision is " +
                    "published.");
            }
        }

        var definitions = new List<NpcSpawnDefinition>(
            expectedEntryCount);
        try
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT map_id,
                       scene_key,
                       npc_key,
                       template_key,
                       object_id,
                       pos_x,
                       pos_z,
                       interaction_id,
                       appearance_type,
                       facing,
                       detail_10077,
                       detail_10080
                FROM npc_spawn_definitions
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
                        $"NPC definition references unpublished map {mapId}.");
                }

                definitions.Add(new NpcSpawnDefinition(
                    mapId,
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    checked((uint)reader.GetInt64(4)),
                    reader.GetFloat(5),
                    reader.GetFloat(6),
                    checked((uint)reader.GetInt64(7)),
                    checked((uint)reader.GetInt64(8)),
                    reader.GetFloat(9),
                    (byte[])reader["detail_10077"],
                    (byte[])reader["detail_10080"]));
            }
        }
        catch (Exception ex) when (
            ex is InvalidDataException or
                OverflowException or
                InvalidCastException)
        {
            throw new WorldContentUnavailableException(
                "npcs",
                WorldContentFailureReason.Invalid,
                "The published NPC content contains a malformed " +
                "definition.",
                ex);
        }

        var canonical = definitions
            .OrderBy(static definition => definition.MapId)
            .ThenBy(
                static definition => definition.NpcKey,
                StringComparer.Ordinal)
            .ThenBy(
                static definition => definition.TemplateKey,
                StringComparer.Ordinal)
            .ThenBy(static definition => definition.ObjectId)
            .ToArray();
        var computed = WorldContentRevisionHasher.HashNpcs(canonical);
        if (computed.EntryCount != expectedEntryCount ||
            !string.Equals(
                computed.Sha256,
                revision,
                StringComparison.Ordinal))
        {
            throw new WorldContentUnavailableException(
                "npcs",
                WorldContentFailureReason.RevisionMismatch,
                "The published NPC content does not match its declared " +
                "revision and entry count.");
        }

        return canonical;
    }
}
