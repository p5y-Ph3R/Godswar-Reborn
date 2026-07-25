using System.Net;

namespace Godswar.Server.Networking.Secure.Udp;

internal sealed class SecureUdpOptions
{
    public bool Enabled { get; set; }

    public string BindHost { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 7444;

    public int MaximumDatagramBytes { get; set; } =
        SecureUdpBindingConstants.MaximumDatagramBytes;

    public int CookieLifetimeSeconds { get; set; } = 10;

    public int CookieFutureSkewSeconds { get; set; } = 2;

    public int CookieKeyRotationSeconds { get; set; } = 60;

    public int SessionCapacity { get; set; } = 1_024;

    public int BindingOfferTtlSeconds { get; set; } = 30;

    public int GlobalPacketsPerSecond { get; set; } = 4_096;

    public int PrefixPacketsPerSecond { get; set; } = 256;

    public int RateLimitPrefixCapacity { get; set; } = 1_024;

    internal void ApplyEnvironment()
    {
        Enabled = ReadBool(
            "GODSWAR_SECURE_UDP_ENABLED",
            Enabled);
        BindHost = ReadString(
            "GODSWAR_SECURE_UDP_BIND_HOST",
            BindHost);
        Port = ReadInt(
            "GODSWAR_SECURE_UDP_PORT",
            Port);
        MaximumDatagramBytes = ReadInt(
            "GODSWAR_SECURE_UDP_MAXIMUM_DATAGRAM_BYTES",
            MaximumDatagramBytes);
        CookieLifetimeSeconds = ReadInt(
            "GODSWAR_SECURE_UDP_COOKIE_LIFETIME_SECONDS",
            CookieLifetimeSeconds);
        CookieFutureSkewSeconds = ReadInt(
            "GODSWAR_SECURE_UDP_COOKIE_FUTURE_SKEW_SECONDS",
            CookieFutureSkewSeconds);
        CookieKeyRotationSeconds = ReadInt(
            "GODSWAR_SECURE_UDP_COOKIE_KEY_ROTATION_SECONDS",
            CookieKeyRotationSeconds);
        SessionCapacity = ReadInt(
            "GODSWAR_SECURE_UDP_SESSION_CAPACITY",
            SessionCapacity);
        BindingOfferTtlSeconds = ReadInt(
            "GODSWAR_SECURE_UDP_BINDING_OFFER_TTL_SECONDS",
            BindingOfferTtlSeconds);
        GlobalPacketsPerSecond = ReadInt(
            "GODSWAR_SECURE_UDP_GLOBAL_PACKETS_PER_SECOND",
            GlobalPacketsPerSecond);
        PrefixPacketsPerSecond = ReadInt(
            "GODSWAR_SECURE_UDP_PREFIX_PACKETS_PER_SECOND",
            PrefixPacketsPerSecond);
        RateLimitPrefixCapacity = ReadInt(
            "GODSWAR_SECURE_UDP_RATE_LIMIT_PREFIX_CAPACITY",
            RateLimitPrefixCapacity);
    }

    internal void NormalizeAndValidate(
        int rawLoginPort,
        int rawGamePort,
        int tlsLoginPort,
        int tlsGamePort)
    {
        BindHost = BindHost?.Trim() ?? string.Empty;
        if (!IPAddress.TryParse(BindHost, out var address) ||
            !IsLoopbackOrPrivate(address))
        {
            throw new InvalidDataException(
                "Secure.Udp.BindHost must be a literal loopback or private address while UDP is development-only.");
        }
        if (Port is < 1 or > ushort.MaxValue)
        {
            throw new InvalidDataException(
                "Secure.Udp.Port must be between 1 and 65535.");
        }
        if (new[]
            {
                rawLoginPort,
                rawGamePort,
                tlsLoginPort,
                tlsGamePort
            }.Contains(Port))
        {
            throw new InvalidDataException(
                "Secure.Udp.Port must be distinct from every TCP listener port.");
        }
        if (MaximumDatagramBytes is <
                SecureUdpBindingConstants.DatagramBytes or >
                SecureUdpBindingConstants.MaximumDatagramBytes)
        {
            throw new InvalidDataException(
                $"Secure.Udp.MaximumDatagramBytes must be between {SecureUdpBindingConstants.DatagramBytes} and {SecureUdpBindingConstants.MaximumDatagramBytes}.");
        }
        if (CookieLifetimeSeconds is < 5 or > 30)
        {
            throw new InvalidDataException(
                "Secure.Udp.CookieLifetimeSeconds must be between 5 and 30.");
        }
        if (CookieFutureSkewSeconds is < 0 or > 5 ||
            CookieFutureSkewSeconds > CookieLifetimeSeconds)
        {
            throw new InvalidDataException(
                "Secure.Udp.CookieFutureSkewSeconds must be between 0 and 5 and no greater than the cookie lifetime.");
        }
        if (CookieKeyRotationSeconds < CookieLifetimeSeconds * 2 ||
            CookieKeyRotationSeconds > 3_600)
        {
            throw new InvalidDataException(
                "Secure.Udp.CookieKeyRotationSeconds must be at least twice the cookie lifetime and cannot exceed 3600.");
        }
        if (SessionCapacity is < 1 or > 65_536)
        {
            throw new InvalidDataException(
                "Secure.Udp.SessionCapacity must be between 1 and 65536.");
        }
        if (BindingOfferTtlSeconds is < 5 or > 120)
        {
            throw new InvalidDataException(
                "Secure.Udp.BindingOfferTtlSeconds must be between 5 and 120.");
        }
        if (GlobalPacketsPerSecond is < 1 or > 1_000_000)
        {
            throw new InvalidDataException(
                "Secure.Udp.GlobalPacketsPerSecond must be between 1 and 1000000.");
        }
        if (PrefixPacketsPerSecond < 1 ||
            PrefixPacketsPerSecond > GlobalPacketsPerSecond)
        {
            throw new InvalidDataException(
                "Secure.Udp.PrefixPacketsPerSecond must be positive and cannot exceed the global limit.");
        }
        if (RateLimitPrefixCapacity is < 1 or > 65_536)
        {
            throw new InvalidDataException(
                "Secure.Udp.RateLimitPrefixCapacity must be between 1 and 65536.");
        }
        if (Enabled)
        {
            throw new InvalidDataException(
                "Secure UDP remains fail-closed until the protected-datagram ADR and nonblocking native UDP worker are implemented.");
        }
    }

    internal SecureUdpCookiePolicy BuildCookiePolicy()
    {
        return new SecureUdpCookiePolicy(
            TimeSpan.FromSeconds(CookieLifetimeSeconds),
            TimeSpan.FromSeconds(CookieFutureSkewSeconds),
            TimeSpan.FromSeconds(CookieKeyRotationSeconds));
    }

    private static bool IsLoopbackOrPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily ==
            System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168;
        }

        return bytes.Length == 16 && (bytes[0] & 0xFE) == 0xFC;
    }

    private static string ReadString(string name, string fallback) =>
        Environment.GetEnvironmentVariable(name) ?? fallback;

    private static int ReadInt(string name, int fallback) =>
        int.TryParse(
            Environment.GetEnvironmentVariable(name),
            out var value)
            ? value
            : fallback;

    private static bool ReadBool(string name, bool fallback) =>
        bool.TryParse(
            Environment.GetEnvironmentVariable(name),
            out var value)
            ? value
            : fallback;
}

internal readonly record struct SecureUdpCookiePolicy(
    TimeSpan Lifetime,
    TimeSpan FutureSkew,
    TimeSpan KeyRotation)
{
    public void Validate()
    {
        if (Lifetime < TimeSpan.FromSeconds(5) ||
            Lifetime > TimeSpan.FromSeconds(30) ||
            FutureSkew < TimeSpan.Zero ||
            FutureSkew > TimeSpan.FromSeconds(5) ||
            FutureSkew > Lifetime ||
            KeyRotation < Lifetime + Lifetime ||
            KeyRotation > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(SecureUdpCookiePolicy));
        }
    }
}
