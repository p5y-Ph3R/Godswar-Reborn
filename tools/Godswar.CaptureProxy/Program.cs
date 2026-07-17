using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Godswar.Server.Packets;
using Npgsql;

var options = Options.Parse(args);
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

await using var log = new CaptureLog(options.OutputPath);
await using var packetLog = await PacketTransactionLog.CreateAsync(options, cts.Token);
var state = new ProxyState(options.DefaultGameHost, options.DefaultGamePort);

Console.WriteLine("Godswar capture proxy");
Console.WriteLine($"Login:  0.0.0.0:{options.LocalLoginPort} -> {options.LoginHost}:{options.LoginPort}");
Console.WriteLine($"Game:   0.0.0.0:{options.LocalGamePort} -> redirect target");
Console.WriteLine($"Rewrite game redirect to #{options.LocalAdvertisedHost}:{options.LocalGamePort}");
Console.WriteLine($"Log:    {Path.GetFullPath(options.OutputPath)}");
Console.WriteLine(packetLog is null
    ? "DB:     disabled"
    : $"DB:     packet_transactions session={packetLog.SessionId}");
Console.WriteLine("Press Ctrl+C to stop.");

var login = RunListenerAsync(
    "LOGIN",
    options.LocalLoginPort,
    _ => ValueTask.FromResult((options.LoginHost, options.LoginPort)),
    bytes => RewriteLoginRedirect(bytes, options, state, log),
    log,
    packetLog,
    cts.Token);

var game = RunListenerAsync(
    "GAME",
    options.LocalGamePort,
    state.WaitForGameTargetAsync,
    null,
    log,
    packetLog,
    cts.Token);

await Task.WhenAll(login, game);

static async Task RunListenerAsync(
    string name,
    int localPort,
    Func<CancellationToken, ValueTask<(string Host, int Port)>> targetResolver,
    Func<byte[], byte[]>? serverToClientTransform,
    CaptureLog log,
    PacketTransactionLog? packetLog,
    CancellationToken cancellationToken)
{
    var listener = new TcpListener(IPAddress.Any, localPort);
    listener.Start();

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(cancellationToken);
            _ = Task.Run(
                () => HandleConnectionAsync(name, client, targetResolver, serverToClientTransform, log, packetLog, cancellationToken),
                cancellationToken);
        }
    }
    catch (OperationCanceledException)
    {
    }
    finally
    {
        listener.Stop();
    }
}

static async Task HandleConnectionAsync(
    string name,
    TcpClient client,
    Func<CancellationToken, ValueTask<(string Host, int Port)>> targetResolver,
    Func<byte[], byte[]>? serverToClientTransform,
    CaptureLog log,
    PacketTransactionLog? packetLog,
    CancellationToken cancellationToken)
{
    using var _ = client;
    client.NoDelay = true;

    var clientEndPoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
    var target = await targetResolver(cancellationToken);
    var targetEndPoint = $"{target.Host}:{target.Port}";
    var connectionId = Guid.NewGuid();

    using var server = new TcpClient { NoDelay = true };
    await server.ConnectAsync(target.Host, target.Port, cancellationToken);

    log.Line($"{name} connected client={clientEndPoint} target={target.Host}:{target.Port}");

    await using var clientStream = client.GetStream();
    await using var serverStream = server.GetStream();
    var clientToServerCipher = new PacketCipher();
    var serverToClientCipher = new PacketCipher();

    var clientToServer = PumpAsync(
        $"{name} C->S",
        clientStream,
        serverStream,
        clientToServerCipher,
        clearTransform: null,
        log,
        packetLog,
        connectionId,
        name,
        "C2S",
        clientEndPoint,
        targetEndPoint,
        cancellationToken);

    var serverToClient = PumpAsync(
        $"{name} S->C",
        serverStream,
        clientStream,
        serverToClientCipher,
        serverToClientTransform,
        log,
        packetLog,
        connectionId,
        name,
        "S2C",
        targetEndPoint,
        clientEndPoint,
        cancellationToken);

    await Task.WhenAny(clientToServer, serverToClient);
    client.Close();
    server.Close();
    log.Line($"{name} closed client={clientEndPoint}");
}

static async Task PumpAsync(
    string direction,
    NetworkStream input,
    NetworkStream output,
    PacketCipher decodeCipher,
    Func<byte[], byte[]>? clearTransform,
    CaptureLog log,
    PacketTransactionLog? packetLog,
    Guid connectionId,
    string connectionName,
    string packetDirection,
    string sourceEndPoint,
    string destinationEndPoint,
    CancellationToken cancellationToken)
{
    var buffer = new byte[64 * 1024];
    var packetAccumulator = new PacketFrameAccumulator();

    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            var raw = buffer.AsSpan(0, read).ToArray();
            var clear = raw.ToArray();
            decodeCipher.Transform(clear);

            var patchedClear = clearTransform?.Invoke(clear) ?? clear;
            var outgoing = ReapplyPatchToRaw(raw, clear, patchedClear);
            log.Chunk(direction, clear, raw);
            if (packetLog is not null)
            {
                var chunkSequence = packetLog.NextChunkSequence();
                var packets = packetAccumulator.Append(patchedClear, outgoing);
                packetLog.Enqueue(
                    packets,
                    connectionId,
                    connectionName,
                    packetDirection,
                    sourceEndPoint,
                    destinationEndPoint,
                    chunkSequence);
            }

            await output.WriteAsync(outgoing, cancellationToken);
        }
    }
    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
    {
    }
}

