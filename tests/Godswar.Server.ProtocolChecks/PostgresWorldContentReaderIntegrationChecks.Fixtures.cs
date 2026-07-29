using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
using Npgsql;
using NpgsqlTypes;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PostgresWorldContentReaderIntegrationChecks
{
    private static async Task InsertFixturesAsync(
        NpgsqlDataSource dataSource,
        CapturedMonsterSpawn fixture,
        byte[] historicalPacket,
        Guid captureSessionId,
        Guid connectionId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        await AssertFixtureKeysAreFreeAsync(connection, transaction);

        await using (var monsterCommand = new NpgsqlCommand(
                         """
                         INSERT INTO monster_spawn_packets (
                             map_id,
                             scene_key,
                             template_key,
                             display_name,
                             object_id,
                             pos_x,
                             pos_z,
                             clear_bytes,
                             source
                         )
                         VALUES (
                             @map_id,
                             @scene_key,
                             @template_key,
                             @display_name,
                             @object_id,
                             @pos_x,
                             @pos_z,
                             @clear_bytes,
                             @source
                         );
                         """,
                         connection,
                         transaction))
        {
            monsterCommand.Parameters.AddWithValue(
                "map_id",
                NpgsqlDbType.Smallint,
                fixture.MapId);
            monsterCommand.Parameters.AddWithValue(
                "scene_key",
                NpgsqlDbType.Varchar,
                fixture.SceneKey);
            monsterCommand.Parameters.AddWithValue(
                "template_key",
                NpgsqlDbType.Varchar,
                fixture.TemplateKey);
            monsterCommand.Parameters.AddWithValue(
                "display_name",
                NpgsqlDbType.Varchar,
                fixture.DisplayName);
            monsterCommand.Parameters.AddWithValue(
                "object_id",
                NpgsqlDbType.Bigint,
                checked((long)fixture.ObjectId));
            monsterCommand.Parameters.AddWithValue(
                "pos_x",
                NpgsqlDbType.Real,
                fixture.X);
            monsterCommand.Parameters.AddWithValue(
                "pos_z",
                NpgsqlDbType.Real,
                fixture.Z);
            monsterCommand.Parameters.AddWithValue(
                "clear_bytes",
                NpgsqlDbType.Bytea,
                fixture.Packet);
            monsterCommand.Parameters.AddWithValue(
                "source",
                NpgsqlDbType.Varchar,
                FixtureSource);
            Check.Equal(
                1,
                await monsterCommand.ExecuteNonQueryAsync(),
                "one tracked monster fixture is inserted");
        }

        await using (var sessionCommand = new NpgsqlCommand(
                         """
                         INSERT INTO packet_capture_sessions (
                             id,
                             capture_name
                         )
                         VALUES (@id, @capture_name);
                         """,
                         connection,
                         transaction))
        {
            sessionCommand.Parameters.AddWithValue(
                "id",
                NpgsqlDbType.Uuid,
                captureSessionId);
            sessionCommand.Parameters.AddWithValue(
                "capture_name",
                NpgsqlDbType.Varchar,
                "B05 packet-history decoy");
            Check.Equal(
                1,
                await sessionCommand.ExecuteNonQueryAsync(),
                "one packet-history fixture session is inserted");
        }

        await using (var packetCommand = new NpgsqlCommand(
                         """
                         INSERT INTO packet_transactions (
                             id,
                             capture_session_id,
                             connection_id,
                             connection_name,
                             direction,
                             chunk_sequence,
                             packet_sequence,
                             packet_index,
                             stream_offset,
                             declared_length,
                             actual_length,
                             opcode,
                             clear_bytes,
                             raw_bytes,
                             notes
                         )
                         VALUES (
                             @id,
                             @capture_session_id,
                             @connection_id,
                             'game',
                             'S2C',
                             1,
                             1,
                             0,
                             0,
                             @packet_length,
                             @packet_length,
                             10090,
                             @clear_bytes,
                             @raw_bytes,
                             @notes
                         );
                         """,
                         connection,
                         transaction))
        {
            packetCommand.Parameters.AddWithValue(
                "id",
                NpgsqlDbType.Bigint,
                HistoricalPacketTransactionId);
            packetCommand.Parameters.AddWithValue(
                "capture_session_id",
                NpgsqlDbType.Uuid,
                captureSessionId);
            packetCommand.Parameters.AddWithValue(
                "connection_id",
                NpgsqlDbType.Uuid,
                connectionId);
            packetCommand.Parameters.AddWithValue(
                "packet_length",
                NpgsqlDbType.Integer,
                historicalPacket.Length);
            packetCommand.Parameters.AddWithValue(
                "clear_bytes",
                NpgsqlDbType.Bytea,
                historicalPacket);
            packetCommand.Parameters.AddWithValue(
                "raw_bytes",
                NpgsqlDbType.Bytea,
                historicalPacket);
            packetCommand.Parameters.AddWithValue(
                "notes",
                NpgsqlDbType.Text,
                "B05 must never use packet history as published content.");
            Check.Equal(
                1,
                await packetCommand.ExecuteNonQueryAsync(),
                "one packet-history decoy is inserted");
        }

        await transaction.CommitAsync();
    }

    private static async Task AssertFixtureKeysAreFreeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM monster_spawn_packets
                    WHERE map_id = @map_id
                      AND object_id = @object_id
                ),
                EXISTS (
                    SELECT 1
                    FROM packet_transactions
                    WHERE id = @packet_id
                );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "map_id",
            NpgsqlDbType.Smallint,
            FixtureMapId);
        command.Parameters.AddWithValue(
            "object_id",
            NpgsqlDbType.Bigint,
            checked((long)FixtureMonsterObjectId));
        command.Parameters.AddWithValue(
            "packet_id",
            NpgsqlDbType.Bigint,
            HistoricalPacketTransactionId);
        await using var reader = await command.ExecuteReaderAsync();
        Check.True(
            await reader.ReadAsync(),
            "fixture-collision query returns one row");
        Check.True(
            !reader.GetBoolean(0),
            "tracked monster fixture key is unused");
        Check.True(
            !reader.GetBoolean(1),
            "packet-history fixture key is unused");
    }

    private static async Task MutateFixtureDisplayNameAsync(
        NpgsqlDataSource dataSource)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE monster_spawn_packets
            SET display_name = @display_name
            WHERE map_id = @map_id
              AND object_id = @object_id
              AND source = @source
              AND display_name = @initial_display_name;
            """,
            connection);
        command.Parameters.AddWithValue(
            "display_name",
            NpgsqlDbType.Varchar,
            MutatedDisplayName);
        command.Parameters.AddWithValue(
            "map_id",
            NpgsqlDbType.Smallint,
            FixtureMapId);
        command.Parameters.AddWithValue(
            "object_id",
            NpgsqlDbType.Bigint,
            checked((long)FixtureMonsterObjectId));
        command.Parameters.AddWithValue(
            "source",
            NpgsqlDbType.Varchar,
            FixtureSource);
        command.Parameters.AddWithValue(
            "initial_display_name",
            NpgsqlDbType.Varchar,
            InitialDisplayName);
        Check.Equal(
            1,
            await command.ExecuteNonQueryAsync(),
            "one tracked backing row is mutated");
    }

    private static async Task DeleteExactFixturesAsync(
        NpgsqlDataSource dataSource,
        Guid captureSessionId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction =
            await connection.BeginTransactionAsync();

        await using (var monsterCommand = new NpgsqlCommand(
                         """
                         DELETE FROM monster_spawn_packets
                         WHERE map_id = @map_id
                           AND object_id = @object_id
                           AND source = @source;
                         """,
                         connection,
                         transaction))
        {
            monsterCommand.Parameters.AddWithValue(
                "map_id",
                NpgsqlDbType.Smallint,
                FixtureMapId);
            monsterCommand.Parameters.AddWithValue(
                "object_id",
                NpgsqlDbType.Bigint,
                checked((long)FixtureMonsterObjectId));
            monsterCommand.Parameters.AddWithValue(
                "source",
                NpgsqlDbType.Varchar,
                FixtureSource);
            Check.Equal(
                1,
                await monsterCommand.ExecuteNonQueryAsync(),
                "one exact monster fixture is cleaned");
        }

        await using (var packetCommand = new NpgsqlCommand(
                         """
                         DELETE FROM packet_transactions
                         WHERE id = @id
                           AND capture_session_id = @capture_session_id;
                         """,
                         connection,
                         transaction))
        {
            packetCommand.Parameters.AddWithValue(
                "id",
                NpgsqlDbType.Bigint,
                HistoricalPacketTransactionId);
            packetCommand.Parameters.AddWithValue(
                "capture_session_id",
                NpgsqlDbType.Uuid,
                captureSessionId);
            Check.Equal(
                1,
                await packetCommand.ExecuteNonQueryAsync(),
                "one exact packet-history fixture is cleaned");
        }

        await using (var sessionCommand = new NpgsqlCommand(
                         """
                         DELETE FROM packet_capture_sessions
                         WHERE id = @id;
                         """,
                         connection,
                         transaction))
        {
            sessionCommand.Parameters.AddWithValue(
                "id",
                NpgsqlDbType.Uuid,
                captureSessionId);
            Check.Equal(
                1,
                await sessionCommand.ExecuteNonQueryAsync(),
                "one exact packet-history session is cleaned");
        }

        await transaction.CommitAsync();
    }

    private static CapturedMonsterSpawn CreateMonsterFixture()
    {
        var packet = Convert.FromHexString(
            "6C00242712020000752700000400000000000000320100003201000017ED144300000000E0D55F42B70B05C0415F6E6F726D616C5F737475625F3030330000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
        BinaryPrimitives.WriteUInt32LittleEndian(
            packet.AsSpan(8, 4),
            FixtureMonsterObjectId);
        var fixture = new CapturedMonsterSpawn(
            FixtureMapId,
            "Sparta",
            "A_normal_stub_003",
            InitialDisplayName,
            FixtureMonsterObjectId,
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(28, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(packet.AsSpan(36, 4)),
            packet);
        fixture.Validate(FixtureMapId);
        return fixture;
    }

    private static byte[] CreateHistoricalBootstrapDecoy()
    {
        var packet = new byte[2048];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(0, 2),
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2, 2),
            10090);
        "B05_PACKET_HISTORY_IS_NOT_CONTENT"u8.CopyTo(
            packet.AsSpan(4));
        return packet;
    }
}
