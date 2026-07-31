using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Godswar.Server.Networking.Backhaul;

internal enum BackhaulCertificatePurpose : byte
{
    GatewayClient = 1,
    WorkerServer = 2
}

/// <summary>
/// Small rotation-aware set of exact SHA-256 leaf-certificate pins.
/// Certificate pins are public identifiers, not secret key material.
/// </summary>
internal sealed class BackhaulCertificatePins
{
    public const int MaximumPins = 8;
    private readonly byte[][] _pins;

    public BackhaulCertificatePins(IEnumerable<string> sha256HexPins)
    {
        ArgumentNullException.ThrowIfNull(sha256HexPins);
        var pins = new List<byte[]>();
        foreach (var configured in sha256HexPins)
        {
            if (string.IsNullOrWhiteSpace(configured) ||
                configured.Length != 64)
            {
                throw new InvalidDataException(
                    "Backhaul certificate pins must be exact 64-character " +
                    "SHA-256 hex values.");
            }

            byte[] pin;
            try
            {
                pin = Convert.FromHexString(configured);
            }
            catch (FormatException error)
            {
                throw new InvalidDataException(
                    "A backhaul certificate pin is not valid hexadecimal.",
                    error);
            }
            if (pin.Length != SHA256.HashSizeInBytes ||
                pin.All(static value => value == 0))
            {
                throw new InvalidDataException(
                    "Backhaul certificate pins must be nonzero SHA-256 " +
                    "digests.");
            }
            if (pins.Any(existing =>
                    CryptographicOperations.FixedTimeEquals(
                        existing,
                        pin)))
            {
                CryptographicOperations.ZeroMemory(pin);
                continue;
            }

            pins.Add(pin);
            if (pins.Count > MaximumPins)
            {
                foreach (var value in pins)
                {
                    CryptographicOperations.ZeroMemory(value);
                }
                throw new InvalidDataException(
                    $"At most {MaximumPins} backhaul certificate pins " +
                    "are allowed.");
            }
        }

        if (pins.Count == 0)
        {
            throw new InvalidDataException(
                "At least one backhaul certificate pin is required.");
        }

        _pins = pins.ToArray();
    }

    public int Count => _pins.Length;

    public static string FingerprintOf(
        X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Convert.ToHexString(
            SHA256.HashData(certificate.RawData));
    }

    internal bool Matches(X509Certificate2 certificate)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        try
        {
            if (!SHA256.TryHashData(
                    certificate.RawData,
                    digest,
                    out var written) ||
                written != digest.Length)
            {
                return false;
            }

            var matched = false;
            foreach (var pin in _pins)
            {
                // Evaluate every configured pin so match position does not
                // affect validation time.
                matched |= CryptographicOperations.FixedTimeEquals(
                    pin,
                    digest);
            }
            return matched;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(digest);
        }
    }
}

/// <summary>
/// TLS 1.3-only, mutually authenticated private backhaul policy. Exact leaf
/// pins intentionally replace public-PKI name/chain trust; validity, CA,
/// key-usage, EKU, ALPN, encryption and cipher policy remain mandatory.
/// </summary>
internal static class BackhaulTlsPolicy
{
    private const string ClientAuthenticationOid =
        "1.3.6.1.5.5.7.3.2";
    private const string ServerAuthenticationOid =
        "1.3.6.1.5.5.7.3.1";

    public static readonly SslApplicationProtocol ApplicationProtocol =
        new("godswar-backhaul/1");

    private static readonly TlsCipherSuite[] AllowedCipherSuites =
    [
        TlsCipherSuite.TLS_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256
    ];

    public static SslServerAuthenticationOptions CreateWorkerServerOptions(
        X509Certificate2 workerCertificate,
        BackhaulCertificatePins allowedGatewayCertificates,
        TimeProvider? timeProvider = null)
    {
        ValidateLocalCertificate(
            workerCertificate,
            BackhaulCertificatePurpose.WorkerServer,
            timeProvider ?? TimeProvider.System);
        ArgumentNullException.ThrowIfNull(allowedGatewayCertificates);

        var options = new SslServerAuthenticationOptions
        {
            AllowRenegotiation = false,
            ApplicationProtocols = [ApplicationProtocol],
            CertificateRevocationCheckMode =
                X509RevocationMode.NoCheck,
            ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls13,
            EncryptionPolicy = EncryptionPolicy.RequireEncryption,
            RemoteCertificateValidationCallback =
                CreatePeerValidator(
                    allowedGatewayCertificates,
                    BackhaulCertificatePurpose.GatewayClient,
                    timeProvider ?? TimeProvider.System),
            ServerCertificate = workerCertificate
        };
        ApplyCipherPolicy(options);
        return options;
    }

    public static SslClientAuthenticationOptions CreateGatewayClientOptions(
        string workerTlsHost,
        X509Certificate2 gatewayCertificate,
        BackhaulCertificatePins allowedWorkerCertificates,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(workerTlsHost) ||
            workerTlsHost.Length > 253 ||
            workerTlsHost.Any(static character =>
                character is <= ' ' or > '~'))
        {
            throw new ArgumentException(
                "Worker TLS host must be bounded printable ASCII.",
                nameof(workerTlsHost));
        }
        ValidateLocalCertificate(
            gatewayCertificate,
            BackhaulCertificatePurpose.GatewayClient,
            timeProvider ?? TimeProvider.System);
        ArgumentNullException.ThrowIfNull(allowedWorkerCertificates);

