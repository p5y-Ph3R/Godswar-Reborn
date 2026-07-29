using Godswar.Server.Application.World;
using Godswar.Server.Domain.World.Content;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class
    PostgresNpcContentPublicationIntegrationChecks
{
    private static async Task AssertUnpublishedDatabaseAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT COUNT(*) FROM npc_content_revisions),
                (SELECT COUNT(*) FROM npc_spawn_definitions),
                (SELECT COUNT(*) FROM npc_content_publication);
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "unpublished NPC content shape returns one row");
        Check.Equal(
            0L,
            reader.GetInt64(0),
            "disposable gate starts without an NPC release");
        Check.Equal(
            0L,
            reader.GetInt64(1),
            "disposable gate starts without official NPC definitions");
        Check.Equal(
            0L,
            reader.GetInt64(2),
            "disposable gate starts without an NPC publication");
    }

    private static async Task AssertSingletonPublicationAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT publication.revision,
                   release.entry_count,
                   release.source,
                   (SELECT COUNT(*) FROM npc_content_revisions),
                   (SELECT COUNT(*) FROM npc_spawn_definitions),
                   (SELECT COUNT(*) FROM npc_content_publication)
            FROM npc_content_publication publication
            JOIN npc_content_revisions release
              ON release.revision = publication.revision
            WHERE publication.family = 'npcs';
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "official NPC publication metadata exists");
        Check.Equal(
            ExpectedRevision,
            reader.GetString(0),
            "official NPC publication revision");
        Check.Equal(
            ExpectedEntryCount,
            reader.GetInt32(1),
            "official NPC release entry count");
        Check.Equal(
            ExpectedSource,
            reader.GetString(2),
            "official NPC release source");
        Check.Equal(
            1L,
            reader.GetInt64(3),
            "idempotent publisher leaves one NPC release");
        Check.Equal(
            (long)ExpectedEntryCount,
            reader.GetInt64(4),
            "idempotent publisher leaves exactly the reviewed definitions");
        Check.Equal(
            1L,
            reader.GetInt64(5),
            "idempotent publisher leaves one publication pointer");
        Check.True(
            !await reader.ReadAsync(),
            "official NPC publication metadata is singular");
    }

    private static async Task<NpcSpawnDefinition[]>
        ReadPublishedDefinitionsAsync(
            NpgsqlDataSource dataSource)
    {
        var definitions = new List<NpcSpawnDefinition>(
            ExpectedEntryCount);
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT definition.map_id,
                   definition.scene_key,
                   definition.npc_key,
                   definition.template_key,
                   definition.object_id,
                   definition.pos_x,
                   definition.pos_z,
                   definition.interaction_id,
                   definition.appearance_type,
                   definition.facing,
                   definition.detail_10077,
                   definition.detail_10080
            FROM npc_content_publication publication
            JOIN npc_spawn_definitions definition
              ON definition.revision = publication.revision
            WHERE publication.family = 'npcs'
            ORDER BY definition.map_id,
                     definition.npc_key COLLATE "C",
                     definition.template_key COLLATE "C",
                     definition.object_id;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            definitions.Add(new NpcSpawnDefinition(
                reader.GetInt16(0),
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

        Check.Equal(
            ExpectedEntryCount,
            definitions.Count,
            "every official NPC definition is read from PostgreSQL");
        return definitions.ToArray();
    }

    private static async Task<NpcSpawnDefinition[]>
        ReadAllNpcDefinitionsAsync(
            NpgsqlDataSource dataSource,
            IWorldContentReader worldContent)
    {
        var mapIds = new List<short>();
        await using (var connection =
                     await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
                         """
                         SELECT map_id
                         FROM map_templates
                         ORDER BY map_id;
                         """,
                         connection))
        await using (var reader =
                     await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                mapIds.Add(reader.GetInt16(0));
            }
        }

        var definitions = new List<NpcSpawnDefinition>(
            worldContent.Manifest.Npcs.EntryCount);
        foreach (var mapId in mapIds)
        {
            var map = await worldContent.ReadMapAsync(mapId);
            definitions.AddRange(map.Npcs);
        }

        Check.Equal(
            worldContent.Manifest.Npcs.EntryCount,
            definitions.Count,
            "reader exposes every official NPC definition");
        return Canonicalize(definitions);
    }

    private static async Task InsertLegacyNpcSourceFixturesAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection =
            await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();
        await AssertLegacyFixtureKeysAreFreeAsync(
            connection,
            transaction);

        await using (var packet = new NpgsqlCommand(
                         """
                         INSERT INTO npc_spawn_packets (
                             map_id,
                             scene_key,
                             npc_key,
                             template_key,
                             object_id,
                             pos_x,
                             pos_z,
                             clear_bytes,
                             source
                         )
                         VALUES (
                             @map_id,
                             'B05B_Legacy',
                             @npc_key,
                             @template_key,
                             4294905006,
                             12.5,
                             -8.25,
                             '\x0400'::bytea,
                             @source
                         );
                         """,
                         connection,
                         transaction))
        {
            AddLegacyFixtureParameters(packet);
            Check.Equal(
                1,
                await packet.ExecuteNonQueryAsync(),
                "one legacy NPC packet decoy is inserted");
        }

        await using (var reference = new NpgsqlCommand(
                         """
                         INSERT INTO npc_spawn_references (
                             quest_id,
                             role,
                             npc_key,
                             map_id,
                             pos_x,
                             pos_z,
                             source
                         )
                         VALUES (
                             @quest_id,
                             'b05b',
                             @npc_key,
                             @map_id,
                             12.5,
                             -8.25,
                             @source
                         );
                         """,
                         connection,
                         transaction))
        {
            AddLegacyFixtureParameters(reference);
            Check.Equal(
                1,
                await reference.ExecuteNonQueryAsync(),
                "one legacy NPC reference decoy is inserted");
        }

        await transaction.CommitAsync();
    }

    private static async Task AssertLegacyFixtureKeysAreFreeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1 FROM npc_spawn_packets
                    WHERE map_id = @map_id
                      AND template_key = @template_key
                ),
                EXISTS (
                    SELECT 1 FROM npc_spawn_references
                    WHERE quest_id = @quest_id
                      AND role = 'b05b'
                      AND npc_key = @npc_key
                );
            """,
            connection,
            transaction);
        AddLegacyFixtureParameters(command);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "legacy NPC fixture collision query returns one row");
        Check.True(
            !reader.GetBoolean(0) && !reader.GetBoolean(1),
            "legacy NPC fixture keys are unused");
    }

    private static void AddLegacyFixtureParameters(
        NpgsqlCommand command)
    {
        command.Parameters.AddWithValue(
            "map_id",
            NpgsqlDbType.Smallint,
            LegacyMapId);
        command.Parameters.AddWithValue(
            "quest_id",
            NpgsqlDbType.Integer,
            LegacyQuestId);
        command.Parameters.AddWithValue(
            "npc_key",
            NpgsqlDbType.Varchar,
            LegacyNpcKey);
        command.Parameters.AddWithValue(
            "template_key",
            NpgsqlDbType.Varchar,
            LegacyTemplateKey);
        command.Parameters.AddWithValue(
            "source",
            NpgsqlDbType.Varchar,
            LegacySource);
    }
}
