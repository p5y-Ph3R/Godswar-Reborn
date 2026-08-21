using System.Buffers.Binary;
using System.Net.Sockets;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Networking.SemanticGateway;

internal sealed record LegacyGameLoginProbeResult(
    string Username,
    string Identifier,
    RealmId RealmId,
    byte[] EncryptedPacket);

/// <summary>
/// Reads exactly the first legacy game-login packet. Decryption is performed
/// only on a copy; the untouched encrypted bytes are forwarded so the
/// authoritative worker starts with the original cipher state.
/// </summary>
internal static class LegacyGameLoginProbe
{
    public static async Task<LegacyGameLoginProbeResult> ReadAsync(
        NetworkStream stream,
        TimeSpan timeout,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (timeout <= TimeSpan.Zero ||
            timeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var clock = timeProvider ?? TimeProvider.System;
        using var deadline = new CancellationTokenSource(
            timeout,
            clock);
        using var lifetime =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                deadline.Token);
        var encryptedHeader = new byte[2];
        try
        {
            await ReadExactlyAsync(
                stream,
                encryptedHeader,
                lifetime.Token);
            var cipher = new PacketCipher();
            var clearHeader = encryptedHeader.ToArray();
            cipher.Transform(clearHeader);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(
                clearHeader);
            if (length < 4 ||
                length > LegacyProtocolLimits.MaxPacketLength)
            {
                throw new InvalidDataException(
                    "The first game packet has an invalid bounded length.");
            }

            var encryptedPacket = new byte[length];
            encryptedHeader.CopyTo(encryptedPacket, 0);
            try
            {
                await ReadExactlyAsync(
                    stream,
                    encryptedPacket.AsMemory(2),
                    lifetime.Token);
                var clearBody = encryptedPacket.AsSpan(2).ToArray();
                try
                {
                    cipher.Transform(clearBody);
                    var opcode =
                        BinaryPrimitives.ReadUInt16LittleEndian(
                            clearBody);
                    if (opcode != Opcodes.LoginGameServer)
                    {
                        throw new InvalidDataException(
                            "The first game packet must be LoginGameServer.");
                    }

                    var clearPacket = new byte[length];
                    clearHeader.CopyTo(clearPacket, 0);
                    clearBody.CopyTo(clearPacket, 2);
                    LegacyGameLoginIdentity? identity;
                    try
                    {
                        if (!LegacyGameLoginPacket.TryRead(
                                new GamePacket(clearPacket),
                                out identity) ||
                            identity is null ||
                            identity.Username.Length >
                                SemanticGatewayPrincipal
                                    .MaximumUsernameLength)
                        {
                            throw new InvalidDataException(
                                "The first game packet has no valid realm identity.");
                        }
                    }
                    finally
                    {
                        Array.Clear(clearPacket);
                    }

                    return new LegacyGameLoginProbeResult(
                        identity.Username,
                        identity.Identifier,
                        identity.RealmId,
                        encryptedPacket);
                }
                finally
                {
                    Array.Clear(clearBody);
                }
            }
            catch
            {
                Array.Clear(encryptedPacket);
                throw;
            }
            finally
            {
                Array.Clear(clearHeader);
            }
        }
        catch (OperationCanceledException)
            when (deadline.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The semantic gateway game-login probe timed out.");
        }
        finally
        {
            Array.Clear(encryptedHeader);
        }
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(
                destination[offset..],
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "The client closed during the first game packet.");
            }

            offset += read;
        }
    }
}
