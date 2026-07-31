using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Godswar.Server.Networking.SemanticGateway;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SemanticGatewayChecks
{
    private static async Task CheckLegacyGameLoginProbeAsync()
    {
        var encrypted = CreateEncryptedGameLogin("test2");
        var sentinel = new byte[] { 0xA5, 0x5A, 0xC3 };
        using var listener = new TcpListener(
            IPAddress.Loopback,
            0);
        listener.Start();
        using var client = new TcpClient(AddressFamily.InterNetwork);
        var connect = client.ConnectAsync(
            (IPEndPoint)listener.LocalEndpoint);
        using var server = await listener.AcceptTcpClientAsync();
        await connect;
        client.NoDelay = true;
        server.NoDelay = true;

        var probe = LegacyGameLoginProbe.ReadAsync(
            server.GetStream(),
            TimeSpan.FromSeconds(2));
        await client.GetStream().WriteAsync(
            encrypted.AsMemory(0, 1));
        await Task.Yield();
        await client.GetStream().WriteAsync(
            encrypted.AsMemory(1, 2));
        await Task.Yield();
        var coalesced = encrypted[3..]
            .Concat(sentinel)
            .ToArray();
        await client.GetStream().WriteAsync(coalesced);

        var result = await probe;
        Check.Equal(
            "test2",
            result.Username,
            "segmented legacy game-login probe decodes username");
        Check.True(
            result.EncryptedPacket.SequenceEqual(encrypted),
            "probe preserves exact ciphertext and cipher position");
        var trailing = new byte[sentinel.Length];
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(2));
        await server.GetStream().ReadExactlyAsync(
            trailing,
            timeout.Token);
        Check.True(
            trailing.SequenceEqual(sentinel),
            "coalesced post-login ciphertext remains unread for relay");

        await CheckProbeRejectsWrongFirstPacketAsync();
    }

    private static async Task CheckProbeRejectsWrongFirstPacketAsync()
    {
        var encrypted = CreateEncryptedGameLogin(
            "TEST",
            opcode: 0x7777);
        using var listener = new TcpListener(
            IPAddress.Loopback,
            0);
        listener.Start();
        using var client = new TcpClient(AddressFamily.InterNetwork);
        var connect = client.ConnectAsync(
            (IPEndPoint)listener.LocalEndpoint);
        using var server = await listener.AcceptTcpClientAsync();
        await connect;
        var probe = LegacyGameLoginProbe.ReadAsync(
            server.GetStream(),
            TimeSpan.FromSeconds(2));
        await client.GetStream().WriteAsync(encrypted);
        try
        {
            await probe;
        }
        catch (InvalidDataException)
        {
            return;
        }

        throw new InvalidOperationException(
            "Wrong first legacy game opcode was accepted by the gateway.");
    }

    private static byte[] CreateEncryptedGameLogin(
        string rawUsername,
        ushort opcode = Opcodes.LoginGameServer)
    {
        var packet = new byte[36];
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet.AsSpan(2),
            opcode);
        PacketText.WriteFixedAscii(
            packet.AsSpan(4, 32),
            rawUsername);
        new PacketCipher().Transform(packet);
        return packet;
    }
}
