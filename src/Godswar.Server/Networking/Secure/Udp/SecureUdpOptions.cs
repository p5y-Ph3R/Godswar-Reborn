using System.Net;

namespace Godswar.Server.Networking.Secure.Udp;

internal sealed class SecureUdpOptions
{
    public bool Enabled { get; set; }

    public bool GameplayMovementEnabled { get; set; }

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

    public int UnvalidatedPacketsPerSecond { get; set; } = 3_072;

    public int PrefixPacketsPerSecond { get; set; } = 256;

    public int RateLimitPrefixCapacity { get; set; } = 1_024;

    public int BindingProofPacketsPerSecond { get; set; } = 512;

    public int BindingProofPrefixPacketsPerSecond { get; set; } = 64;

    public int ProtectedCandidatePacketsPerSecond { get; set; } = 512;

    public int ProtectedCandidatePrefixPacketsPerSecond { get; set; } = 256;

    public int AuthenticatedSessionPacketsPerSecond { get; set; } = 256;

    public int KeepAliveIntervalSeconds { get; set; } = 5;

    public int BoundSessionIdleTimeoutSeconds { get; set; } = 30;

    public int SessionCleanupIntervalSeconds { get; set; } = 5;

    public int MinimumRebindIntervalMilliseconds { get; set; } = 2_000;

    public int PreviousKeyEpochOverlapSeconds { get; set; } = 10;

    public int KeyRotationSeconds { get; set; } = 300;

    public int KeyRotationPacketLimit { get; set; } = 1_000_000;