static byte[] RewriteLoginRedirect(
    byte[] bytes,
    Options options,
    ProxyState state,
    CaptureLog log)
{
    var output = bytes.ToArray();

    for (var offset = 0; offset <= output.Length - 44; offset++)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(offset, 2));
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(offset + 2, 2));
        if (opcode != 10001 || length < 44)
        {
            continue;
        }

        var hostField = ReadNullTerminated(output, offset + 5, 35);
        var originalHost = hostField.TrimStart('#');
        var originalPort = BinaryPrimitives.ReadInt32LittleEndian(output.AsSpan(offset + 40, 4));

        if (!string.IsNullOrWhiteSpace(originalHost) && originalPort > 0)
        {
            state.SetGameTarget(originalHost, originalPort);
            log.Line($"LOGIN redirect original=#{originalHost}:{originalPort}");
        }

        output.AsSpan(offset + 5, 35).Clear();
        Encoding.ASCII.GetBytes(options.LocalAdvertisedHost, output.AsSpan(offset + 5, Math.Min(35, options.LocalAdvertisedHost.Length)));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(offset + 40, 4), options.LocalGamePort);

        log.Line($"LOGIN redirect rewritten=#{options.LocalAdvertisedHost}:{options.LocalGamePort}");
    }

    return output;
}

static byte[] ReapplyPatchToRaw(byte[] raw, byte[] clear, byte[] patchedClear)
{
    if (clear.AsSpan().SequenceEqual(patchedClear))
    {
        return raw;
    }

    if (patchedClear.Length != raw.Length)
    {
        throw new InvalidOperationException("Clear-text packet rewrite cannot change byte count.");
    }

    var output = raw.ToArray();
    for (var i = 0; i < output.Length; i++)
    {
        output[i] = (byte)(raw[i] ^ clear[i] ^ patchedClear[i]);
    }

    return output;
}

static string ReadNullTerminated(byte[] bytes, int offset, int maxLength)
{
    var end = offset;
    var limit = Math.Min(bytes.Length, offset + maxLength);
    while (end < limit && bytes[end] != 0)
    {
        end++;
    }

    return Encoding.ASCII.GetString(bytes, offset, end - offset);
}

sealed class ProxyState(string? defaultGameHost, int defaultGamePort)
{
    private readonly TaskCompletionSource<(string Host, int Port)> _gameTarget =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void SetGameTarget(string host, int port)
    {
        _gameTarget.TrySetResult((host, port));
    }

    public async ValueTask<(string Host, int Port)> WaitForGameTargetAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(defaultGameHost))
        {
            return (defaultGameHost, defaultGamePort);
        }

        return await _gameTarget.Task.WaitAsync(cancellationToken);
    }
}

sealed class PacketFrameAccumulator
{
    private const int MaxFrameLength = 64 * 1024;

    private readonly List<byte> _clearPending = [];
    private readonly List<byte> _rawPending = [];
    private long _pendingStreamOffset;

    public IReadOnlyList<CapturedPacketFrame> Append(ReadOnlySpan<byte> clearChunk, ReadOnlySpan<byte> rawChunk)
    {
        if (clearChunk.Length != rawChunk.Length)
        {
            throw new InvalidOperationException("Clear and raw packet buffers must have the same length.");
        }

        _clearPending.AddRange(clearChunk.ToArray());
        _rawPending.AddRange(rawChunk.ToArray());

        var packetIndex = 0;
        var packets = new List<CapturedPacketFrame>();

        while (_clearPending.Count >= 4)
        {
            var declaredLength = _clearPending[0] | (_clearPending[1] << 8);
            if (declaredLength < 4 || declaredLength > MaxFrameLength)
            {
                packets.Add(CreateFrame(packetIndex++, _clearPending.Count, declaredLength, null, "invalid frame length"));
                _pendingStreamOffset += _clearPending.Count;
                _clearPending.Clear();
                _rawPending.Clear();
                break;
            }

            if (_clearPending.Count < declaredLength)
            {
                break;
            }

            var opcode = _clearPending[2] | (_clearPending[3] << 8);
            packets.Add(CreateFrame(packetIndex++, declaredLength, declaredLength, opcode, string.Empty));
            _pendingStreamOffset += declaredLength;
            _clearPending.RemoveRange(0, declaredLength);
            _rawPending.RemoveRange(0, declaredLength);
        }

        return packets;
    }

