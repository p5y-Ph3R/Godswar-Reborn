using System.Net;
using System.Text.Json.Serialization;

namespace Godswar.Server.Networking.Secure;

internal sealed class SecureNetworkOptions
{
    internal const string DefaultCertificatePasswordEnvironmentVariable =
        "GODSWAR_SECURE_CERTIFICATE_PASSWORD";

    internal const string PredecessorOriginSha256 =
        "753BE49FE94B6F4C0E3329BC8905945BD9B0F1A790B4B9038E69C2A5AD49ED79";

    public bool Enabled { get; set; }

    public SecureEndpointOptions Login { get; set; } = new()
    {
        BindHost = "127.0.0.1",
        Port = 6599,
        DnsHost = "login.reborn.test"
    };

    public SecureEndpointOptions Game { get; set; } = new()
    {
        BindHost = "127.0.0.1",
        Port = 7443,
        DnsHost = "game.reborn.test"
    };

    public string CertificatePath { get; set; } = string.Empty;

    public string[] AllowedOriginSha256 { get; set; } =
        [PredecessorOriginSha256];

    [JsonIgnore]
    public string CertificatePassword { get; set; } = string.Empty;

    public void ApplyEnvironment()
    {
        Enabled = ReadBool("GODSWAR_SECURE_ENABLED", Enabled);
        Login ??= new SecureEndpointOptions();
        Game ??= new SecureEndpointOptions();
        Login.BindHost = ReadString(
            "GODSWAR_SECURE_LOGIN_BIND_HOST",
            Login.BindHost);
        Login.Port = ReadInt("GODSWAR_SECURE_LOGIN_PORT", Login.Port);
        Login.DnsHost = ReadString(
            "GODSWAR_SECURE_LOGIN_DNS_HOST",
            Login.DnsHost);
        Game.BindHost = ReadString(
            "GODSWAR_SECURE_GAME_BIND_HOST",
            Game.BindHost);
        Game.Port = ReadInt("GODSWAR_SECURE_GAME_PORT", Game.Port);
        Game.DnsHost = ReadString(
            "GODSWAR_SECURE_GAME_DNS_HOST",
            Game.DnsHost);
        CertificatePath = ReadString(
            "GODSWAR_SECURE_CERTIFICATE_PATH",
            CertificatePath);
        CertificatePassword =
            Environment.GetEnvironmentVariable(
                DefaultCertificatePasswordEnvironmentVariable)
            ?? CertificatePassword;

        var allowedBuilds =
            Environment.GetEnvironmentVariable(
                "GODSWAR_SECURE_ALLOWED_ORIGIN_SHA256");
        if (!string.IsNullOrWhiteSpace(allowedBuilds))
        {
            AllowedOriginSha256 = allowedBuilds.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);
        }
    }

    public void NormalizeAndValidate(
        string optionsPath,
        int rawLoginPort,
        int rawGamePort)
    {
        Login ??= new SecureEndpointOptions();
        Game ??= new SecureEndpointOptions();
        AllowedOriginSha256 ??= [];

        Login.Normalize();
        Game.Normalize();
        CertificatePath = CertificatePath?.Trim() ?? string.Empty;

        if (!Enabled)
        {
            return;
        }

        Login.Validate(nameof(Login));
        Game.Validate(nameof(Game));

        if (Login.Port == Game.Port ||
            Login.Port == rawLoginPort ||
            Login.Port == rawGamePort ||
            Game.Port == rawLoginPort ||
            Game.Port == rawGamePort)
        {
            throw new InvalidDataException(
                "Secure login/game ports must be distinct from each other and from both raw ports.");
        }

        AllowedOriginSha256 = AllowedOriginSha256
            .Select(NormalizeBuildHash)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (AllowedOriginSha256.Length == 0)
        {
            throw new InvalidDataException(
                "At least one allowed Origin SHA-256 is required.");
        }

        Login.ValidateDevelopmentBind(nameof(Login));
        Game.ValidateDevelopmentBind(nameof(Game));

        if (string.IsNullOrWhiteSpace(CertificatePath))
        {
            throw new InvalidDataException(
                "Secure networking requires a PKCS#12 certificate path.");
        }

        if (!Path.IsPathRooted(CertificatePath))
        {
            var root =
                Path.GetDirectoryName(Path.GetFullPath(optionsPath))
                ?? Environment.CurrentDirectory;
            CertificatePath = Path.GetFullPath(
                Path.Combine(root, CertificatePath));
        }

        if (!File.Exists(CertificatePath))
        {
            throw new InvalidDataException(
                "The configured secure-network certificate file does not exist.");
        }
        if (string.IsNullOrEmpty(CertificatePassword))
        {
            throw new InvalidDataException(
                $"Secure networking requires a nonempty PKCS#12 password supplied through {DefaultCertificatePasswordEnvironmentVariable}.");
        }
    }

    internal IReadOnlySet<string> BuildAllowedHashSet()
    {
        return AllowedOriginSha256.ToHashSet(StringComparer.Ordinal);
    }

    internal static void ValidateSecureRuntime(
        NetworkRuntimeOptions runtimeOptions)
    {
        ArgumentNullException.ThrowIfNull(runtimeOptions);
        if (runtimeOptions.IngressQueueBytes <
            SecureProtocolConstants.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                $"Secure ingress byte capacity must accept one {SecureProtocolConstants.MaximumPayloadBytes}-byte outer payload.");
        }
        if (runtimeOptions.ControlQueueBytes < 8)
        {
            throw new InvalidDataException(
                "Secure control byte capacity must accept one 8-byte Pong.");
        }
    }

    private static string NormalizeBuildHash(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Length != 64 ||
            !normalized.All(static character =>
                character is >= '0' and <= '9' or >= 'A' and <= 'F'))
        {
            throw new InvalidDataException(
                "Every allowed Origin SHA-256 must contain exactly 64 hexadecimal characters.");
        }

        return normalized;
    }

    private static string ReadString(string name, string fallback)
    {
        return Environment.GetEnvironmentVariable(name) ?? fallback;
    }

    private static int ReadInt(string name, int fallback)
    {
        return int.TryParse(
            Environment.GetEnvironmentVariable(name),
            out var value)
            ? value
            : fallback;
    }

    private static bool ReadBool(string name, bool fallback)
    {
        return bool.TryParse(
            Environment.GetEnvironmentVariable(name),
            out var value)
            ? value
            : fallback;
    }
}

internal sealed class SecureEndpointOptions
{
    public string BindHost { get; set; } = "127.0.0.1";

    public int Port { get; set; }

    public string DnsHost { get; set; } = string.Empty;

    internal void Normalize()
    {
        BindHost = BindHost?.Trim() ?? string.Empty;
        DnsHost = DnsHost?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    internal void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(BindHost))
        {
            throw new InvalidDataException(
                $"{name}.BindHost cannot be empty.");
        }
        if (Port is < 1 or > 65_535)
        {
            throw new InvalidDataException(
                $"{name}.Port must be between 1 and 65535.");
        }
        if (!SecureProtocolValidation.IsDnsName(DnsHost, 253))
        {
            throw new InvalidDataException(
                $"{name}.DnsHost must be a strict lowercase ASCII DNS name.");
        }
    }

    internal void ValidateDevelopmentBind(string name)
    {
        if (!IPAddress.TryParse(BindHost, out var address) ||
            !IsLoopbackOrPrivate(address))
        {
            throw new InvalidDataException(
                $"{name}.BindHost must be a literal loopback or private address while secure endpoints are development-only.");
        }
    }

    private static bool IsLoopbackOrPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
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
}
