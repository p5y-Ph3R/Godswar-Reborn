using System.Buffers.Binary;
using System.Security.Cryptography;
using Godswar.Server.Networking.Secure;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.SecureSmoke;

internal static class SmokePackets
{
    public static byte[] Login(
        string username,
        ReadOnlySpan<byte> password)
    {
        if (password.Length is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(password));
        }

        var packet = new byte[68];
        WriteHeader(packet, Opcodes.Login);
        PacketText.WriteFixedAscii(packet.AsSpan(4, 32), username);
        password.CopyTo(packet.AsSpan(36, 32));
        return packet;
    }

    public static byte[] GameLogin(string username)
    {
        var packet = new byte[36];
        WriteHeader(packet, Opcodes.LoginGameServer);
        PacketText.WriteFixedAscii(packet.AsSpan(4, 32), username);
        return packet;
    }

    public static byte[] Opcode(ushort opcode)
    {
        var packet = new byte[4];
        WriteHeader(packet, opcode);
        return packet;
    }

    public static byte[] GameBind(SecureGameGrant grant)
    {
        var grantId = new byte[
            SecureProtocolConstants.GrantIdBytes];
        var ticket = new byte[
            SecureProtocolConstants.TicketBytes];
        var payload = new byte[
            SecureProtocolConstants.GameBindBytes];
        try
        {
            if (!grant.TryCopySecrets(grantId, ticket))
            {
                throw new InvalidDataException(
                    "Game grant no longer owns its presentation secrets.");
            }
            using var bind = new SecureGameBind(grantId, ticket);
            if (!SecureGameControlCodec.TryEncodeBind(
                    bind,
                    payload,
                    out var written) ||
                written != payload.Length)
            {
                throw new InvalidDataException(
                    "Could not encode the secure game bind.");
            }
            return payload;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(grantId);
            CryptographicOperations.ZeroMemory(ticket);
        }
    }

    private static void WriteHeader(Span<byte> packet, ushort opcode)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet,
            checked((ushort)packet.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            packet[2..],
            opcode);
    }
}