    private CapturedPacketFrame CreateFrame(
        int packetIndex,
        int actualLength,
        int? declaredLength,
        int? opcode,
        string notes)
    {
        return new CapturedPacketFrame(
            PacketIndex: packetIndex,
            StreamOffset: _pendingStreamOffset,
            DeclaredLength: declaredLength,
            ActualLength: actualLength,
            Opcode: opcode,
            ClearBytes: _clearPending.GetRange(0, actualLength).ToArray(),
            RawBytes: _rawPending.GetRange(0, actualLength).ToArray(),
            Notes: notes);
    }
}

sealed class PacketTransactionLog : IAsyncDisposable
{
    private const string SchemaSql = """
        CREATE TABLE IF NOT EXISTS packet_capture_sessions (
            id uuid PRIMARY KEY,
            started_at timestamptz NOT NULL DEFAULT now(),
            capture_name varchar(128) NOT NULL DEFAULT '',
            login_host varchar(255) NOT NULL DEFAULT '',
            login_port integer NOT NULL DEFAULT 0,
            local_login_port integer NOT NULL DEFAULT 0,
            local_game_port integer NOT NULL DEFAULT 0,
            local_advertised_host varchar(255) NOT NULL DEFAULT '',
            default_game_host varchar(255),
            default_game_port integer NOT NULL DEFAULT 0,
            output_path text NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS packet_opcodes (
            opcode integer NOT NULL,
            direction varchar(4) NOT NULL DEFAULT 'ANY',
            name varchar(128) NOT NULL,
            category varchar(64) NOT NULL DEFAULT '',
            confidence varchar(16) NOT NULL DEFAULT 'known',
            description text NOT NULL DEFAULT '',
            notes text NOT NULL DEFAULT '',
            first_seen_at timestamptz,
            updated_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (opcode, direction),
            CONSTRAINT packet_opcodes_direction_check CHECK (direction IN ('ANY', 'C2S', 'S2C'))
        );

        CREATE TABLE IF NOT EXISTS packet_transactions (
            id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            capture_session_id uuid NOT NULL REFERENCES packet_capture_sessions (id) ON DELETE CASCADE,
            captured_at timestamptz NOT NULL DEFAULT now(),
            connection_id uuid NOT NULL,
            connection_name varchar(16) NOT NULL,
            direction varchar(4) NOT NULL,
            source_endpoint varchar(128) NOT NULL DEFAULT '',
            destination_endpoint varchar(128) NOT NULL DEFAULT '',
            chunk_sequence bigint NOT NULL,
            packet_sequence bigint NOT NULL,
            packet_index integer NOT NULL,
            stream_offset bigint NOT NULL,
            declared_length integer,
            actual_length integer NOT NULL,
            opcode integer,
            opcode_name varchar(128) NOT NULL DEFAULT 'Unknown',
            clear_bytes bytea NOT NULL,
            raw_bytes bytea NOT NULL,
            notes text NOT NULL DEFAULT ''
        );

        ALTER TABLE packet_transactions
            ADD COLUMN IF NOT EXISTS opcode_name varchar(128) NOT NULL DEFAULT 'Unknown';

        CREATE INDEX IF NOT EXISTS ix_packet_transactions_session_sequence
            ON packet_transactions (capture_session_id, packet_sequence);

        CREATE INDEX IF NOT EXISTS ix_packet_transactions_opcode
            ON packet_transactions (opcode);

        CREATE INDEX IF NOT EXISTS ix_packet_transactions_opcode_name
            ON packet_transactions (opcode_name);

        CREATE INDEX IF NOT EXISTS ix_packet_transactions_direction
            ON packet_transactions (connection_name, direction);

        CREATE INDEX IF NOT EXISTS ix_packet_transactions_captured_at
            ON packet_transactions (captured_at);

        CREATE TABLE IF NOT EXISTS npc_spawn_packets (
            map_id smallint NOT NULL,
            scene_key varchar(64) NOT NULL,
            npc_key varchar(96) NOT NULL,
            template_key varchar(128) NOT NULL,
            object_id bigint NOT NULL,
            pos_x real NOT NULL,
            pos_z real NOT NULL,
            clear_bytes bytea NOT NULL,
            detail_10077 bytea NOT NULL DEFAULT '\x'::bytea,
            detail_10080 bytea NOT NULL DEFAULT '\x'::bytea,
            source varchar(64) NOT NULL DEFAULT 'capture_proxy',
            first_seen_at timestamptz NOT NULL DEFAULT now(),
            last_seen_at timestamptz NOT NULL DEFAULT now(),
            capture_count integer NOT NULL DEFAULT 1,
            PRIMARY KEY (map_id, template_key)
        );

        ALTER TABLE npc_spawn_packets
            ADD COLUMN IF NOT EXISTS detail_10077 bytea NOT NULL DEFAULT '\x'::bytea;

        ALTER TABLE npc_spawn_packets
            ADD COLUMN IF NOT EXISTS detail_10080 bytea NOT NULL DEFAULT '\x'::bytea;

        CREATE INDEX IF NOT EXISTS ix_npc_spawn_packets_map
            ON npc_spawn_packets (map_id, npc_key);

        CREATE INDEX IF NOT EXISTS ix_npc_spawn_packets_object
            ON npc_spawn_packets (map_id, object_id);

        CREATE TABLE IF NOT EXISTS monster_spawn_packets (
            map_id smallint NOT NULL,
            scene_key varchar(96) NOT NULL,
            template_key varchar(128) NOT NULL,
            display_name varchar(255) NOT NULL DEFAULT '',
            object_id bigint NOT NULL,
            pos_x real NOT NULL,
            pos_z real NOT NULL,
            clear_bytes bytea NOT NULL,
            source varchar(64) NOT NULL DEFAULT 'capture_proxy',
            first_seen_at timestamptz NOT NULL DEFAULT now(),
            last_seen_at timestamptz NOT NULL DEFAULT now(),
            capture_count integer NOT NULL DEFAULT 1,
            PRIMARY KEY (map_id, object_id)
        );

        CREATE INDEX IF NOT EXISTS ix_monster_spawn_packets_map
            ON monster_spawn_packets (map_id, template_key);

        CREATE OR REPLACE FUNCTION set_packet_transaction_opcode_name()
        RETURNS trigger
        LANGUAGE plpgsql
        AS $$
        BEGIN
            NEW.opcode_name := COALESCE((
                SELECT packet_opcodes.name
                FROM packet_opcodes
                WHERE packet_opcodes.opcode = NEW.opcode
                  AND packet_opcodes.direction IN (NEW.direction, 'ANY')
                ORDER BY CASE WHEN packet_opcodes.direction = NEW.direction THEN 0 ELSE 1 END
                LIMIT 1
            ), 'Unknown');

            RETURN NEW;
        END;
        $$;

        DROP TRIGGER IF EXISTS trg_packet_transactions_opcode_name ON packet_transactions;

        CREATE TRIGGER trg_packet_transactions_opcode_name
        BEFORE INSERT OR UPDATE OF opcode, direction
        ON packet_transactions
        FOR EACH ROW
        EXECUTE FUNCTION set_packet_transaction_opcode_name();
        """;

