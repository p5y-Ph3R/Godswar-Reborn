using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Godswar.Server.Networking.Backhaul;

namespace Godswar.Server.ProtocolChecks;

internal static partial class BackhaulProtocolChecks
{
    private const string ClientAuthenticationOid =
        "1.3.6.1.5.5.7.3.2";
    private const string ServerAuthenticationOid =
        "1.3.6.1.5.5.7.3.1";

    private static async Task CheckTlsPolicyAndRoundTripAsync()
    {
        using var gateway = CreateCertificate(
            "gateway",
            ClientAuthenticationOid);
        using var worker = CreateCertificate(
            "worker",
            ServerAuthenticationOid);
        CheckCertificatePinsAndPurpose(gateway, worker);
        await CheckMutualTlsRoundTripAsync(gateway, worker);
    }

    private static void CheckCertificatePinsAndPurpose(
        X509Certificate2 gateway,
        X509Certificate2 worker)
    {
        var gatewayFingerprint =
            BackhaulCertificatePins.FingerprintOf(gateway);
        var workerFingerprint =
            BackhaulCertificatePins.FingerprintOf(worker);
        var gatewayPins = new BackhaulCertificatePins(
            [gatewayFingerprint, gatewayFingerprint.ToLowerInvariant()]);
        var workerPins = new BackhaulCertificatePins(
            [workerFingerprint]);
        Check.Equal(
            1,
            gatewayPins.Count,
            "duplicate certificate pins are canonicalized");

        Check.Throws<InvalidDataException>(
            () => _ = new BackhaulCertificatePins([]),
            "an empty pin set is rejected");
        Check.Throws<InvalidDataException>(
            () => _ = new BackhaulCertificatePins(
                [new string('0', 64)]),
            "an all-zero pin is rejected");
        Check.Throws<InvalidDataException>(
            () => _ = new BackhaulCertificatePins(
                Enumerable.Range(
                        1,
                        BackhaulCertificatePins.MaximumPins + 1)
                    .Select(static value =>
                        Convert.ToHexString(
                            SHA256.HashData(
                                BitConverter.GetBytes(value))))),
            "pin rotation set is strictly bounded");

        BackhaulTlsPolicy.ValidateLocalCertificate(
            gateway,
            BackhaulCertificatePurpose.GatewayClient,
            TimeProvider.System);
        BackhaulTlsPolicy.ValidateLocalCertificate(
            worker,
            BackhaulCertificatePurpose.WorkerServer,
            TimeProvider.System);
        Check.Throws<InvalidDataException>(
            () => BackhaulTlsPolicy.ValidateLocalCertificate(
                gateway,
                BackhaulCertificatePurpose.WorkerServer,
                TimeProvider.System),
            "gateway EKU cannot be used as a worker certificate");
        using var publicOnly =
            X509CertificateLoader.LoadCertificate(gateway.RawData);
        Check.Throws<InvalidDataException>(
            () => BackhaulTlsPolicy.ValidateLocalCertificate(
                publicOnly,
                BackhaulCertificatePurpose.GatewayClient,
                TimeProvider.System),
            "local certificate requires its private key");

        var serverOptions =
            BackhaulTlsPolicy.CreateWorkerServerOptions(
                worker,
                gatewayPins);
        var clientOptions =
            BackhaulTlsPolicy.CreateGatewayClientOptions(
                "worker.internal",
                gateway,
                workerPins);
        Check.True(
            serverOptions.EnabledSslProtocols ==
                SslProtocols.Tls13 &&
            clientOptions.EnabledSslProtocols ==
                SslProtocols.Tls13 &&
            serverOptions.ClientCertificateRequired &&
            serverOptions.ApplicationProtocols?.Count == 1 &&
            serverOptions.ApplicationProtocols[0] ==
                BackhaulTlsPolicy.ApplicationProtocol &&
            clientOptions.ApplicationProtocols?.Count == 1 &&
            clientOptions.ApplicationProtocols[0] ==
                BackhaulTlsPolicy.ApplicationProtocol,
            "backhaul options require TLS 1.3, mutual auth, and exact ALPN");
        Check.True(
            serverOptions.RemoteCertificateValidationCallback!(
                serverOptions,
                gateway,
                null,
                SslPolicyErrors.RemoteCertificateChainErrors) &&
            clientOptions.RemoteCertificateValidationCallback!(
                clientOptions,
                worker,
                null,
                SslPolicyErrors.RemoteCertificateChainErrors),
            "exact pins accept certificates with the required peer EKU");
        Check.True(
            !serverOptions.RemoteCertificateValidationCallback!(
                serverOptions,
                worker,
                null,
                SslPolicyErrors.None) &&
            !clientOptions.RemoteCertificateValidationCallback!(
                clientOptions,
                gateway,
                null,
                SslPolicyErrors.None),
            "pin and certificate purpose mismatches fail closed");
    }

