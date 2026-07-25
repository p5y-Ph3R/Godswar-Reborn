using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Godswar.Server.Networking.Secure;

internal sealed class SecureServerCertificate : IDisposable
{
    private const string RsaSha256SignatureOid =
        "1.2.840.113549.1.1.11";

    private const string ServerAuthenticationOid =
        "1.3.6.1.5.5.7.3.1";

    private readonly X509Certificate2Collection _certificates;
    private int _disposed;

    private SecureServerCertificate(
        X509Certificate2Collection certificates,
        SslStreamCertificateContext context)
    {
        _certificates = certificates;
        Context = context;
    }

    public SslStreamCertificateContext Context { get; }

    public static SecureServerCertificate Load(
        SecureNetworkOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled)
        {
            throw new InvalidOperationException(
                "A server certificate is not loaded while secure networking is disabled.");
        }

        var certificates =
            X509CertificateLoader.LoadPkcs12CollectionFromFile(
                options.CertificatePath,
                options.CertificatePassword,
                OperatingSystem.IsWindows()
                    ? X509KeyStorageFlags.DefaultKeySet
                    : X509KeyStorageFlags.EphemeralKeySet);
        try
        {
            var privateCertificates = certificates
                .Where(static certificate => certificate.HasPrivateKey)
                .ToArray();
            if (privateCertificates.Length != 1)
            {
                throw new InvalidDataException(
                    "The secure-network PKCS#12 must contain exactly one certificate with a private key.");
            }

            var leaf = privateCertificates[0];
            ValidateLeaf(
                leaf,
                options.Login.DnsHost,
                options.Game.DnsHost,
                timeProvider ?? TimeProvider.System);
            var additional = new X509Certificate2Collection();
            foreach (var certificate in certificates)
            {
                if (!ReferenceEquals(certificate, leaf))
                {
                    additional.Add(certificate);
                }
            }

            var context = SslStreamCertificateContext.Create(
                leaf,
                additional,
                offline: true);
            return new SecureServerCertificate(certificates, context);
        }
        catch
        {
            DisposeCertificates(certificates);
            throw;
        }
    }

    internal static void ValidateLeaf(
        X509Certificate2 leaf,
        string loginDnsHost,
        string gameDnsHost,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(leaf);
        ArgumentNullException.ThrowIfNull(timeProvider);

        using var rsa = leaf.GetRSAPrivateKey();
        if (!leaf.HasPrivateKey || rsa is null || rsa.KeySize < 2048)
        {
            throw new InvalidDataException(
                "The TLS certificate must have an RSA private key of at least 2048 bits.");
        }
        if (!string.Equals(
                leaf.SignatureAlgorithm.Value,
                RsaSha256SignatureOid,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The TLS leaf certificate must use an RSA SHA-256 signature.");
        }

        var now = timeProvider.GetUtcNow();
        if (now < leaf.NotBefore.ToUniversalTime() ||
            now > leaf.NotAfter.ToUniversalTime())
        {
            throw new InvalidDataException(
                "The TLS leaf certificate is outside its validity interval.");
        }

        var basicConstraints = leaf.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .SingleOrDefault();
        if (basicConstraints?.CertificateAuthority == true)
        {
            throw new InvalidDataException(
                "The TLS leaf certificate cannot be a certificate authority.");
        }

        var enhancedKeyUsage = leaf.Extensions
            .OfType<X509EnhancedKeyUsageExtension>()
            .SingleOrDefault();
        if (enhancedKeyUsage is null ||
            !enhancedKeyUsage.EnhancedKeyUsages
                .Cast<Oid>()
                .Any(static oid =>
                    string.Equals(
                        oid.Value,
                        ServerAuthenticationOid,
                        StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The TLS leaf certificate must contain the server-authentication EKU.");
        }

        var dnsNames = leaf.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(static extension => extension.EnumerateDnsNames())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!dnsNames.Contains(loginDnsHost) ||
            !dnsNames.Contains(gameDnsHost))
        {
            throw new InvalidDataException(
                "The TLS certificate SAN must contain both configured secure DNS hosts.");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            DisposeCertificates(_certificates);
        }
    }

    private static void DisposeCertificates(
        X509Certificate2Collection certificates)
    {
        foreach (var certificate in certificates)
        {
            certificate.Dispose();
        }
    }
}
