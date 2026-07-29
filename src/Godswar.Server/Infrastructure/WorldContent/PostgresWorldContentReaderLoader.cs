using System.Data;
using System.Diagnostics;
using Godswar.Server.Application.World;
using Npgsql;

namespace Godswar.Server.Infrastructure.WorldContent;

internal static partial class PostgresWorldContentReaderLoader
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
            var npcDefinitions = await LoadPublishedNpcDefinitionsAsync(
                connection,
                transaction,
                mapIds.ToHashSet(),
                cancellationToken);
            var npcDialogues =
                await LoadPublishedNpcDialogueDefinitionsAsync(
                    connection,
                    transaction,
                    npcDefinitions,
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

            var reader = PinnedWorldContentReader.Create(
                PostgresWorldContentSource,
                mapIds,
                npcDefinitions,
                monsters,
                enterBootstrap,
                npcTexts: npcDialogues.Texts,
                npcDialogueRoutes: npcDialogues.Routes);
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

}
