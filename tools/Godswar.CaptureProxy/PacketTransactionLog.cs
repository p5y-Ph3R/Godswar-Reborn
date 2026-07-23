using System.Threading.Channels;
using Npgsql;

sealed partial class PacketTransactionLog : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly short? _monsterMapId;
    private readonly Channel<PacketTransactionRecord> _queue =
        Channel.CreateUnbounded<PacketTransactionRecord>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, CapturedMonsterTemplate?> _monsterTemplateCache = new(StringComparer.Ordinal);
    private readonly Task _writerTask;
    private long _chunkSequence;
    private long _packetSequence;

    private PacketTransactionLog(NpgsqlDataSource dataSource, Guid sessionId, short? monsterMapId)
    {
        _dataSource = dataSource;
        _monsterMapId = monsterMapId;
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

        return new PacketTransactionLog(dataSource, sessionId, options.MonsterMapId);
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
            if (!_monsterMapId.HasValue)
            {
                return;
            }

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
}