    private const string InsertPacketSql = """
        INSERT INTO packet_transactions (
            capture_session_id,
            captured_at,
            connection_id,
            connection_name,
            direction,
            source_endpoint,
            destination_endpoint,
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
            @capture_session_id,
            @captured_at,
            @connection_id,
            @connection_name,
            @direction,
            @source_endpoint,
            @destination_endpoint,
            @chunk_sequence,
            @packet_sequence,
            @packet_index,
            @stream_offset,
            @declared_length,
            @actual_length,
            @opcode,
            @clear_bytes,
            @raw_bytes,
            @notes
        );
        """;

    private const string UpsertNpcSpawnSql = """
        INSERT INTO npc_spawn_packets (
            map_id,
            scene_key,
            npc_key,
            template_key,
            object_id,
            pos_x,
            pos_z,
            clear_bytes,
            source,
            first_seen_at,
            last_seen_at,
            capture_count
        )
        VALUES (
            @map_id,
            @scene_key,
            @npc_key,
            @template_key,
            @object_id,
            @pos_x,
            @pos_z,
            @clear_bytes,
            'capture_proxy',
            @captured_at,
            @captured_at,
            1
        )
        ON CONFLICT (map_id, template_key) DO UPDATE
        SET object_id = EXCLUDED.object_id,
            pos_x = EXCLUDED.pos_x,
            pos_z = EXCLUDED.pos_z,
            clear_bytes = EXCLUDED.clear_bytes,
            source = EXCLUDED.source,
            last_seen_at = EXCLUDED.last_seen_at,
            capture_count = npc_spawn_packets.capture_count + 1;
        """;

    private const string UpdateNpcDetailSql = """
        UPDATE npc_spawn_packets
        SET detail_10077 = CASE WHEN @opcode = 10077 THEN @clear_bytes ELSE detail_10077 END,
            detail_10080 = CASE WHEN @opcode = 10080 THEN @clear_bytes ELSE detail_10080 END,
            last_seen_at = @captured_at
        WHERE object_id = @object_id;
        """;

