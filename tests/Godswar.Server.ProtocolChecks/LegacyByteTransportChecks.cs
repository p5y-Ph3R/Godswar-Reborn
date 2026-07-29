using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Godswar.Server.Game;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class LegacyByteTransportChecks
{
    private const string GoldenCipherSha256 =
        "6920EFDAB42B41C9F1726C79C3E9B5EAA15BA490D4D135C6A650BDDF9607227F";

    private const string CapturedBootstrapClearSha256 =
        "AD4125D3F759C969487EC5C89EA3AB2D41646D073AB4BBAA88D1B3082C95EB84";

    private const string CapturedBootstrapCipherSha256 =
        "D31B039946DFB8D26AA0D0DBA8BA29659E73122CA42A20B380FF87986FC808AE";

    private static readonly byte[] GoldenCipherStream = Convert.FromHexString(
        "249ABD36DDE26636F661AC93CA9D30EFFED9340B1215D8270651BCC35A0D805F" +
        "CE09843B2285289716C1CC73EA7D50CF9EB9D46B32F578872631DC237A6D20BF" +
        "EEE9A49B426548773621EC530A5D70AF3E9974CB52D518E74611FC839ACDC01F" +
        "0EC9C4FB6245685756810C332A3D908FDE79142B72B5B84766F11CE3BA2D607" +
        "F2EA9E45B8225883776E12C134A1DB06F7E59B48B929558A786D13C43DA8D00" +
        "DF4E8904BBA205A81796414CF36AFDD04F1E3954EBB275F807A6B15CA3FAEDA" +
        "03F6E69241BC2E5C8F7B6A16CD38ADDF02FBE19F44BD2559867C6917C031A4D" +
        "409F8E49447BE2C5E8D7D6018CB3AABD100F5EF994ABF23538C7E6719C633AA" +
        "DE0FFAEB1BC253025883776E12C134A1DB06F7E59B48B929558A786D13C43DA8" +
        "D00DF4E8904BBA205A81796414CF3");

    public static async Task RunAsync()
    {
        CheckIndependentGoldenVector();
        await CheckCapturedGameBootstrapAsync();
        await CheckFragmentedAndCoalescedReadsAsync();
        await CheckLoginHandlerLoopAsync();
        await CheckLegacyEofBoundariesAsync();
        await CheckPacketLengthBoundsAsync();
        await CheckSequentialAndArbitraryWritesAsync();
        await CheckSessionDelegationAsync();
        await CheckRawTcpAdapterAsync();
    }

    private static void CheckIndependentGoldenVector()
    {
        var clearStream = CreateGoldenClearStream();
        Check.Equal(300, clearStream.Length, "golden clear stream crosses XOR wrap");
        Check.Equal(300, GoldenCipherStream.Length, "golden cipher stream length");
        Check.Equal(
            GoldenCipherSha256,
            Convert.ToHexString(SHA256.HashData(GoldenCipherStream)),
            "fixed golden cipher SHA-256");

        var encrypted = (byte[])clearStream.Clone();
        new PacketCipher().Transform(encrypted);
        Check.True(
            encrypted.SequenceEqual(GoldenCipherStream),
            "legacy cipher remains byte-identical to fixed golden stream");
    }

    private static async Task CheckCapturedGameBootstrapAsync()
    {
        var capturedClear = PacketBuilder.AfterLogin();
        Check.Equal(2772, capturedClear.Length, "captured game bootstrap length");
        Check.Equal(
            CapturedBootstrapClearSha256,
            Convert.ToHexString(SHA256.HashData(capturedClear)),
            "captured game bootstrap clear SHA-256");

        var transport = new ScriptedLegacyByteTransport();
        await using (var session = new ClientSession(transport))
        {
            await session.SendAsync(capturedClear, CancellationToken.None);
        }

        Check.Equal(
            CapturedBootstrapCipherSha256,
            Convert.ToHexString(SHA256.HashData(transport.WrittenBytes)),
            "captured game bootstrap raw transport SHA-256");
    }

    private static async Task CheckFragmentedAndCoalescedReadsAsync()
    {
        await CheckInboundGoldenStreamAsync(
            [1],
            "one-byte transport fragmentation");
        await CheckInboundGoldenStreamAsync(
            [int.MaxValue],
            "coalesced transport input");
        await CheckInboundGoldenStreamAsync(
            [2, 17, 251, 1, 29],
            "mixed transport fragmentation");
    }

    private static async Task CheckInboundGoldenStreamAsync(
        int[] readChunks,
        string description)
    {
        var clearPackets = CreateGoldenPackets();
        var transport = new ScriptedLegacyByteTransport(
            GoldenCipherStream,
            readChunks);

        await using (var session = new ClientSession(transport))
        {
            foreach (var expected in clearPackets)
            {
                var packet = await session.ReadPacketAsync(CancellationToken.None);
                Check.True(packet is not null, $"{description} returns packet");
                Check.True(
                    packet!.Buffer.SequenceEqual(expected),
                    $"{description} preserves packet bytes");
            }

            Check.True(
                await session.ReadPacketAsync(CancellationToken.None) is null,
                $"{description} reports EOF after complete packets");
        }

        Check.True(transport.IsDisposed, $"{description} disposes transport");
    }

    private static async Task CheckLoginHandlerLoopAsync()
    {
        var login = CreatePacket(68, Opcodes.Login, 0);
        login.AsSpan(4).Clear();
        PacketText.WriteFixedAscii(login.AsSpan(4, 32), "test2");
        PacketText.WriteFixedAscii(login.AsSpan(36, 32), "password");
        var input = new[]
        {
            login,
            CreatePacket(4, Opcodes.SelectServer, 0),
            CreatePacket(4, Opcodes.LoginReturnInfo, 0)
        }.SelectMany(static packet => packet).ToArray();
        var transport = new ScriptedLegacyByteTransport(
            Encrypt(input),
            [1, int.MaxValue, 2]);
        var options = new ServerOptions();
        options.RuntimeProfile = "LocalDevelopment";
        options.Storage.Provider = "Json";
        var legacyAuthenticationAccess =
            LegacyAuthenticationAccess.Create(
                ServerRuntimeProfilePolicy.Validate(options));

        await using (var session = new ClientSession(transport))
        {
            var handler = new LoginClientHandler(
                session,
                new LoginGameStore(),
                options,
                legacyAuthenticationAccess:
                    legacyAuthenticationAccess);
            await handler.RunAsync(CancellationToken.None);
        }

        var expectedClear = PacketBuilder.ServerList()
            .Concat(PacketBuilder.SendServer())
            .Concat(PacketBuilder.GameServerRedirect(
                options.Game.PublicHost,
                options.Game.Port))
            .ToArray();
        Check.True(
            transport.WrittenBytes.SequenceEqual(Encrypt(expectedClear)),
            "transport framing dispatches login opcodes and ordered responses");
        Check.Equal(
            3,
            transport.WriteCount,
            "login handler authenticates before server selection and redirect");
    }

    private static async Task CheckLegacyEofBoundariesAsync()
    {
        await CheckReadReturnsNullAsync(
            [],
            "EOF before header");
        await CheckReadReturnsNullAsync(
            GoldenCipherStream.AsSpan(0, 2).ToArray(),
            "EOF before first body byte");
        await CheckReadThrowsAsync<EndOfStreamException>(
            GoldenCipherStream.AsSpan(0, 1).ToArray(),
            "EOF inside header");
        await CheckReadThrowsAsync<EndOfStreamException>(
            GoldenCipherStream.AsSpan(0, 3).ToArray(),
            "EOF after first body byte");
    }

    private static async Task CheckReadReturnsNullAsync(
        byte[] inbound,
        string description)
    {
        var transport = new ScriptedLegacyByteTransport(inbound, [1]);
        await using var session = new ClientSession(transport);
        Check.True(
            await session.ReadPacketAsync(CancellationToken.None) is null,
            description);
    }

    private static async Task CheckReadThrowsAsync<TException>(
        byte[] inbound,
        string description)
        where TException : Exception
    {
        var transport = new ScriptedLegacyByteTransport(inbound, [1]);
        await using var session = new ClientSession(transport);
        await ExpectThrowsAsync<TException>(
            () => session.ReadPacketAsync(CancellationToken.None),
            description);
    }

    private static async Task CheckPacketLengthBoundsAsync()
    {
        await CheckInvalidLengthAsync(0);
        await CheckInvalidLengthAsync(1);
        await CheckInvalidLengthAsync(2);
        await CheckInvalidLengthAsync(3);
        await CheckInvalidLengthAsync(8197);
        await CheckInvalidLengthAsync(ushort.MaxValue);

        var maximumPacket = CreatePacket(8196, 0x7070, 0x44);
        var encryptedMaximum = Encrypt(maximumPacket);
        var transport = new ScriptedLegacyByteTransport(
            encryptedMaximum,
            [1, 2, 31, 257]);
        await using var session = new ClientSession(transport);
        var decoded = await session.ReadPacketAsync(CancellationToken.None);
        Check.True(decoded is not null, "maximum legacy packet is accepted");
        Check.True(
            decoded!.Buffer.SequenceEqual(maximumPacket),
            "maximum legacy packet remains byte-identical");
    }

    private static async Task CheckInvalidLengthAsync(ushort length)
    {
        var clearHeader = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(clearHeader, length);
        var transport = new ScriptedLegacyByteTransport(
            Encrypt(clearHeader),
            [1]);
        await using var session = new ClientSession(transport);
        await ExpectThrowsAsync<InvalidDataException>(
            () => session.ReadPacketAsync(CancellationToken.None),
            $"legacy packet length {length} rejects");
    }

    private static async Task CheckSequentialAndArbitraryWritesAsync()
    {
        var clearPackets = CreateGoldenPackets();
        var packetTransport = new ScriptedLegacyByteTransport();
        await using (var session = new ClientSession(packetTransport))
        {
            foreach (var packet in clearPackets)
            {
                await session.SendAsync(packet, CancellationToken.None);
            }
        }

        Check.True(
            packetTransport.WrittenBytes.SequenceEqual(GoldenCipherStream),
            "sequential packet writes match fixed cipher stream");
        Check.Equal(3, packetTransport.WriteCount, "one write per packet");

        var clearStream = CreateGoldenClearStream();
        var chunkTransport = new ScriptedLegacyByteTransport();
        await using (var session = new ClientSession(chunkTransport))
        {
            var offset = 0;
            foreach (var length in new[] { 1, 2, 252, 1, 44 })
            {
                await session.SendAsync(
                    clearStream.AsMemory(offset, length),
                    CancellationToken.None,
                    framed: false);
                offset += length;
            }
        }

        Check.True(
            chunkTransport.WrittenBytes.SequenceEqual(GoldenCipherStream),
            "arbitrary write chunks preserve continuous cipher state");
        Check.Equal(5, chunkTransport.WriteCount, "one flushed write per stream chunk");
        Check.Equal(1, chunkTransport.MaximumConcurrentWrites, "writes remain serialized");
    }

    private static async Task CheckSessionDelegationAsync()
    {
        var transport = new ScriptedLegacyByteTransport(
            remoteEndPoint: "fixture:4321");
        var session = new ClientSession(transport);
        Check.Equal("fixture:4321", session.RemoteEndPoint, "endpoint delegates");

        session.Disconnect();
        session.Disconnect();
        Check.Equal(1, transport.DisconnectCount, "disconnect is idempotent");

        await session.DisposeAsync();
        Check.True(transport.IsDisposed, "session disposes owned transport");
    }

    private static async Task CheckRawTcpAdapterAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cancellationToken = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var outbound = new TcpClient();
            var acceptTask = listener.AcceptTcpClientAsync(cancellationToken).AsTask();
            await outbound.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            using var inbound = await acceptTask;
            var transport = new RawTcpLegacyTransport(outbound);

            Check.True(outbound.NoDelay, "raw TCP adapter enables NoDelay");
            Check.True(
                transport.RemoteEndPoint != "unknown",
                "raw TCP adapter captures endpoint");
            var capturedEndPoint = transport.RemoteEndPoint;

            var peerBytes = new byte[] { 9, 8, 7, 6, 5 };
            await inbound.GetStream().WriteAsync(peerBytes, cancellationToken);
            var readBytes = new byte[peerBytes.Length];
            var readOffset = 0;
            while (readOffset < readBytes.Length)
            {
                var read = await transport.ReadAsync(
                    readBytes.AsMemory(readOffset, 1),
                    cancellationToken);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"Raw TCP adapter closed after {readOffset} bytes.");
                }

                readOffset += read;
            }
            Check.True(
                readBytes.SequenceEqual(peerBytes),
                "raw TCP adapter preserves partial reads");

            var serverBytes = new byte[] { 1, 3, 5, 7, 9, 11 };
            await transport.WriteAsync(serverBytes, cancellationToken);
            var received = await ReadExactlyAsync(
                inbound.GetStream(),
                serverBytes.Length,
                cancellationToken);
            Check.True(
                received.SequenceEqual(serverBytes),
                "raw TCP adapter writes and flushes every byte");

            transport.Disconnect();
            transport.Disconnect();
            Check.Equal(
                capturedEndPoint,
                transport.RemoteEndPoint,
                "raw TCP endpoint remains stable after disconnect");
            Check.Equal(
                0,
                await inbound.GetStream().ReadAsync(new byte[1], cancellationToken),
                "raw TCP disconnect closes the peer stream");

            var firstDispose = transport.DisposeAsync().AsTask();
            var concurrentDispose = transport.DisposeAsync().AsTask();
            Check.True(
                ReferenceEquals(firstDispose, concurrentDispose),
                "raw TCP concurrent disposal shares completion");
            await Task.WhenAll(firstDispose, concurrentDispose);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static byte[][] CreateGoldenPackets()
    {
        return
        [
            CreatePacket(4, 0x1001, 0x11),
            CreatePacket(253, 0x1002, 0x22),
            CreatePacket(43, 0x1003, 0x33)
        ];
    }

    private static byte[] CreateGoldenClearStream()
    {
        return CreateGoldenPackets().SelectMany(static packet => packet).ToArray();
    }

    private static byte[] CreatePacket(
        ushort length,
        ushort opcode,
        byte seed)
    {
        var packet = new byte[length];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, length);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), opcode);
        for (var offset = 4; offset < packet.Length; offset++)
        {
            packet[offset] = (byte)(seed + offset * 37);
        }

        return packet;
    }

    private static byte[] Encrypt(byte[] clear)
    {
        var encrypted = (byte[])clear.Clone();
        new PacketCipher().Transform(encrypted);
        return encrypted;
    }

    private static async Task<byte[]> ReadExactlyAsync(
        NetworkStream stream,
        int count,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[count];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(
                bytes.AsMemory(offset),
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Raw TCP peer closed after {offset} of {count} bytes.");
            }

            offset += read;
        }

        return bytes;
    }

    private static async Task ExpectThrowsAsync<TException>(
        Func<Task> action,
        string description)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Assertion failed: {description}; expected {typeof(TException).Name}.");
    }

    private sealed class LoginGameStore : GameStoreTestStub
    {
        public override Task<GameAccount> LoginOrCreateAccountAsync(
            string username,
            string password,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GameAccount
            {
                Id = 7,
                Username = username
            });
        }
    }
}
