namespace Godswar.Server.Networking.Secure.Udp;

internal enum SecureUdpBindingType : byte
{
    ClientHello = 1,
    ServerChallenge = 2,
    ClientProof = 3
}

internal static class SecureUdpBindingConstants
{
    public const uint Magic = 0x47575355; // GWSU
    public const ushort HeaderBytes = 48;
    public const byte ProtocolMajor = 1;
    public const byte ProtocolMinor = 0;
    public const ushort DatagramBytes = 128;
    public const ushort PayloadBytes = 48;
    public const int ConnectionIdBytes = 16;
    public const int ClientNonceBytes = 16;
    public const int CookieTagBytes = 32;
    public const int MaximumDatagramBytes = 1_200;
}

internal readonly ref struct SecureUdpBindingView
{
    public SecureUdpBindingView(
        SecureUdpBindingType type,
        ReadOnlySpan<byte> connectionId,
        uint keyEpoch,
        ulong sequence,
        ReadOnlySpan<byte> clientNonce,
        long issuedAtUnixSeconds,
        ReadOnlySpan<byte> authenticator)
    {
        Type = type;
        ConnectionId = connectionId;
        KeyEpoch = keyEpoch;
        Sequence = sequence;
        ClientNonce = clientNonce;
        IssuedAtUnixSeconds = issuedAtUnixSeconds;
        Authenticator = authenticator;
    }

    public SecureUdpBindingType Type { get; }

    public ReadOnlySpan<byte> ConnectionId { get; }

    public uint KeyEpoch { get; }

    public ulong Sequence { get; }

    public ReadOnlySpan<byte> ClientNonce { get; }

    public long IssuedAtUnixSeconds { get; }

    public ReadOnlySpan<byte> Authenticator { get; }
}