        var options = new SslClientAuthenticationOptions
        {
            AllowRenegotiation = false,
            ApplicationProtocols = [ApplicationProtocol],
            CertificateRevocationCheckMode =
                X509RevocationMode.NoCheck,
            ClientCertificates =
                new X509CertificateCollection
                {
                    gatewayCertificate
                },
            EnabledSslProtocols = SslProtocols.Tls13,
            EncryptionPolicy = EncryptionPolicy.RequireEncryption,
            RemoteCertificateValidationCallback =
                CreatePeerValidator(
                    allowedWorkerCertificates,
                    BackhaulCertificatePurpose.WorkerServer,
                    timeProvider ?? TimeProvider.System),
            TargetHost = workerTlsHost
        };
        ApplyCipherPolicy(options);
        return options;
    }

    public static bool IsNegotiationAccepted(
        SslStream stream,
        bool localIsServer)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return stream.IsAuthenticated &&
            stream.IsServer == localIsServer &&
            stream.IsEncrypted &&
            stream.IsSigned &&
            stream.RemoteCertificate is not null &&
            stream.SslProtocol == SslProtocols.Tls13 &&
            stream.NegotiatedApplicationProtocol ==
                ApplicationProtocol &&
            AllowedCipherSuites.Contains(
                stream.NegotiatedCipherSuite);
    }

    internal static void ValidateLocalCertificate(
        X509Certificate2 certificate,
        BackhaulCertificatePurpose purpose,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!certificate.HasPrivateKey)
        {
            throw new InvalidDataException(
                "A local backhaul certificate must have a private key.");
        }
        if (!ValidateCertificate(
                certificate,
                purpose,
                timeProvider))
        {
            throw new InvalidDataException(
                $"The local backhaul certificate is invalid for {purpose}.");
        }
    }

    private static RemoteCertificateValidationCallback
        CreatePeerValidator(
            BackhaulCertificatePins pins,
            BackhaulCertificatePurpose purpose,
            TimeProvider timeProvider) =>
        (_, certificate, _, _) =>
        {
            if (certificate is null)
            {
                return false;
            }

            X509Certificate2? loaded = null;
            try
            {
                var leaf = certificate as X509Certificate2;
                if (leaf is null)
                {
                    loaded = X509CertificateLoader.LoadCertificate(
                        certificate.GetRawCertData());
                    leaf = loaded;
                }

                return pins.Matches(leaf) &&
                    ValidateCertificate(
                        leaf,
                        purpose,
                        timeProvider);
            }
            catch (Exception error)
                when (error is CryptographicException or
                    InvalidOperationException or
                    ArgumentException)
            {
                return false;
            }
            finally
            {
                loaded?.Dispose();
            }
        };

    private static bool ValidateCertificate(
        X509Certificate2 certificate,
        BackhaulCertificatePurpose purpose,
        TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        if (now < certificate.NotBefore.ToUniversalTime() ||
            now > certificate.NotAfter.ToUniversalTime())
        {
            return false;
        }

        var constraintExtensions = certificate.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .Take(2)
            .ToArray();
        if (constraintExtensions.Length != 1 ||
            constraintExtensions[0].CertificateAuthority)
        {
            return false;
        }

        var ekuExtensions = certificate.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .Take(2)
            .ToArray();
        var requiredOid = purpose switch
        {
            BackhaulCertificatePurpose.GatewayClient =>
                ClientAuthenticationOid,
            BackhaulCertificatePurpose.WorkerServer =>
                ServerAuthenticationOid,
            _ => throw new ArgumentOutOfRangeException(
                nameof(purpose))
        };
        if (ekuExtensions.Length != 1 ||
            !ekuExtensions[0].EnhancedKeyUsages
                .Cast<Oid>()
                .Any(oid => string.Equals(
                    oid.Value,
                    requiredOid,
                    StringComparison.Ordinal)))
        {
            return false;
        }

        var keyUsageExtensions = certificate.Extensions
            .OfType<X509KeyUsageExtension>()
            .Take(2)
            .ToArray();
        if (keyUsageExtensions.Length > 1 ||
            keyUsageExtensions.Length == 1 &&
            (keyUsageExtensions[0].KeyUsages &
                X509KeyUsageFlags.DigitalSignature) == 0)
        {
            return false;
        }

        using var rsa = certificate.GetRSAPublicKey();
        using var ecdsa = certificate.GetECDsaPublicKey();
        return rsa is { KeySize: >= 2048 } ||
            ecdsa is { KeySize: >= 256 };
    }

    private static void ApplyCipherPolicy(
        SslServerAuthenticationOptions options)
    {
        if (!OperatingSystem.IsLinux() &&
            !OperatingSystem.IsMacOS())
        {
            return;
        }

#pragma warning disable CA1416
        options.CipherSuitesPolicy =
            new CipherSuitesPolicy(AllowedCipherSuites);
#pragma warning restore CA1416
    }

    private static void ApplyCipherPolicy(
        SslClientAuthenticationOptions options)
    {
        if (!OperatingSystem.IsLinux() &&
            !OperatingSystem.IsMacOS())
        {
            return;
        }

#pragma warning disable CA1416
        options.CipherSuitesPolicy =
            new CipherSuitesPolicy(AllowedCipherSuites);
#pragma warning restore CA1416
    }
}