    private static async Task CheckMutualTlsRoundTripAsync(
        X509Certificate2 gateway,
        X509Certificate2 worker)
    {
        // Keep the pinned mTLS leg outside host security products that proxy
        // IPv4 loopback TLS with their own certificate. The test must observe
        // the exact generated leaf rather than weaken pin validation.
        var listener = new TcpListener(
            IPAddress.IPv6Loopback,
            0);
        listener.Start(1);
        try
        {
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            using var client = new TcpClient(
                AddressFamily.InterNetworkV6);
            var acceptTask = listener.AcceptTcpClientAsync();
            await client.ConnectAsync(
                endpoint.Address,
                endpoint.Port);
            using var accepted = await acceptTask;
            using var gatewayStream = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false);
            using var workerStream = new SslStream(
                accepted.GetStream(),
                leaveInnerStreamOpen: false);
            var gatewayOptions =
                BackhaulTlsPolicy.CreateGatewayClientOptions(
                    "worker.internal",
                    gateway,
                    new BackhaulCertificatePins(
                        [BackhaulCertificatePins.FingerprintOf(worker)]));
            var workerOptions =
                BackhaulTlsPolicy.CreateWorkerServerOptions(
                    worker,
                    new BackhaulCertificatePins(
                        [BackhaulCertificatePins.FingerprintOf(gateway)]));

            var clientHandshake =
                BackhaulStreamIo.AuthenticateAsGatewayAsync(
                    gatewayStream,
                    gatewayOptions,
                    TimeSpan.FromSeconds(10),
                    TimeProvider.System,
                    CancellationToken.None);
            var serverHandshake =
                BackhaulStreamIo.AuthenticateAsWorkerAsync(
                    workerStream,
                    workerOptions,
                    TimeSpan.FromSeconds(10),
                    TimeProvider.System,
                    CancellationToken.None);
            await Task.WhenAll(clientHandshake, serverHandshake);
            Check.True(
                BackhaulTlsPolicy.IsNegotiationAccepted(
                    gatewayStream,
                    localIsServer: false) &&
                BackhaulTlsPolicy.IsNegotiationAccepted(
                    workerStream,
                    localIsServer: true),
                "live loopback negotiation satisfies both policy views");

            var first = new byte[] { 1, 2, 3 };
            var second = new byte[] { 4, 5, 6, 7 };
            await gatewayStream.WriteAsync(first);
            await gatewayStream.FlushAsync();
            await gatewayStream.WriteAsync(second);
            await gatewayStream.FlushAsync();
            var received = new byte[first.Length + second.Length];
            await BackhaulStreamIo.ReadExactlyAsync(
                workerStream,
                received,
                TimeSpan.FromSeconds(5),
                TimeProvider.System,
                CancellationToken.None,
                BackhaulTimeoutStage.TransportWrite);
            Check.True(
                received.SequenceEqual(first.Concat(second)),
                "TLS stream read preserves bytes across segmented writes");

            var response = new byte[] { 9, 8, 7, 6 };
            await BackhaulStreamIo.WriteExactlyAsync(
                workerStream,
                response,
                TimeSpan.FromSeconds(5),
                TimeProvider.System,
                CancellationToken.None,
                BackhaulTimeoutStage.TransportWrite);
            var responseRead = new byte[response.Length];
            await gatewayStream.ReadExactlyAsync(responseRead);
            Check.True(
                responseRead.SequenceEqual(response),
                "TLS stream write preserves exact response bytes");
        }
        finally
        {
            listener.Stop();
        }
    }

    private static X509Certificate2 CreateCertificate(
        string commonName,
        string enhancedKeyUsageOid)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: false,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature,
                critical: true));
        var usages = new OidCollection
        {
            new Oid(enhancedKeyUsageOid)
        };
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                usages,
                critical: true));
        var now = DateTimeOffset.UtcNow;
        using var ephemeral = request.CreateSelfSigned(
            now.AddMinutes(-5),
            now.AddHours(1));
        const string password = "backhaul-protocol-check";
        var pkcs12 = ephemeral.Export(
            X509ContentType.Pkcs12,
            password);
        try
        {
            return X509CertificateLoader.LoadPkcs12(
                pkcs12,
                password,
                OperatingSystem.IsWindows()
                    ? X509KeyStorageFlags.DefaultKeySet
                    : X509KeyStorageFlags.EphemeralKeySet);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }
}
