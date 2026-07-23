using Godswar.Server.Game;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.State;

internal sealed partial class PostgresGameStore
{
    public async Task<IReadOnlyList<CapturedNpcSpawn>> GetCapturedNpcSpawnsAsync(
        short mapId,
        CancellationToken cancellationToken = default)
    {
        var spawns = new List<CapturedNpcSpawn>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT map_id, scene_key, npc_key, template_key, object_id, pos_x, pos_z,
                   clear_bytes, detail_10077, detail_10080
            FROM npc_spawn_packets
            WHERE map_id = @mapId
            ORDER BY npc_key, template_key;
            """, connection);

        command.Parameters.AddWithValue("mapId", mapId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            spawns.Add(new CapturedNpcSpawn(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                unchecked((uint)reader.GetInt64(4)),
                reader.GetFloat(5),
                reader.GetFloat(6),
                (byte[])reader["clear_bytes"],
                (byte[])reader["detail_10077"],
                (byte[])reader["detail_10080"]));
        }

        return spawns;
    }

    public async Task<IReadOnlyList<NpcSpawnDefinition>> GetNpcSpawnDefinitionsAsync(
        short mapId,
        CancellationToken cancellationToken = default)
    {
        var capturedSpawns = await GetCapturedNpcSpawnsAsync(mapId, cancellationToken);
        var capturedAppearanceFallbacks = mapId == 1
            ? await GetCapturedNpcSpawnsAsync(0, cancellationToken)
            : [];
        var referenceDefinitions = await GetNormalizedNpcSpawnReferencesAsync(mapId, cancellationToken);
        return NpcSpawnDefinitionFactory.Create(
            mapId,
            capturedSpawns,
            capturedAppearanceFallbacks,
            referenceDefinitions);
    }

    private async Task<IReadOnlyList<NpcSpawnReferenceDefinition>> GetNormalizedNpcSpawnReferencesAsync(
        short mapId,
        CancellationToken cancellationToken)
    {
        var definitions = new List<NpcSpawnReferenceDefinition>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            WITH position_counts AS (
                SELECT ns.map_id,
                       ns.npc_key,
                       ns.pos_x,
                       ns.pos_z,
                       COUNT(*) AS reference_count
                FROM npc_spawn_references ns
                WHERE ns.map_id = @mapId
                  AND ns.npc_key <> ''
                GROUP BY ns.map_id, ns.npc_key, ns.pos_x, ns.pos_z
            ),
            ranked_positions AS (
                SELECT pc.*,
                       ROW_NUMBER() OVER (
                           PARTITION BY pc.map_id, pc.npc_key
                           ORDER BY pc.reference_count DESC, pc.pos_x, pc.pos_z
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
                                        WHEN na.scene_key <> '' AND na.scene_key = nt.scene_key THEN 0
                                        WHEN na.scene_key <> '' THEN 1
                                        ELSE 2
                                    END,
                                    LENGTH(na.template_key),
                                    na.template_key
                       ) AS appearance_rank
                FROM npc_appearance_templates na
                LEFT JOIN npc_text_templates nt ON nt.npc_key = na.npc_key
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
            LEFT JOIN npc_text_templates nt ON nt.npc_key = rp.npc_key
            LEFT JOIN map_templates mt ON mt.map_id = rp.map_id
            WHERE rp.position_rank = 1
            ORDER BY rp.npc_key, ra.template_key;
            """, connection);
        command.Parameters.AddWithValue("mapId", mapId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var definition = new NpcSpawnReferenceDefinition(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFloat(4),
                reader.GetFloat(5));
            if (float.IsFinite(definition.X) && float.IsFinite(definition.Z))
            {
                definitions.Add(definition);
            }
        }

        return definitions;
    }

    public async Task<IReadOnlyList<CapturedMonsterSpawn>> GetCapturedMonsterSpawnsAsync(
        short mapId,
        CancellationToken cancellationToken = default)
    {
        var spawns = new List<CapturedMonsterSpawn>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT map_id, scene_key, template_key, display_name, object_id, pos_x, pos_z, clear_bytes
            FROM monster_spawn_packets
            WHERE map_id = @mapId
              AND object_id BETWEEN 1 AND 4294967295
            ORDER BY object_id;
            """, connection);

        command.Parameters.AddWithValue("mapId", mapId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
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

        return spawns;
    }

    public async Task<IReadOnlyList<byte[]>> GetEnterSyncPacketsAsync(CancellationToken cancellationToken = default)
    {
        var packets = new List<byte[]>();
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var templateCommand = new NpgsqlCommand("""
            SELECT clear_bytes
            FROM server_packet_templates
            WHERE template_key = 'enter_syn_game_data'
              AND direction = 'S2C'
              AND opcode = 10090
            ORDER BY sequence;
            """, connection);

        {
            await using var reader = await templateCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                packets.Add((byte[])reader["clear_bytes"]);
            }
        }

        if (packets.Count > 0)
        {
            return packets;
        }

        await using var command = new NpgsqlCommand("""
            SELECT clear_bytes
            FROM packet_transactions
            WHERE direction = 'S2C'
              AND opcode = 10090
              AND actual_length = 2048
              AND id BETWEEN 85538 AND 85542
            ORDER BY id;
            """, connection);

        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                packets.Add((byte[])reader["clear_bytes"]);
            }
        }

        if (packets.Count > 0)
        {
            return packets;
        }

        await using var fallbackCommand = new NpgsqlCommand("""
            SELECT clear_bytes
            FROM packet_transactions
            WHERE direction = 'S2C'
              AND opcode = 10090
              AND actual_length = 2048
            ORDER BY id
            LIMIT 5;
            """, connection);

        {
            await using var fallbackReader = await fallbackCommand.ExecuteReaderAsync(cancellationToken);
            while (await fallbackReader.ReadAsync(cancellationToken))
            {
                packets.Add((byte[])fallbackReader["clear_bytes"]);
            }
        }

        return packets;
    }

}