    private const string UpsertMonsterSpawnSql = """
        INSERT INTO monster_spawn_packets (
            map_id,
            scene_key,
            template_key,
            display_name,
            object_id,
            pos_x,
            pos_z,
            clear_bytes,
            source,
            first_seen_at,
            last_seen_at,
            capture_count
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
            'capture_proxy',
            @captured_at,
            @captured_at,
            1
        )
        ON CONFLICT (map_id, object_id) DO UPDATE
        SET template_key = EXCLUDED.template_key,
            display_name = EXCLUDED.display_name,
            pos_x = EXCLUDED.pos_x,
            pos_z = EXCLUDED.pos_z,
            clear_bytes = EXCLUDED.clear_bytes,
            source = EXCLUDED.source,
            last_seen_at = EXCLUDED.last_seen_at,
            capture_count = monster_spawn_packets.capture_count + 1;
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly Channel<PacketTransactionRecord> _queue =
        Channel.CreateUnbounded<PacketTransactionRecord>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, CapturedMonsterTemplate?> _monsterTemplateCache = new(StringComparer.Ordinal);
    private readonly Task _writerTask;
    private long _chunkSequence;
    private long _packetSequence;

    private PacketTransactionLog(NpgsqlDataSource dataSource, Guid sessionId)
    {
        _dataSource = dataSource;
        SessionId = sessionId;
        _writerTask = Task.Run(WriteLoopAsync);
    }

    public Guid SessionId { get; }

    public static async Task<PacketTransactionLog?> CreateAsync(Options options, CancellationToken cancellationToken)
    {
        if (options.DisableDatabaseLogging)
        {
            return null;
        }

        var dataSource = NpgsqlDataSource.Create(options.PostgresConnectionString);
        await using (var schemaCommand = dataSource.CreateCommand(SchemaSql))
        {
            await schemaCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var sessionId = Guid.NewGuid();
        await using (var sessionCommand = dataSource.CreateCommand("""
            INSERT INTO packet_capture_sessions (
                id,
                capture_name,
                login_host,
                login_port,
                local_login_port,
                local_game_port,
                local_advertised_host,
                default_game_host,
                default_game_port,
                output_path
            )
            VALUES (
                @id,
                @capture_name,
                @login_host,
                @login_port,
                @local_login_port,
                @local_game_port,
                @local_advertised_host,
                @default_game_host,
                @default_game_port,
                @output_path
            );
            """))
        {
            sessionCommand.Parameters.AddWithValue("id", sessionId);
            sessionCommand.Parameters.AddWithValue("capture_name", Path.GetFileNameWithoutExtension(options.OutputPath));
            sessionCommand.Parameters.AddWithValue("login_host", options.LoginHost);
            sessionCommand.Parameters.AddWithValue("login_port", options.LoginPort);
            sessionCommand.Parameters.AddWithValue("local_login_port", options.LocalLoginPort);
            sessionCommand.Parameters.AddWithValue("local_game_port", options.LocalGamePort);
            sessionCommand.Parameters.AddWithValue("local_advertised_host", options.LocalAdvertisedHost);
            sessionCommand.Parameters.AddWithValue("default_game_host", (object?)options.DefaultGameHost ?? DBNull.Value);
            sessionCommand.Parameters.AddWithValue("default_game_port", options.DefaultGamePort);
            sessionCommand.Parameters.AddWithValue("output_path", Path.GetFullPath(options.OutputPath));
            await sessionCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return new PacketTransactionLog(dataSource, sessionId);
    }

    public long NextChunkSequence()
    {
        return Interlocked.Increment(ref _chunkSequence);
    }

    public void Enqueue(
        IReadOnlyList<CapturedPacketFrame> frames,
        Guid connectionId,
        string connectionName,
        string direction,
        string sourceEndPoint,
        string destinationEndPoint,
        long chunkSequence)
    {
        foreach (var frame in frames)
        {
            _queue.Writer.TryWrite(new PacketTransactionRecord(
                CaptureSessionId: SessionId,
                CapturedAt: DateTimeOffset.UtcNow,
                ConnectionId: connectionId,
                ConnectionName: connectionName,
                Direction: direction,
                SourceEndPoint: sourceEndPoint,
                DestinationEndPoint: destinationEndPoint,
                ChunkSequence: chunkSequence,
                PacketSequence: Interlocked.Increment(ref _packetSequence),
                PacketIndex: frame.PacketIndex,
                StreamOffset: frame.StreamOffset,
                DeclaredLength: frame.DeclaredLength,
                ActualLength: frame.ActualLength,
                Opcode: frame.Opcode,
                ClearBytes: frame.ClearBytes,
                RawBytes: frame.RawBytes,
                Notes: frame.Notes));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _writerTask;
        await _dataSource.DisposeAsync();
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var packet in _queue.Reader.ReadAllAsync())
        {
            try
            {
                await InsertAsync(packet);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[db] packet transaction insert failed: {ex.Message}");
            }
        }
    }

    private async Task InsertAsync(PacketTransactionRecord packet)
    {
        await using var command = _dataSource.CreateCommand(InsertPacketSql);
        command.Parameters.AddWithValue("capture_session_id", packet.CaptureSessionId);
        command.Parameters.AddWithValue("captured_at", packet.CapturedAt);
        command.Parameters.AddWithValue("connection_id", packet.ConnectionId);
        command.Parameters.AddWithValue("connection_name", packet.ConnectionName);
        command.Parameters.AddWithValue("direction", packet.Direction);
        command.Parameters.AddWithValue("source_endpoint", packet.SourceEndPoint);
        command.Parameters.AddWithValue("destination_endpoint", packet.DestinationEndPoint);
        command.Parameters.AddWithValue("chunk_sequence", packet.ChunkSequence);
        command.Parameters.AddWithValue("packet_sequence", packet.PacketSequence);
        command.Parameters.AddWithValue("packet_index", packet.PacketIndex);
        command.Parameters.AddWithValue("stream_offset", packet.StreamOffset);
        command.Parameters.AddWithValue("declared_length", (object?)packet.DeclaredLength ?? DBNull.Value);
        command.Parameters.AddWithValue("actual_length", packet.ActualLength);
        command.Parameters.AddWithValue("opcode", (object?)packet.Opcode ?? DBNull.Value);
        command.Parameters.AddWithValue("clear_bytes", packet.ClearBytes);
        command.Parameters.AddWithValue("raw_bytes", packet.RawBytes);
        command.Parameters.AddWithValue("notes", packet.Notes);
        await command.ExecuteNonQueryAsync();

        if (TryParseCityNpcSpawn(packet, out var spawn))
        {
            await UpsertNpcSpawnAsync(spawn, packet.CapturedAt);
        }
        else if (TryParseMonsterSpawn(packet, out var monsterSpawn))
        {
            var template = await ResolveMonsterTemplateAsync(monsterSpawn.TemplateKey);
            if (template is not null)
            {
                await UpsertMonsterSpawnAsync(monsterSpawn, template.Value, packet.CapturedAt);
            }
        }
        else if (TryParseNpcDetailPacket(packet, out var detail))
        {
            await UpdateNpcDetailAsync(detail, packet.CapturedAt);
        }
    }

    private async Task UpsertNpcSpawnAsync(CapturedNpcSpawnRecord spawn, DateTimeOffset capturedAt)
    {
        await using var command = _dataSource.CreateCommand(UpsertNpcSpawnSql);
        command.Parameters.AddWithValue("map_id", spawn.MapId);
        command.Parameters.AddWithValue("scene_key", spawn.SceneKey);
        command.Parameters.AddWithValue("npc_key", spawn.NpcKey);
        command.Parameters.AddWithValue("template_key", spawn.TemplateKey);
        command.Parameters.AddWithValue("object_id", (long)spawn.ObjectId);
        command.Parameters.AddWithValue("pos_x", spawn.X);
        command.Parameters.AddWithValue("pos_z", spawn.Z);
        command.Parameters.AddWithValue("clear_bytes", spawn.Packet);
        command.Parameters.AddWithValue("captured_at", capturedAt);
        await command.ExecuteNonQueryAsync();
    }

    private async Task UpsertMonsterSpawnAsync(
        CapturedMonsterSpawnRecord spawn,
        CapturedMonsterTemplate template,
        DateTimeOffset capturedAt)
    {
        await using var command = _dataSource.CreateCommand(UpsertMonsterSpawnSql);
        command.Parameters.AddWithValue("map_id", template.MapId);
        command.Parameters.AddWithValue("scene_key", template.SceneKey);
        command.Parameters.AddWithValue("template_key", spawn.TemplateKey);
        command.Parameters.AddWithValue("display_name", template.DisplayName);
        command.Parameters.AddWithValue("object_id", (long)spawn.ObjectId);
        command.Parameters.AddWithValue("pos_x", spawn.X);
        command.Parameters.AddWithValue("pos_z", spawn.Z);
        command.Parameters.AddWithValue("clear_bytes", spawn.Packet);
        command.Parameters.AddWithValue("captured_at", capturedAt);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<CapturedMonsterTemplate?> ResolveMonsterTemplateAsync(string templateKey)
    {
        if (_monsterTemplateCache.TryGetValue(templateKey, out var cached))
        {
            return cached;
        }

        await using var command = _dataSource.CreateCommand("""
            SELECT source_map_id, scene_key, display_name
            FROM monster_templates
            WHERE template_key = @template_key
              AND source_map_id IS NOT NULL
            ORDER BY CASE source_map_id
                         WHEN 0 THEN 0
                         WHEN 1 THEN 1
                         WHEN 4 THEN 2
                         ELSE 3
                     END,
                     source_map_id
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("template_key", templateKey);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            _monsterTemplateCache[templateKey] = null;
            return null;
        }

        var template = new CapturedMonsterTemplate(
            reader.GetInt16(0),
            reader.GetString(1),
            reader.GetString(2));
        _monsterTemplateCache[templateKey] = template;
        return template;
    }

    private async Task UpdateNpcDetailAsync(CapturedNpcDetailRecord detail, DateTimeOffset capturedAt)
    {
        await using var command = _dataSource.CreateCommand(UpdateNpcDetailSql);
        command.Parameters.AddWithValue("opcode", detail.Opcode);
        command.Parameters.AddWithValue("object_id", (long)detail.ObjectId);
        command.Parameters.AddWithValue("clear_bytes", detail.Packet);
        command.Parameters.AddWithValue("captured_at", capturedAt);
        await command.ExecuteNonQueryAsync();
    }

    private static bool TryParseCityNpcSpawn(PacketTransactionRecord packet, out CapturedNpcSpawnRecord spawn)
    {
        spawn = default;

        if (!string.Equals(packet.ConnectionName, "game", StringComparison.OrdinalIgnoreCase) ||
            packet.Direction != "S2C" ||
            packet.Opcode != 10020 ||
            packet.ClearBytes.Length < 108)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(packet.ClearBytes.AsSpan(0, 2));
        if (length > packet.ClearBytes.Length || length < 108)
        {
            return false;
        }

        var templateKey = ReadNullTerminatedAscii(packet.ClearBytes.AsSpan(44, length - 44));
        short mapId;
        string sceneKey;
        if (templateKey.StartsWith("Sparta_", StringComparison.Ordinal))
        {
            mapId = 0;
            sceneKey = "Sparta";
        }
        else if (templateKey.StartsWith("Athens_", StringComparison.Ordinal))
        {
            mapId = 1;
            sceneKey = "Athens";
        }
        else
        {
            return false;
        }

        var secondUnderscore = templateKey.IndexOf('_', "Athens_".Length);
        if (secondUnderscore < 0)
        {
            return false;
        }

        var npcKey = templateKey[..secondUnderscore];
        var objectId = BinaryPrimitives.ReadUInt32LittleEndian(packet.ClearBytes.AsSpan(8, 4));
        var x = BinaryPrimitives.ReadSingleLittleEndian(packet.ClearBytes.AsSpan(28, 4));
        var z = BinaryPrimitives.ReadSingleLittleEndian(packet.ClearBytes.AsSpan(36, 4));

        spawn = new CapturedNpcSpawnRecord(
            mapId,
            sceneKey,
            npcKey,
            templateKey,
            objectId,
            x,
            z,
            packet.ClearBytes[..length]);
        return true;
    }

    private static bool TryParseMonsterSpawn(PacketTransactionRecord packet, out CapturedMonsterSpawnRecord spawn)
    {
        spawn = default;

        if (!string.Equals(packet.ConnectionName, "game", StringComparison.OrdinalIgnoreCase) ||
            packet.Direction != "S2C" ||
            packet.Opcode != 10020 ||
            packet.ClearBytes.Length < 108)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(packet.ClearBytes.AsSpan(0, 2));
        if (length > packet.ClearBytes.Length || length < 108)
        {
            return false;
        }

        var templateKey = ReadNullTerminatedAscii(packet.ClearBytes.AsSpan(44, length - 44));
        if (templateKey.StartsWith("Sparta_", StringComparison.Ordinal) ||
            templateKey.StartsWith("Athens_", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(templateKey))
        {
            return false;
        }

        var objectType = BinaryPrimitives.ReadUInt32LittleEndian(packet.ClearBytes.AsSpan(4, 4));
        if (objectType != 0x00000212)
        {
            return false;
        }

        spawn = new CapturedMonsterSpawnRecord(
            templateKey,
            BinaryPrimitives.ReadUInt32LittleEndian(packet.ClearBytes.AsSpan(8, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(packet.ClearBytes.AsSpan(28, 4)),
            BinaryPrimitives.ReadSingleLittleEndian(packet.ClearBytes.AsSpan(36, 4)),
            packet.ClearBytes[..length]);
        return true;
    }

    private static bool TryParseNpcDetailPacket(PacketTransactionRecord packet, out CapturedNpcDetailRecord detail)
    {
        detail = default;

        if (!string.Equals(packet.ConnectionName, "game", StringComparison.OrdinalIgnoreCase) ||
            packet.Direction != "S2C" ||
            packet.Opcode is not (10077 or 10080) ||
            packet.ClearBytes.Length < 8)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(packet.ClearBytes.AsSpan(0, 2));
        if (length > packet.ClearBytes.Length || length < 8)
        {
            return false;
        }

        var objectId = BinaryPrimitives.ReadUInt32LittleEndian(packet.ClearBytes.AsSpan(4, 4));
        detail = new CapturedNpcDetailRecord(packet.Opcode.Value, objectId, packet.ClearBytes[..length]);
        return true;
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        return Encoding.ASCII.GetString(bytes[..length]);
    }
}

readonly record struct CapturedNpcSpawnRecord(
    short MapId,
    string SceneKey,
    string NpcKey,
    string TemplateKey,
    uint ObjectId,
    float X,
    float Z,
    byte[] Packet);

readonly record struct CapturedNpcDetailRecord(
    int Opcode,
    uint ObjectId,
    byte[] Packet);

readonly record struct CapturedMonsterSpawnRecord(
    string TemplateKey,
    uint ObjectId,
    float X,
    float Z,
    byte[] Packet);

readonly record struct CapturedMonsterTemplate(
    short MapId,
    string SceneKey,
    string DisplayName);

sealed record CapturedPacketFrame(
    int PacketIndex,
    long StreamOffset,
    int? DeclaredLength,
    int ActualLength,
    int? Opcode,
    byte[] ClearBytes,
    byte[] RawBytes,
    string Notes);

sealed record PacketTransactionRecord(
    Guid CaptureSessionId,
    DateTimeOffset CapturedAt,
    Guid ConnectionId,
    string ConnectionName,
    string Direction,
    string SourceEndPoint,
    string DestinationEndPoint,
    long ChunkSequence,
    long PacketSequence,
    int PacketIndex,
    long StreamOffset,
    int? DeclaredLength,
    int ActualLength,
    int? Opcode,
    byte[] ClearBytes,
    byte[] RawBytes,
    string Notes);

sealed class CaptureLog : IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    public CaptureLog(string outputPath)
    {
        _writer = new StreamWriter(File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true
        };
    }

    public void Line(string message)
    {
        lock (_lock)
        {
            _writer.WriteLine($"{DateTimeOffset.Now:O} {message}");
        }
    }

    public void Chunk(string direction, ReadOnlySpan<byte> clearBytes, ReadOnlySpan<byte> rawBytes)
    {
        lock (_lock)
        {
            _writer.WriteLine($"{DateTimeOffset.Now:O} {direction} bytes={clearBytes.Length} clearHead={DescribeHead(clearBytes)} rawHead={DescribeHead(rawBytes)}");
            _writer.WriteLine("CLEAR " + Convert.ToHexString(clearBytes));
            _writer.WriteLine("RAW   " + Convert.ToHexString(rawBytes));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
    }

    private static string DescribeHead(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4)
        {
            return "short";
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[..2]);
        var opcode = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(2, 2));
        return $"declared={length} opcode={opcode}";
    }
}

