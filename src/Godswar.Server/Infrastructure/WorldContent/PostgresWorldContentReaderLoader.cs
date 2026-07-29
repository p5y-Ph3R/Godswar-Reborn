using System.Data;
using System.Diagnostics;
using Godswar.Server.Application.World;
using Godswar.Server.State;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static class PostgresWorldContentReaderLoader
{
    private const string PostgresWorldContentSource =
        "postgres-published-v1";

    public static async Task<IWorldContentReader> LoadAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await using var dataSource =
                NpgsqlDataSource.Create(connectionString);
            await using var connection =
                await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction =
                await connection.BeginTransactionAsync(
                    IsolationLevel.RepeatableRead,
                    cancellationToken);
            await using (var readOnly = new NpgsqlCommand(
                             "SET TRANSACTION READ ONLY;",
                             connection,
                             transaction))
            {
                await readOnly.ExecuteNonQueryAsync(cancellationToken);
            }

            var mapIds = await LoadPublishedMapIdsAsync(
                connection,
                transaction,
                cancellationToken);
            var capturedNpcs = await LoadCapturedNpcSpawnsAsync(
                connection,
                transaction,
                cancellationToken);
            var npcReferences = await LoadNormalizedNpcSpawnReferencesAsync(
                connection,
                transaction,
                cancellationToken);
            var monsters = await LoadCapturedMonsterSpawnsAsync(
                connection,
                transaction,
                cancellationToken);
            var enterBootstrap = await LoadEnterBootstrapPacketsAsync(
                connection,
                transaction,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            var npcDefinitions = BuildNpcDefinitions(
                mapIds,
                capturedNpcs,
                npcReferences);
            var reader = PinnedWorldContentReader.Create(
                PostgresWorldContentSource,
                mapIds,
                npcDefinitions,
                monsters,
                enterBootstrap);
            stopwatch.Stop();
            WorldContentMetrics.RecordLoad(
                PostgresWorldContentSource,
                "success",
                stopwatch.Elapsed);
            return reader;
        }
        catch (WorldContentUnavailableException ex)
        {
            stopwatch.Stop();
            WorldContentMetrics.RecordRejection(ex.Family, ex.Reason);
            WorldContentMetrics.RecordLoad(
                PostgresWorldContentSource,
                "rejected",
                stopwatch.Elapsed);
            throw;
        }
        catch
        {
            stopwatch.Stop();
            WorldContentMetrics.RecordLoad(
                PostgresWorldContentSource,
                "error",
                stopwatch.Elapsed);
            throw;
        }
    }

    private static async Task<short[]> LoadPublishedMapIdsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var mapIds = new List<short>();
        await using var command = new NpgsqlCommand(
            """
            SELECT map_id
            FROM map_templates
            ORDER BY map_id;
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            mapIds.Add(reader.GetInt16(0));
        }

        return mapIds.ToArray();
    }

    private static async Task<CapturedNpcSpawn[]>
        LoadCapturedNpcSpawnsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var spawns = new List<CapturedNpcSpawn>();
        await using var command = new NpgsqlCommand(
            """
            SELECT map_id, scene_key, npc_key, template_key, object_id,
                   pos_x, pos_z, clear_bytes, detail_10077, detail_10080
            FROM npc_spawn_packets
            ORDER BY map_id, npc_key, template_key, object_id;
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            spawns.Add(new CapturedNpcSpawn(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                checked((uint)reader.GetInt64(4)),
                reader.GetFloat(5),
                reader.GetFloat(6),
                (byte[])reader["clear_bytes"],
                (byte[])reader["detail_10077"],
                (byte[])reader["detail_10080"]));
        }

        return spawns.ToArray();
    }

    private static async Task<NpcSpawnReferenceDefinition[]>
        LoadNormalizedNpcSpawnReferencesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var definitions = new List<NpcSpawnReferenceDefinition>();
        await using var command = new NpgsqlCommand(
            """
            WITH position_counts AS (
                SELECT ns.map_id,
                       ns.npc_key,
                       ns.pos_x,
                       ns.pos_z,
                       COUNT(*) AS reference_count
                FROM npc_spawn_references ns
                WHERE ns.npc_key <> ''
                GROUP BY ns.map_id, ns.npc_key, ns.pos_x, ns.pos_z
            ),
            ranked_positions AS (
                SELECT pc.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY pc.map_id, pc.npc_key
                           ORDER BY pc.reference_count DESC,
                                    pc.pos_x,
                                    pc.pos_z
                       ) AS position_rank
                FROM position_counts pc
            ),
            ranked_appearances AS (
                SELECT na.npc_key,
                       na.template_key,
                       na.scene_key,
                       ROW_NUMBER() OVER (
                           PARTITION BY na.npc_key
                           ORDER BY CASE
                                        WHEN na.scene_key <> ''
                                         AND na.scene_key = nt.scene_key THEN 0
                                        WHEN na.scene_key <> '' THEN 1
                                        ELSE 2
                                    END,
                                    LENGTH(na.template_key),
                                    na.template_key
                       ) AS appearance_rank
                FROM npc_appearance_templates na
                LEFT JOIN npc_text_templates nt
                  ON nt.npc_key = na.npc_key
                WHERE na.npc_key <> ''
                  AND na.template_key <> ''
            )
            SELECT rp.map_id,
                   COALESCE(
                       NULLIF(ra.scene_key, ''),
                       NULLIF(nt.scene_key, ''),
                       NULLIF(mt.scene_key, ''),
                       SPLIT_PART(rp.npc_key, '_', 1)
                   ) AS scene_key,
                   rp.npc_key,
                   ra.template_key,
                   rp.pos_x,
                   rp.pos_z
            FROM ranked_positions rp
            JOIN ranked_appearances ra
              ON ra.npc_key = rp.npc_key
             AND ra.appearance_rank = 1
            LEFT JOIN npc_text_templates nt
              ON nt.npc_key = rp.npc_key
            LEFT JOIN map_templates mt
              ON mt.map_id = rp.map_id
            WHERE rp.position_rank = 1
            ORDER BY rp.map_id, rp.npc_key, ra.template_key;
            """,
            connection,
            transaction);

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var definition = new NpcSpawnReferenceDefinition(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFloat(4),
                reader.GetFloat(5));
            if (float.IsFinite(definition.X) &&
                float.IsFinite(definition.Z))
            {
                definitions.Add(definition);
            }
        }

        return definitions.ToArray();
    }

    private static async Task<CapturedMonsterSpawn[]>
        LoadCapturedMonsterSpawnsAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CancellationToken cancellationToken)
    {
        var spawns = new List<CapturedMonsterSpawn>();
        await using var command = new NpgsqlCommand(
            """
            SELECT map_id, scene_key, template_key, display_name, object_id,
                   pos_x, pos_z, clear_bytes
            FROM monster_spawn_packets
            WHERE object_id BETWEEN 1 AND 4294967295
            ORDER BY map_id, object_id;
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            spawns.Add(new CapturedMonsterSpawn(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                checked((uint)reader.GetInt64(4)),
                reader.GetFloat(5),
                reader.GetFloat(6),
                (byte[])reader["clear_bytes"]));
        }

        return spawns.ToArray();
    }

    private static async Task<byte[][]> LoadEnterBootstrapPacketsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var packets = new List<byte[]>();
        await using var command = new NpgsqlCommand(
            """
            SELECT clear_bytes
            FROM server_packet_templates
            WHERE template_key = 'enter_syn_game_data'
              AND direction = 'S2C'
              AND opcode = 10090
            ORDER BY sequence;
            """,
            connection,
            transaction);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            packets.Add((byte[])reader["clear_bytes"]);
        }

        // Empty is an explicitly published safe bootstrap. In particular, do
        // not read capture-history tables: research data is not authority.
        return packets.ToArray();
    }

    private static NpcSpawnDefinition[] BuildNpcDefinitions(
        IReadOnlyList<short> mapIds,
        IReadOnlyList<CapturedNpcSpawn> capturedNpcs,
        IReadOnlyList<NpcSpawnReferenceDefinition> references)
    {
        var capturedByMap = capturedNpcs
            .GroupBy(static spawn => spawn.MapId)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<CapturedNpcSpawn>)
                    group.ToArray());
        var referencesByMap = references
            .GroupBy(static definition => definition.MapId)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<NpcSpawnReferenceDefinition>)
                    group.ToArray());
        capturedByMap.TryGetValue(0, out var spartaAppearances);
        spartaAppearances ??= [];

        return mapIds
            .SelectMany(mapId =>
            {
                capturedByMap.TryGetValue(mapId, out var captured);
                referencesByMap.TryGetValue(mapId, out var mapReferences);
                return NpcSpawnDefinitionFactory.Create(
                    mapId,
                    captured ?? [],
                    mapId == 1 ? spartaAppearances : [],
                    mapReferences ?? []);
            })
            .ToArray();
    }
}
