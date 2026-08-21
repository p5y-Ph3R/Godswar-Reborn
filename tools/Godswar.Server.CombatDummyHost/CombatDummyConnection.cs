using System.Buffers.Binary;
using System.Diagnostics;
using Godswar.Server.Protocol;

namespace Godswar.Server.CombatDummyHost;

internal sealed class CombatDummyConnection(
    CombatDummyDefinition definition,
    CombatDummyHostOptions options,
    CombatDummyReadiness readiness)
{
    private const ushort PlayerDeathOpcode = 0x2722;
    private const int MaximumHandshakePackets = 4_096;
    private long _lastReceiveTimestamp;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        do
        {
            var reconnectDelay = options.ReconnectDelay;
            readiness.Connecting(definition);
            try
            {
                await RunSessionAsync(cancellationToken);
                if (options.ExitAfterReady)
                {
                    return;
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                readiness.Unavailable(
                    definition,
                    "Stopped",
                    detail: null);
                return;
            }
            catch (Exception error)
            {
                reconnectDelay = ResolveReconnectDelay(options, error);
                readiness.Unavailable(
                    definition,
                    options.ExitAfterReady ? "Failed" : "Reconnecting",
                    error.Message);
                if (options.ExitAfterReady)
                {
                    throw;
                }

                Console.WriteLine(
                    $"[{definition.CharacterName}] reconnecting after " +
                    $"{error.GetType().Name}: {Sanitize(error.Message)}");
            }

            try
            {
                await Task.Delay(reconnectDelay, cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                readiness.Unavailable(
                    definition,
                    "Stopped",
                    detail: null);
                return;
            }
        }
        while (!cancellationToken.IsCancellationRequested);
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        await using var peer = await LegacyDummyPeer.ConnectAsync(
            options.Address,
            options.GamePort,
            cancellationToken);
        using var readyDeadline = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        readyDeadline.CancelAfter(TimeSpan.FromSeconds(30));

        await peer.SendAsync(
            DummyPackets.GameLogin(definition.AccountUsername),
            readyDeadline.Token);
        var preview = await ReadThroughOpcodeAsync(
            peer,
            Opcodes.RoleInfo,
            readyDeadline.Token);
        CombatDummyHandshakeValidator.ValidateCharacterPreview(
            definition,
            preview);

        await peer.SendAsync(DummyPackets.EnterGame(), readyDeadline.Token);
        await ReadThroughEnterGameAsync(peer, readyDeadline.Token);

        await peer.SendAsync(
            DummyPackets.ServerTimeRequest(),
            readyDeadline.Token);
        await peer.SendAsync(DummyPackets.ClientReady(), readyDeadline.Token);
        await peer.SendAsync(
            DummyPackets.PlayerDetailRequest(),
            readyDeadline.Token);
        await peer.SendAsync(
            DummyPackets.EnterUiReady(),
            readyDeadline.Token);
        await ReadThroughWorldReadyAsync(peer, readyDeadline.Token);

        readiness.Ready(definition);
        Volatile.Write(
            ref _lastReceiveTimestamp,
            Stopwatch.GetTimestamp());
        Console.WriteLine(
            $"[{definition.CharacterName}] ready map={definition.MapId} " +
            $"position={definition.PositionX:F0},{definition.PositionZ:F0}");
        if (options.ExitAfterReady)
        {
            return;
        }

        using var sessionLifetime = CancellationTokenSource
            .CreateLinkedTokenSource(cancellationToken);
        var receive = ReceiveUntilDisconnectAsync(
            peer,
            sessionLifetime.Token);
        var heartbeat = SendHeartbeatsAsync(
            peer,
            sessionLifetime.Token);
        try
        {
            await await Task.WhenAny(receive, heartbeat);
        }
        finally
        {
            sessionLifetime.Cancel();
            try
            {
                await Task.WhenAll(receive, heartbeat);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task ReceiveUntilDisconnectAsync(
        LegacyDummyPeer peer,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var packet = await peer.ReadAsync(cancellationToken);
            Volatile.Write(
                ref _lastReceiveTimestamp,
                Stopwatch.GetTimestamp());
            if (ReadOpcode(packet) == Opcodes.Ping)
            {
                readiness.Heartbeat(definition);
            }
            if (IsTerminalLocalDeath(packet))
            {
                await DelayTerminalDisconnectAsync(
                    options.CorpseRetentionDelay,
                    static (delay, token) => Task.Delay(delay, token),
                    cancellationToken);
            }
        }
    }

    internal static async Task DelayTerminalDisconnectAsync(
        TimeSpan corpseRetentionDelay,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delayAsync);
        await delayAsync(corpseRetentionDelay, cancellationToken);
        throw new CombatDummyTerminalDeathException();
    }

    internal static TimeSpan ResolveReconnectDelay(
        CombatDummyHostOptions hostOptions,
        Exception sessionError) =>
        sessionError is CombatDummyTerminalDeathException
            ? hostOptions.PostRemovalReconnectDelay
            : hostOptions.ReconnectDelay;

    private async Task SendHeartbeatsAsync(
        LegacyDummyPeer peer,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var lastReceive = Volatile.Read(ref _lastReceiveTimestamp);
            if (Stopwatch.GetElapsedTime(lastReceive) >=
                options.HeartbeatInterval + options.HeartbeatInterval)
            {
                throw new TimeoutException(
                    "No server packet or heartbeat acknowledgement was " +
                    "received within the liveness deadline.");
            }

            using var sendDeadline = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);
            sendDeadline.CancelAfter(options.HeartbeatInterval);
            await peer.SendAsync(
                DummyPackets.Ping(),
                sendDeadline.Token);
        }
    }

    private static async Task<byte[]> ReadThroughOpcodeAsync(
        LegacyDummyPeer peer,
        ushort terminalOpcode,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < MaximumHandshakePackets; index++)
        {
            var packet = await peer.ReadAsync(cancellationToken);
            if (ReadOpcode(packet) == terminalOpcode)
            {
                return packet;
            }
        }

        throw new InvalidDataException(
            $"Handshake did not reach opcode {terminalOpcode} within " +
            $"{MaximumHandshakePackets} packets.");
    }

    private async Task ReadThroughEnterGameAsync(
        LegacyDummyPeer peer,
        CancellationToken cancellationToken)
    {
        var observedEnterMain = false;
        for (var index = 0; index < MaximumHandshakePackets; index++)
        {
            var packet = await peer.ReadAsync(cancellationToken);
            var opcode = ReadOpcode(packet);
            if (opcode == CombatDummyHandshakeValidator.EnterMainOpcode)
            {
                CombatDummyHandshakeValidator.ValidateEnterMain(
                    definition,
                    packet);
                observedEnterMain = true;
            }

            if (opcode == Opcodes.GameServerReady)
            {
                if (!observedEnterMain)
                {
                    throw new InvalidDataException(
                        "GameServerReady arrived without a validated " +
                        "EnterMain identity.");
                }

                return;
            }
        }

        throw new InvalidDataException(
            "Enter-game handshake did not complete within the packet cap.");
    }

    private static async Task ReadThroughWorldReadyAsync(
        LegacyDummyPeer peer,
        CancellationToken cancellationToken)
    {
        var observedNpc = false;
        for (var index = 0; index < MaximumHandshakePackets; index++)
        {
            var packet = await peer.ReadAsync(cancellationToken);
            var opcode = ReadOpcode(packet);
            if (CombatDummyHandshakeValidator.ObserveWorldReady(
                    ref observedNpc,
                    packet))
            {
                return;
            }
        }

        throw new InvalidDataException(
            "World-ready handshake did not observe an NPC before the " +
            "terminal local status packet.");
    }

    internal static bool IsTerminalLocalDeath(ReadOnlySpan<byte> packet)
    {
        return packet.Length == 28 &&
            ReadOpcode(packet) == PlayerDeathOpcode &&
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(4, 4)) ==
                DummyPackets.LocalPlayerObjectId;
    }

    private static ushort ReadOpcode(ReadOnlySpan<byte> packet) =>
        BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(2, 2));

    private static string Sanitize(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');
}

internal sealed class CombatDummyTerminalDeathException()
    : InvalidOperationException(
        "Dummy reached terminal death; held the connected corpse window " +
        "before removal and free revival.");
