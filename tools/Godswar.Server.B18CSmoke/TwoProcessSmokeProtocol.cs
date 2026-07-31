using System.Buffers.Binary;
using System.Security.Cryptography;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.B18CSmoke;

internal static class TwoProcessSmokeProtocol
{
    public static async Task<LegacySmokePeer> OpenRoundAsync(
        SmokeEndpoints endpoints,
        string loginName,
        string username,
        byte[] password,
        CancellationToken cancellationToken)
    {
        using var roundTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        roundTimeout.CancelAfter(TimeSpan.FromSeconds(8));
        var token = roundTimeout.Token;

        await using (var login = await LegacySmokePeer.ConnectAsync(
            endpoints.RelayLoginPort,
            token))
        {
            var loginPacket = SmokePackets.Login(loginName, password);
            try
            {
                await login.SendAsync(loginPacket, token);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(loginPacket);
            }

            await AssertPacketStreamAsync(
                login,
                PacketBuilder.ServerList(),
                "login response",
                token);
            await login.SendAsync(
                SmokePackets.Opcode(Opcodes.SelectServer),
                token);
            RequirePacket(
                await ReadPacketAsync(
                    login,
                    "server selection response",
                    token),
                PacketBuilder.SendServer(),
                "server selection response");
            await login.SendAsync(
                SmokePackets.Opcode(Opcodes.LoginReturnInfo),
                token);
            var redirect = await ReadPacketAsync(
                login,
                "public relay redirect",
                token);
            AssertPublicRedirect(redirect, endpoints);
        }

        var game = await LegacySmokePeer.ConnectAsync(
            endpoints.RelayGamePort,
            token);
        try
        {
            await game.SendAsync(
                SmokePackets.GameLogin(username),
                token);
            await AssertPacketStreamAsync(
                game,
                PacketBuilder.AfterLogin(),
                "AfterLogin response",
                token);
            RequirePacket(
                await ReadPacketAsync(
                    game,
                    "blank character response",
                    token),
                PacketBuilder.BlankUser(),
                "blank character response");
            return game;
        }
        catch
        {
            await game.DisposeAsync();
            throw;
        }
    }

    public static async Task RequireActiveConnectionClosedAsync(
        LegacySmokePeer game,
        CancellationToken cancellationToken)
    {
        using var closeTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        closeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await game.WaitForRemoteCloseAsync(closeTimeout.Token);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The active relayed game connection survived the worker stop.");
        }
    }

    private static void AssertPublicRedirect(
        byte[] redirect,
        SmokeEndpoints endpoints)
    {
        if (redirect.Length < 44)
        {
            throw new InvalidDataException(
                "The login redirect was shorter than its route fields.");
        }

        var advertisedPort = BinaryPrimitives.ReadInt32LittleEndian(
            redirect.AsSpan(40, 4));
        if (advertisedPort == endpoints.WorkerGamePort)
        {
            throw new InvalidDataException(
                "The login redirect exposed the private worker game port.");
        }
        if (advertisedPort != endpoints.RelayGamePort)
        {
            throw new InvalidDataException(
                "The login redirect did not advertise the public relay game port.");
        }

        RequirePacket(
            redirect,
            PacketBuilder.GameServerRedirect(
                "127.0.0.1",
                endpoints.RelayGamePort),
            "public relay redirect");
    }

    private static async Task AssertPacketStreamAsync(
        LegacySmokePeer peer,
        byte[] expected,
        string description,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < expected.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                expected.AsSpan(offset, 2));
            if (length < 4 || offset + length > expected.Length)
            {
                throw new InvalidDataException(
                    "A server packet fixture is malformed.");
            }

            RequirePacket(
                await ReadPacketAsync(
                    peer,
                    description,
                    cancellationToken),
                expected.AsSpan(offset, length),
                description);
            offset += length;
        }
    }

    private static void RequirePacket(
        ReadOnlySpan<byte> actual,
        ReadOnlySpan<byte> expected,
        string description)
    {
        if (!actual.SequenceEqual(expected))
        {
            throw new InvalidDataException(
                $"The {description} did not match the server fixture.");
        }
    }

    private static async Task<byte[]> ReadPacketAsync(
        LegacySmokePeer peer,
        string description,
        CancellationToken cancellationToken)
    {
        try
        {
            return await peer.ReadAsync(cancellationToken);
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException(
                $"The relay closed before the {description} completed.",
                error);
        }
    }
}
