using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Godswar.Server.ProtocolChecks;

internal sealed class SecureTlsTestCertificate : IDisposable
{
    private readonly CngKey? _serverCngKey;
    private readonly RSA _serverKey;

    private SecureTlsTestCertificate(
        RSA serverKey,
        CngKey? serverCngKey,
        X509Certificate2 root,
        X509Certificate2 server)
    {
        _serverKey = serverKey;
        _serverCngKey = serverCngKey;
        Root = root;
        Server = server;
        Context = SslStreamCertificateContext.Create(
            server,
            new X509Certificate2Collection(root),
            offline: true);
    }

    public X509Certificate2 Root { get; }

    public X509Certificate2 Server { get; }

    public SslStreamCertificateContext Context { get; }

    public static SecureTlsTestCertificate Create(
        params string[] dnsNames)
    {
        dnsNames = dnsNames.Length == 0
            ? ["login.reborn.test", "game.reborn.test"]
            : dnsNames;
        var now = DateTimeOffset.UtcNow;
        using var rootKey = RSA.Create(3072);
        var rootRequest = new CertificateRequest(
            $"CN=Reborn Slice 6 Test Root {Guid.NewGuid():N}",
            rootKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: true,
                hasPathLengthConstraint: true,
                pathLengthConstraint: 0,
                critical: true));
        rootRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign |
                X509KeyUsageFlags.CrlSign,
                critical: true));
        rootRequest.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(
                rootRequest.PublicKey,
                critical: false));
        using var rootWithKey = rootRequest.CreateSelfSigned(
            now.AddDays(-1),
            now.AddDays(30));
        var root = X509CertificateLoader.LoadCertificate(
            rootWithKey.RawData);

        var (serverKey, serverCngKey) = CreateServerKey();
        var serverRequest = new CertificateRequest(
            $"CN={dnsNames[0]}",
            serverKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        serverRequest.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: false,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: true));
        serverRequest.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature |
                X509KeyUsageFlags.KeyEncipherment,
                critical: true));
        var usages = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1")
        };
        serverRequest.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(usages, critical: true));
        serverRequest.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(
                serverRequest.PublicKey,
                critical: false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var dnsName in dnsNames)
        {
            san.AddDnsName(dnsName);
        }
        serverRequest.CertificateExtensions.Add(san.Build());

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        if (serial.All(static value => value == 0))
        {
            serial[^1] = 1;
        }
        using var publicServer = serverRequest.Create(
            rootWithKey,
            now.AddMinutes(-5),
            now.AddDays(7),
            serial);
        var server = publicServer.CopyWithPrivateKey(serverKey);
        return new SecureTlsTestCertificate(
            serverKey,
            serverCngKey,
            root,
            server);
    }

    private static (RSA Key, CngKey? CngKey) CreateServerKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (RSA.Create(2048), null);
        }

        return CreateWindowsServerKey();
    }

    [SupportedOSPlatform("windows")]
    private static (RSA Key, CngKey CngKey) CreateWindowsServerKey()
    {
        var parameters = new CngKeyCreationParameters
        {
            ExportPolicy = CngExportPolicies.AllowExport,
            KeyCreationOptions = CngKeyCreationOptions.None,
            KeyUsage = CngKeyUsages.Signing,
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider
        };
        parameters.Parameters.Add(
            new CngProperty(
                "Length",
                BitConverter.GetBytes(2048),
                CngPropertyOptions.None));
        var cngKey = CngKey.Create(
            CngAlgorithm.Rsa,
            $"RebornSlice6TlsTest-{Guid.NewGuid():N}",
            parameters);
        return (new RSACng(cngKey), cngKey);
    }

    public byte[] ExportPfx(string password)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        var certificates = new X509Certificate2Collection
        {
            Server,
            Root
        };
        return certificates.Export(
                X509ContentType.Pkcs12,
                password)
            ?? throw new CryptographicException(
                "The TLS test certificate collection could not be exported.");
    }

    public SslClientAuthenticationOptions CreateClientOptions(
        string targetHost = "login.reborn.test",
        string applicationProtocol = "godswar-shim/1")
    {
        return new SslClientAuthenticationOptions
        {
            AllowRenegotiation = false,
            ApplicationProtocols =
                [new SslApplicationProtocol(applicationProtocol)],
            // The flow-controlled named-pipe harness negotiates the shared
            // TLS 1.2 minimum. TLS 1.3 remains enabled by server policy.
            EnabledSslProtocols =
                System.Security.Authentication.SslProtocols.Tls12,
            EncryptionPolicy = EncryptionPolicy.RequireEncryption,
            TargetHost = targetHost,
            RemoteCertificateValidationCallback =
                (_, remoteCertificate, _, _) =>
                    IsPinnedTestServer(
                        remoteCertificate,
                        targetHost)
        };
    }

    private bool IsPinnedTestServer(
        X509Certificate? remoteCertificate,
        string targetHost)
    {
        if (remoteCertificate is null)
        {
            return false;
        }

        var remoteBytes = remoteCertificate.GetRawCertData();
        var pinned = CryptographicOperations.FixedTimeEquals(
            remoteBytes,
            Server.RawData);
        using var leaf = X509CertificateLoader.LoadCertificate(remoteBytes);
        var hasName = leaf.Extensions
            .OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(static extension => extension.EnumerateDnsNames())
            .Contains(targetHost, StringComparer.OrdinalIgnoreCase);
        return pinned && hasName;
    }

    public void Dispose()
    {
        Server.Dispose();
        Root.Dispose();
        if (_serverCngKey is not null)
        {
            _serverCngKey.Delete();
        }
        _serverKey.Dispose();
        _serverCngKey?.Dispose();
    }
}