sealed class PacketCipher
{
    private static readonly byte[] HashOne = ReferencePackets.HashOne.ToArray();
    private static readonly byte[] HashTwo = ReferencePackets.HashTwo.ToArray();

    private int _pointer;

    public void Transform(Span<byte> packet)
    {
        for (var i = 0; i < packet.Length; i++)
        {
            packet[i] = (byte)((packet[i] ^ HashOne[_pointer]) ^ HashTwo[_pointer]);
            _pointer = (_pointer + 1) & 0xff;
        }
    }
}

sealed record Options(
    string LoginHost,
    int LoginPort,
    int LocalLoginPort,
    int LocalGamePort,
    string LocalAdvertisedHost,
    string? DefaultGameHost,
    int DefaultGamePort,
    string OutputPath,
    string PostgresConnectionString,
    bool DisableDatabaseLogging)
{
    private const string DefaultPostgresConnectionString =
        "Host=127.0.0.1;Port=5432;Database=godswar;Username=godswar;Password=godswar_dev_password;Pooling=true";

    public static Options Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var key = args[i][2..];
            if (string.Equals(key, "disable-db", StringComparison.OrdinalIgnoreCase))
            {
                values[key] = "true";
                continue;
            }

            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for --{key}");
            }

            values[key] = args[++i];
        }

        if (!values.TryGetValue("login-host", out var loginHost) || string.IsNullOrWhiteSpace(loginHost))
        {
            throw new ArgumentException("Required: --login-host <host-or-ip>");
        }

        return new Options(
            LoginHost: loginHost,
            LoginPort: GetInt(values, "login-port", 5999),
            LocalLoginPort: GetInt(values, "local-login-port", 5999),
            LocalGamePort: GetInt(values, "local-game-port", 7000),
            LocalAdvertisedHost: GetString(values, "local-advertised-host", "127.1.1.110"),
            DefaultGameHost: values.GetValueOrDefault("default-game-host"),
            DefaultGamePort: GetInt(values, "default-game-port", 7000),
            OutputPath: GetString(values, "out", Path.Combine("captures", $"godswar-proxy-{DateTime.Now:yyyyMMdd-HHmmss}.log")),
            PostgresConnectionString: GetString(
                values,
                "postgres-connection-string",
                Environment.GetEnvironmentVariable("GODSWAR_CAPTURE_POSTGRES_CONNECTION_STRING")
                    ?? Environment.GetEnvironmentVariable("GODSWAR_POSTGRES_CONNECTION_STRING")
                    ?? DefaultPostgresConnectionString),
            DisableDatabaseLogging: GetBool(values, "disable-db", false));
    }

    private static int GetInt(Dictionary<string, string> values, string key, int fallback)
    {
        return values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed)
            ? parsed
            : fallback;
    }

    private static string GetString(Dictionary<string, string> values, string key, string fallback)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : fallback;
    }

    private static bool GetBool(Dictionary<string, string> values, string key, bool fallback)
    {
        return values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed)
            ? parsed
            : fallback;
    }
}
