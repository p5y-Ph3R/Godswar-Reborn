using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace Godswar.Server.Networking.Secure;

internal static class SecureTlsPolicy
{
    public static readonly SslApplicationProtocol ApplicationProtocol =
        new("godswar-shim/1");

    private static readonly TlsCipherSuite[] AllowedCipherSuites =
    [
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
        TlsCipherSuite.TLS_AES_128_GCM_SHA256,
        TlsCipherSuite.TLS_AES_256_GCM_SHA384
    ];

    public static SslServerAuthenticationOptions CreateServerOptions(
        SslStreamCertificateContext certificateContext)
    {
        ArgumentNullException.ThrowIfNull(certificateContext);

        var options = new SslServerAuthenticationOptions
        {
            AllowRenegotiation = false,
            ApplicationProtocols = [ApplicationProtocol],
            ClientCertificateRequired = false,
            EnabledSslProtocols =
                SslProtocols.Tls12 | SslProtocols.Tls13,
            EncryptionPolicy = EncryptionPolicy.RequireEncryption,
            ServerCertificateContext = certificateContext
        };

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
#pragma warning disable CA1416
            options.CipherSuitesPolicy =
                new CipherSuitesPolicy(AllowedCipherSuites);
#pragma warning restore CA1416
        }

        return options;
    }

    public static bool IsNegotiationAccepted(SslStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return stream.IsAuthenticated &&
            stream.IsServer &&
            stream.IsEncrypted &&
            stream.IsSigned &&
            stream.SslProtocol is SslProtocols.Tls12 or SslProtocols.Tls13 &&
            stream.NegotiatedApplicationProtocol == ApplicationProtocol &&
            AllowedCipherSuites.Contains(stream.NegotiatedCipherSuite);
    }

    internal static bool IsCipherSuiteAllowed(TlsCipherSuite cipherSuite)
    {
        return AllowedCipherSuites.Contains(cipherSuite);
    }
}
