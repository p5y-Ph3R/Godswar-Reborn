using System.Security.Cryptography;

namespace Godswar.Server.Networking.Secure.Udp;

internal static class SecureUdpTlsProofAuthenticator
{
    public const int KeyBytes = 32;

    private static ReadOnlySpan<byte> Domain =>
        "GWSU-TLS-BIND-PROOF-V1"u8;

    public static bool TryCompute(
        ReadOnlySpan<byte> proofKey,
        ReadOnlySpan<byte> serverChallenge,
        Span<byte> destination)
    {
        if (proofKey.Length != KeyBytes ||
            SecureUdpBindingCodec.IsAllZero(proofKey) ||
            destination.Length <
                SecureUdpBindingConstants.TlsProofTagBytes ||
            !SecureUdpBindingCodec.TryDecode(
                serverChallenge,
                out var challenge) ||
            challenge.Type != SecureUdpBindingType.ServerChallenge)
        {
            return false;
        }

        Span<byte> input = stackalloc byte[
            Domain.Length + SecureUdpBindingConstants.DatagramBytes];
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        try
        {
            Domain.CopyTo(input);
            serverChallenge.CopyTo(input[Domain.Length..]);
            _ = HMACSHA256.HashData(proofKey, input, hash);
            hash[..SecureUdpBindingConstants.TlsProofTagBytes].CopyTo(
                destination);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    public static bool Validate(
        ReadOnlySpan<byte> proofKey,
        ReadOnlySpan<byte> serverChallenge,
        ReadOnlySpan<byte> suppliedAuthenticator)
    {
        if (suppliedAuthenticator.Length !=
                SecureUdpBindingConstants.TlsProofTagBytes ||
            SecureUdpBindingCodec.IsAllZero(suppliedAuthenticator))
        {
            return false;
        }

        Span<byte> expected = stackalloc byte[
            SecureUdpBindingConstants.TlsProofTagBytes];
        try
        {
            return TryCompute(
                    proofKey,
                    serverChallenge,
                    expected) &&
                CryptographicOperations.FixedTimeEquals(
                    expected,
                    suppliedAuthenticator);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
        }
    }
}