    internal void ApplyEnvironment()
    {
        Enabled = ReadBool(
            "GODSWAR_SECURE_UDP_ENABLED",
            Enabled);
        GameplayMovementEnabled = ReadBool(
            "GODSWAR_SECURE_UDP_GAMEPLAY_MOVEMENT_ENABLED",
            GameplayMovementEnabled);
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
        UnvalidatedPacketsPerSecond = ReadInt(
            "GODSWAR_SECURE_UDP_UNVALIDATED_PACKETS_PER_SECOND",
            UnvalidatedPacketsPerSecond);
        PrefixPacketsPerSecond = ReadInt(
            "GODSWAR_SECURE_UDP_PREFIX_PACKETS_PER_SECOND",
            PrefixPacketsPerSecond);
        RateLimitPrefixCapacity = ReadInt(
            "GODSWAR_SECURE_UDP_RATE_LIMIT_PREFIX_CAPACITY",
            RateLimitPrefixCapacity);
        BindingProofPacketsPerSecond = ReadInt(
            "GODSWAR_SECURE_UDP_BINDING_PROOF_PACKETS_PER_SECOND",
            BindingProofPacketsPerSecond);
        BindingProofPrefixPacketsPerSecond = ReadInt(
            "GODSWAR_SECURE_UDP_BINDING_PROOF_PREFIX_PACKETS_PER_SECOND",
            BindingProofPrefixPacketsPerSecond);
        ProtectedCandidatePacketsPerSecond = ReadInt(
            "GODSWAR_SECURE_UDP_PROTECTED_CANDIDATE_PACKETS_PER_SECOND",
            ProtectedCandidatePacketsPerSecond);
        ProtectedCandidatePrefixPacketsPerSecond = ReadInt(
            "GODSWAR_SECURE_UDP_PROTECTED_CANDIDATE_PREFIX_PACKETS_PER_SECOND",
            ProtectedCandidatePrefixPacketsPerSecond);
        AuthenticatedSessionPacketsPerSecond = ReadInt(
            "GODSWAR_SECURE_UDP_AUTHENTICATED_SESSION_PACKETS_PER_SECOND",
            AuthenticatedSessionPacketsPerSecond);
        KeepAliveIntervalSeconds = ReadInt(
            "GODSWAR_SECURE_UDP_KEEPALIVE_INTERVAL_SECONDS",
            KeepAliveIntervalSeconds);
        BoundSessionIdleTimeoutSeconds = ReadInt(
            "GODSWAR_SECURE_UDP_BOUND_SESSION_IDLE_TIMEOUT_SECONDS",
            BoundSessionIdleTimeoutSeconds);
        SessionCleanupIntervalSeconds = ReadInt(
            "GODSWAR_SECURE_UDP_SESSION_CLEANUP_INTERVAL_SECONDS",
            SessionCleanupIntervalSeconds);
        MinimumRebindIntervalMilliseconds = ReadInt(
            "GODSWAR_SECURE_UDP_MINIMUM_REBIND_INTERVAL_MILLISECONDS",
            MinimumRebindIntervalMilliseconds);
        PreviousKeyEpochOverlapSeconds = ReadInt(
            "GODSWAR_SECURE_UDP_PREVIOUS_KEY_EPOCH_OVERLAP_SECONDS",
            PreviousKeyEpochOverlapSeconds);
        KeyRotationSeconds = ReadInt(
            "GODSWAR_SECURE_UDP_KEY_ROTATION_SECONDS",
            KeyRotationSeconds);
        KeyRotationPacketLimit = ReadInt(
            "GODSWAR_SECURE_UDP_KEY_ROTATION_PACKET_LIMIT",
            KeyRotationPacketLimit);
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
        var minimumGameplayDatagramBytes = checked(
            SecureUdpProtectedConstants.HeaderBytes +
            SecureUdpProtectedConstants.PositionSnapshotPayloadBytes +
            SecureUdpProtectedConstants.TagBytes);
        if (GameplayMovementEnabled &&
            MaximumDatagramBytes < minimumGameplayDatagramBytes)
        {
            throw new InvalidDataException(
                $"Secure.Udp.MaximumDatagramBytes must be at least {minimumGameplayDatagramBytes} when authoritative gameplay movement is enabled.");
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
        if (UnvalidatedPacketsPerSecond < 1 ||
            UnvalidatedPacketsPerSecond >= GlobalPacketsPerSecond)
        {
            throw new InvalidDataException(
                "Secure.Udp.UnvalidatedPacketsPerSecond must be positive and lower than the global limit.");
        }
        if (PrefixPacketsPerSecond < 1 ||
            PrefixPacketsPerSecond > UnvalidatedPacketsPerSecond)
        {
            throw new InvalidDataException(
                "Secure.Udp.PrefixPacketsPerSecond must be positive and cannot exceed the unvalidated limit.");
        }
        if (RateLimitPrefixCapacity is < 1 or > 65_536)
        {
            throw new InvalidDataException(
                "Secure.Udp.RateLimitPrefixCapacity must be between 1 and 65536.");
        }
        if (BindingProofPacketsPerSecond < 1)
        {
            throw new InvalidDataException(
                "Secure.Udp.BindingProofPacketsPerSecond must be positive.");
        }
        if (BindingProofPrefixPacketsPerSecond < 1 ||
            BindingProofPrefixPacketsPerSecond >
                BindingProofPacketsPerSecond)
        {
            throw new InvalidDataException(
                "Secure.Udp.BindingProofPrefixPacketsPerSecond must be positive and cannot exceed the binding-proof limit.");
        }
        if (ProtectedCandidatePacketsPerSecond < 1 ||
            UnvalidatedPacketsPerSecond +
                BindingProofPacketsPerSecond +
                ProtectedCandidatePacketsPerSecond >
                GlobalPacketsPerSecond)
        {
            throw new InvalidDataException(
                "Secure.Udp.ProtectedCandidatePacketsPerSecond must be positive and fit inside the global limit after the unvalidated and binding-proof reserves.");
        }
        if (ProtectedCandidatePrefixPacketsPerSecond < 1 ||
            ProtectedCandidatePrefixPacketsPerSecond >
                ProtectedCandidatePacketsPerSecond)
        {
            throw new InvalidDataException(
                "Secure.Udp.ProtectedCandidatePrefixPacketsPerSecond must be positive and cannot exceed the protected-candidate limit.");
        }
        if (AuthenticatedSessionPacketsPerSecond is < 1 or > 65_536)
        {
            throw new InvalidDataException(
                "Secure.Udp.AuthenticatedSessionPacketsPerSecond must be between 1 and 65536.");
        }
        if (KeepAliveIntervalSeconds is < 2 or > 60)
        {
            throw new InvalidDataException(
                "Secure.Udp.KeepAliveIntervalSeconds must be between 2 and 60.");
        }
        if (BoundSessionIdleTimeoutSeconds <
                KeepAliveIntervalSeconds * 3 ||
            BoundSessionIdleTimeoutSeconds > 600)
        {
            throw new InvalidDataException(
                "Secure.Udp.BoundSessionIdleTimeoutSeconds must allow at least three keepalive intervals and cannot exceed 600.");
        }
        if (SessionCleanupIntervalSeconds < 1 ||
            SessionCleanupIntervalSeconds > KeepAliveIntervalSeconds)
        {
            throw new InvalidDataException(
                "Secure.Udp.SessionCleanupIntervalSeconds must be positive and no greater than the keepalive interval.");
        }
        if (MinimumRebindIntervalMilliseconds is < 500 or > 10_000)
        {
            throw new InvalidDataException(
                "Secure.Udp.MinimumRebindIntervalMilliseconds must be between 500 and 10000.");
        }
        if (PreviousKeyEpochOverlapSeconds is < 1 or > 120)
        {
            throw new InvalidDataException(
                "Secure.Udp.PreviousKeyEpochOverlapSeconds must be between 1 and 120.");
        }
        if (KeyRotationSeconds <
                PreviousKeyEpochOverlapSeconds * 2 ||
            KeyRotationSeconds > 86_400)
        {
            throw new InvalidDataException(
                "Secure.Udp.KeyRotationSeconds must be at least twice the previous-epoch overlap and cannot exceed 86400.");
        }
        if (KeyRotationPacketLimit is < 1_024 or > 100_000_000)
        {
            throw new InvalidDataException(
                "Secure.Udp.KeyRotationPacketLimit must be between 1024 and 100000000.");
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

    private static int ReadInt(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (raw is null)
        {
            return fallback;
        }
        if (int.TryParse(raw, out var value))
        {
            return value;
        }
        throw new InvalidDataException(
            $"{name} must be a valid integer.");
    }

    private static bool ReadBool(string name, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (raw is null)
        {
            return fallback;
        }
        if (bool.TryParse(raw, out var value))
        {
            return value;
        }
        throw new InvalidDataException(
            $"{name} must be 'true' or 'false'.");
    }
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
