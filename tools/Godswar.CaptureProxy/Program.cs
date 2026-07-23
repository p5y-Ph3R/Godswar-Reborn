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
if (packetLog is not null)
{
    Console.WriteLine(options.MonsterMapId is short monsterMapId
        ? $"Mobs:   explicit map {monsterMapId}"
        : "Mobs:   packet log only; spawn upserts require --monster-map-id");
}
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
