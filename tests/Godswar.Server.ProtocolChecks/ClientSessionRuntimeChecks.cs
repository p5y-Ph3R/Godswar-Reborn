using System.Buffers.Binary;
using Godswar.Server.Networking;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class ClientSessionRuntimeChecks
{
    public static async Task RunAsync()
    {
        await CheckPhysicalWriteCompletionAndOrderingAsync();
        await CheckPendingAdmissionBurstIsBoundedAsync();
        await CheckQueueAdmissionDeadlineDisconnectsAsync();
        await CheckSlowSessionIsolationAsync();
        await CheckReliableWriteDeadlineAsync();
        await CheckFirstPacketAndIdleDeadlinesAsync();
        await CheckFirstPacketDeadlineIsAbsoluteAsync();
        await CheckHeaderDeadlineAsync();
        await CheckBodyDeadlineIsAbsoluteAsync();
        await CheckConcurrentDisconnectAndDisposeAsync();
    }

    private static async Task CheckPhysicalWriteCompletionAndOrderingAsync()
    {
        var transport = new ControlledLegacyByteTransport(blockWrites: true);
        var options = CreateOptions();
        options.ReliableEgressQueueItems = 2;
        options.ReliableEgressQueueBytes =
            LegacyProtocolLimits.MaxPacketLength;

        await using var session = new ClientSession(
            transport,
            options,
            NetworkEndpointRole.Game);
        var firstClear = CreatePacket(0x3001, 0x11);
        var firstOriginal = (byte[])firstClear.Clone();
        var secondClear = CreatePacket(0x3002, 0x22);

        var firstSend = session.SendAsync(firstClear, CancellationToken.None);
        await transport.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        firstClear.AsSpan().Fill(0xFF);
        var secondSend = session.SendAsync(secondClear, CancellationToken.None);
        await Task.Yield();

        Check.True(
            !firstSend.IsCompleted && !secondSend.IsCompleted,
            "SendAsync completes only after its physical transport write");
        transport.ReleaseWrites();
        await Task.WhenAll(firstSend, secondSend)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Check.Equal(2, transport.WriteCount, "each reliable item is physically written once");
        Check.Equal(
            1,
            transport.MaximumConcurrentWrites,
            "reliable transport writes remain serialized");
        Check.True(
            transport.WrittenBytes.SequenceEqual(
                EncryptInOrder(firstOriginal, secondClear)),
            "bounded reliable egress preserves FIFO cipher order and owns input bytes");
    }

    private static async Task CheckQueueAdmissionDeadlineDisconnectsAsync()
    {
        var time = new ManualTimeProvider();
        var transport = new ControlledLegacyByteTransport(blockWrites: true);
        var options = CreateOptions();
        options.ReliableEgressQueueItems = 1;
        options.ReliableEgressQueueBytes =
            LegacyProtocolLimits.MaxPacketLength;
        options.QueueAdmissionTimeoutMilliseconds = 1_000;
        options.ReliableWriteTimeoutMilliseconds = 10_000;

        await using var session = new ClientSession(
            transport,
            options,
            NetworkEndpointRole.Game,
            time);
        var firstSend = session.SendAsync(
            CreatePacket(0x3101, 0x31),
            CancellationToken.None);
        await transport.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var secondSend = session.SendAsync(
            CreatePacket(0x3102, 0x32),
            CancellationToken.None);
        await Task.Yield();
        var overflowedSend = session.SendAsync(
            CreatePacket(0x3103, 0x33),
            CancellationToken.None);
        await Task.Yield();

        time.Advance(options.QueueAdmissionTimeout);
        await ExpectExceptionAsync<ReliableQueueOverflowException>(
            overflowedSend,
            "reliable queue admission timeout surfaces an explicit overflow")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Check.Equal(
            1,
            transport.DisconnectCount,
            "reliable queue overload terminates the affected session");
        await ObserveTerminalFailureAsync(firstSend);
        await ObserveTerminalFailureAsync(secondSend);
    }

    private static async Task CheckPendingAdmissionBurstIsBoundedAsync()
    {
        var transport = new ControlledLegacyByteTransport(blockWrites: true);
        var options = CreateOptions();
        options.ReliableEgressQueueItems = 1;
        options.ReliableEgressQueueBytes =
            LegacyProtocolLimits.MaxPacketLength;
        options.ReliableEgressPendingItems = 1;
        options.ReliableEgressPendingBytes =
            LegacyProtocolLimits.MaxPacketLength;
        options.ReliableWriteTimeoutMilliseconds = 10_000;

        await using var session = new ClientSession(
            transport,
            options,
            NetworkEndpointRole.Game);
        var first = session.SendAsync(
            CreatePacket(0x3151, 0x31),
            CancellationToken.None);
        await transport.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = session.SendAsync(
            CreatePacket(0x3152, 0x32),
            CancellationToken.None);
        var waiting = session.SendAsync(
            CreatePacket(0x3153, 0x33),
            CancellationToken.None);
        var excess = session.SendAsync(
            CreatePacket(0x3154, 0x34),
            CancellationToken.None);

        await ExpectExceptionAsync<ReliableQueueOverflowException>(
            excess,
            "pending reliable producers are rejected at a finite item bound")
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check.Equal(
            1,
            transport.DisconnectCount,
            "a bounded pending-admission burst terminates only its session");
        await ObserveTerminalFailureAsync(first);
        await ObserveTerminalFailureAsync(queued);
        await ObserveTerminalFailureAsync(waiting);
    }

    private static async Task CheckSlowSessionIsolationAsync()
    {
        var blockedTransport =
            new ControlledLegacyByteTransport(blockWrites: true);
        var healthyTransport = new ControlledLegacyByteTransport();
        var options = CreateOptions();
        await using var blockedSession = new ClientSession(
            blockedTransport,
            options,
            NetworkEndpointRole.Game);
        await using var healthySession = new ClientSession(
            healthyTransport,
            options,
            NetworkEndpointRole.Game);

        var blockedSend = blockedSession.SendAsync(
            CreatePacket(0x3181, 0x41),
            CancellationToken.None);
        await blockedTransport.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await healthySession.SendAsync(
            CreatePacket(0x3182, 0x42),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Check.True(
            !blockedSend.IsCompleted,
            "one blocked session remains backpressured");
        Check.Equal(
            1,
            healthyTransport.WriteCount,
            "an independent healthy session completes its physical write");
        Check.Equal(
            0,
            healthyTransport.DisconnectCount,
            "slow-session backpressure does not disconnect a healthy session");

        blockedSession.Disconnect();
        await ObserveTerminalFailureAsync(blockedSend);
    }

    private static async Task CheckReliableWriteDeadlineAsync()
    {
        var time = new ManualTimeProvider();
        var transport = new ControlledLegacyByteTransport(blockWrites: true);
        var options = CreateOptions();
        options.ReliableWriteTimeoutMilliseconds = 1_000;

        await using var session = new ClientSession(
            transport,
            options,
            NetworkEndpointRole.Login,
            time);
        var send = session.SendAsync(
            CreatePacket(0x3201, 0x44),
            CancellationToken.None);
        await transport.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));

        time.Advance(options.ReliableWriteTimeout);
        var error = await ExpectExceptionAsync<NetworkDeadlineException>(
            send,
            "blocked reliable write observes its absolute deadline")
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(
            error.Stage == NetworkTimeoutStage.ReliableWrite,
            "write deadline reports the reliable-write stage");
        Check.Equal(
            1,
            transport.DisconnectCount,
            "write deadline disconnects only its owning session");
    }

    private static async Task CheckFirstPacketAndIdleDeadlinesAsync()
    {
        var firstTime = new ManualTimeProvider();
        var firstTransport = new ControlledLegacyByteTransport();
        var options = CreateOptions();
        await using (var firstSession = new ClientSession(
                         firstTransport,
                         options,
                         NetworkEndpointRole.Game,
                         firstTime))
        {
            var firstRead = firstSession.ReadPacketAsync(CancellationToken.None);
            await firstTransport.WaitForReadCallsAsync(1);
            firstTime.Advance(options.FirstPacketTimeout);
            var error = await ExpectExceptionAsync<NetworkDeadlineException>(
                firstRead,
                "silent new connection observes the first-packet deadline")
                .WaitAsync(TimeSpan.FromSeconds(5));
            Check.True(
                error.Stage == NetworkTimeoutStage.FirstPacket,
                "new connection timeout reports the first-packet stage");
        }

        var idleTime = new ManualTimeProvider();
        var idleTransport = new ControlledLegacyByteTransport();
        idleTransport.QueueInbound(EncryptInOrder(CreatePacket(0x3301, 0x55)));
        await using var idleSession = new ClientSession(
            idleTransport,
            options,
            NetworkEndpointRole.Game,
            idleTime);
        Check.True(
            await idleSession.ReadPacketAsync(CancellationToken.None) is not null,
            "complete packet transitions a connection to idle tracking");

        var idleRead = idleSession.ReadPacketAsync(CancellationToken.None);
        await idleTransport.WaitForReadCallsAsync(4);
        idleTime.Advance(options.IdleTimeout);
        var idleError = await ExpectExceptionAsync<NetworkDeadlineException>(
            idleRead,
            "silent established connection observes the idle deadline")
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(
            idleError.Stage == NetworkTimeoutStage.Idle,
            "established connection timeout reports the idle stage");
    }

    private static async Task CheckHeaderDeadlineAsync()
    {
        var time = new ManualTimeProvider();
        var transport = new ControlledLegacyByteTransport();
        var options = CreateOptions();
        options.FirstPacketTimeoutMilliseconds = 10_000;
        var encrypted = EncryptInOrder(CreatePacket(0x3401, 0x66));
        transport.QueueInbound(encrypted.AsSpan(0, 1));

        await using var session = new ClientSession(
            transport,
            options,
            NetworkEndpointRole.Game,
            time);
        var read = session.ReadPacketAsync(CancellationToken.None);
        await transport.WaitForReadCallsAsync(2);
        time.Advance(options.PacketHeaderTimeout);

        var error = await ExpectExceptionAsync<NetworkDeadlineException>(
            read,
            "partial packet header observes an absolute header deadline")
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(
            error.Stage == NetworkTimeoutStage.PacketHeader,
            "partial header timeout reports the header stage");
    }

    private static async Task CheckFirstPacketDeadlineIsAbsoluteAsync()
    {
        var time = new ManualTimeProvider();
        var transport = new ControlledLegacyByteTransport();
        var options = CreateOptions();
        options.FirstPacketTimeoutMilliseconds = 1_000;
        options.PacketHeaderTimeoutMilliseconds = 10_000;
        options.PacketBodyTimeoutMilliseconds = 10_000;
        var encrypted = EncryptInOrder(CreatePacket(0x3451, 0x70));
        transport.QueueInbound(encrypted.AsSpan(0, 2));

        await using var session = new ClientSession(
            transport,
            options,
            NetworkEndpointRole.Game,
            time);
        var read = session.ReadPacketAsync(CancellationToken.None);
        await transport.WaitForReadCallsAsync(3);

        time.Advance(TimeSpan.FromMilliseconds(500));
        transport.QueueInbound(encrypted.AsSpan(2, 1));
        await transport.WaitForReadCallsAsync(4);
        time.Advance(TimeSpan.FromMilliseconds(500));

        var error = await ExpectExceptionAsync<NetworkDeadlineException>(
            read,
            "partial credentials do not reset the first-packet deadline")
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(
            error.Stage == NetworkTimeoutStage.FirstPacket,
            "incomplete initial packet reports the first-packet stage");
    }

    private static async Task CheckBodyDeadlineIsAbsoluteAsync()
    {
        var time = new ManualTimeProvider();
        var transport = new ControlledLegacyByteTransport();
        var options = CreateOptions();
        options.FirstPacketTimeoutMilliseconds = 10_000;
        options.PacketBodyTimeoutMilliseconds = 1_000;
        var encrypted = EncryptInOrder(CreatePacket(0x3501, 0x77));
        transport.QueueInbound(encrypted.AsSpan(0, 2));

        await using var session = new ClientSession(
            transport,
            options,
            NetworkEndpointRole.Game,
            time);
        var read = session.ReadPacketAsync(CancellationToken.None);
        await transport.WaitForReadCallsAsync(3);

        time.Advance(TimeSpan.FromMilliseconds(500));
        transport.QueueInbound(encrypted.AsSpan(2, 1));
        await transport.WaitForReadCallsAsync(4);
        time.Advance(TimeSpan.FromMilliseconds(500));

        var error = await ExpectExceptionAsync<NetworkDeadlineException>(
            read,
            "partial body progress does not reset its absolute deadline")
            .WaitAsync(TimeSpan.FromSeconds(5));
        Check.True(
            error.Stage == NetworkTimeoutStage.PacketBody,
            "absolute body timeout reports the body stage");
    }

    private static async Task CheckConcurrentDisconnectAndDisposeAsync()
    {
        var transport = new ControlledLegacyByteTransport(blockWrites: true);
        var options = CreateOptions();
        var session = new ClientSession(
            transport,
            options,
            NetworkEndpointRole.Game);
        var send = session.SendAsync(
            CreatePacket(0x3581, 0x78),
            CancellationToken.None);
        await transport.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var dispose = session.DisposeAsync().AsTask();
        var disconnects = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(session.Disconnect))
            .ToArray();
        await Task.WhenAll(disconnects);
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        await ObserveTerminalFailureAsync(send);

        Check.Equal(
            1,
            transport.DisconnectCount,
            "concurrent disconnect and disposal close the transport once");
    }

    private static NetworkRuntimeOptions CreateOptions()
    {
        return new NetworkRuntimeOptions
        {
            QueueAdmissionTimeoutMilliseconds = 1_000,
            FirstPacketTimeoutMilliseconds = 1_000,
            PacketHeaderTimeoutMilliseconds = 1_000,
            PacketBodyTimeoutMilliseconds = 1_000,
            ReliableWriteTimeoutMilliseconds = 1_000,
            IdleTimeoutMilliseconds = 1_000
        };
    }

    private static byte[] CreatePacket(ushort opcode, byte payload)
    {
        var packet = new byte[5];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)packet.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), opcode);
        packet[4] = payload;
        return packet;
    }

    private static byte[] EncryptInOrder(params byte[][] clearChunks)
    {
        var cipher = new PacketCipher();
        using var output = new MemoryStream();
        foreach (var clearChunk in clearChunks)
        {
            var encrypted = (byte[])clearChunk.Clone();
            cipher.Transform(encrypted);
            output.Write(encrypted);
        }

        return output.ToArray();
    }

    private static async Task<TException> ExpectExceptionAsync<TException>(
        Task task,
        string description)
        where TException : Exception
    {
        try
        {
            await task;
        }
        catch (TException error)
        {
            return error;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected {typeof(TException).Name}.");
    }

    private static async Task ObserveTerminalFailureAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // The overload deliberately fails all admitted work for this
            // session; observing it prevents an unobserved task exception.
        }
    }
}
