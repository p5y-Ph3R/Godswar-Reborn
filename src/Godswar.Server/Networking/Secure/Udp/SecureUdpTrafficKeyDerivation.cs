using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Godswar.Server.Networking.Secure.Udp;

internal static class SecureUdpTrafficKeyDerivation
{
    private static readonly byte[] Domain =
        Encoding.ASCII.GetBytes("GWSU-PROTECTED-DATAGRAM-V1");

    public static bool TryDeriveKey(
        ReadOnlySpan<byte> bindingSecret,
        ReadOnlySpan<byte> connectionId,
        uint serverId,
        SecureUdpTrafficDirection direction,
        uint keyEpoch,
        Span<byte> destination)
    {
        if (bindingSecret.Length !=
                SecureUdpProtectedConstants.KeyBytes ||
            connectionId.Length !=
                SecureUdpProtectedConstants.ConnectionIdBytes ||
            SecureUdpBindingCodec.IsAllZero(bindingSecret) ||
            SecureUdpBindingCodec.IsAllZero(connectionId) ||
            serverId == 0 ||
            keyEpoch == 0 ||
            direction is not (
                SecureUdpTrafficDirection.ClientToServer or
                SecureUdpTrafficDirection.ServerToClient) ||
            destination.Length <
                SecureUdpProtectedConstants.KeyBytes)
        {
            return false;
        }

        Span<byte> salt = stackalloc byte[
            SecureUdpProtectedConstants.ConnectionIdBytes +
            sizeof(uint)];
        connectionId.CopyTo(salt);
        BinaryPrimitives.WriteUInt32BigEndian(
            salt[SecureUdpProtectedConstants.ConnectionIdBytes..],
            serverId);

        Span<byte> info = stackalloc byte[
            Domain.Length + sizeof(byte) + sizeof(uint)];
        Domain.CopyTo(info);
        info[Domain.Length] = (byte)direction;
        BinaryPrimitives.WriteUInt32BigEndian(
            info[(Domain.Length + sizeof(byte))..],
            keyEpoch);

        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            bindingSecret,
            destination[..SecureUdpProtectedConstants.KeyBytes],
            salt,
            info);
        CryptographicOperations.ZeroMemory(salt);
        CryptographicOperations.ZeroMemory(info);
        return true;
    }
}
