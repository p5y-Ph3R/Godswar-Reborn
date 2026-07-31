using System.Net;
using System.Security.Cryptography.X509Certificates;
using Godswar.Server.Networking.Backhaul;
using Godswar.Server.Networking.Secure;

namespace Godswar.Server;

internal sealed class BackhaulWorkerRuntimeOptions
{
    public bool Enabled { get; set; }

    public string BindHost { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 17_000;

    public string CertificatePath { get; set; } = string.Empty;

    public string CertificatePasswordEnvironmentVariable { get; set; } =
        "GODSWAR_BACKHAUL_WORKER_CERTIFICATE_PASSWORD";

    public string[] AllowedGatewayCertificateSha256 { get; set; } = [];

    public int AdmissionCapacity { get; set; } = 512;

    public int ReplayCapacity { get; set; } = 4_096;

    public int ReplayRetentionSeconds { get; set; } = 300;

    public int AdmissionLifetimeSafetyMarginSeconds { get; set; } = 15;

    public int CleanupBatchSize { get; set; } = 64;

    public int MaximumConcurrentTlsHandshakes { get; set; } = 32;

    public int TlsHandshakeTimeoutMilliseconds { get; set; } = 5_000;

    public int OpenSessionTimeoutMilliseconds { get; set; } = 2_000;

    public int WriteTimeoutMilliseconds { get; set; } = 5_000;

    public IPEndPoint BindEndpoint =>
        new(ResolveAddress(BindHost), Port);

    public BackhaulRuntimeLimits RuntimeLimits =>
        new BackhaulRuntimeLimits(
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(
                TlsHandshakeTimeoutMilliseconds),
            TimeSpan.FromMilliseconds(
                OpenSessionTimeoutMilliseconds),
            TimeSpan.FromMilliseconds(
                WriteTimeoutMilliseconds))
        .Validate();

    public BackhaulCertificatePins BuildAllowedGatewayPins() =>
        new(AllowedGatewayCertificateSha256);

    public X509Certificate2 LoadCertificate()
    {
        if (!Enabled)
        {
            throw new InvalidOperationException(
                "Backhaul worker mode is disabled.");
        }

        var password = Environment.GetEnvironmentVariable(
            CertificatePasswordEnvironmentVariable);
        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidDataException(
                "The backhaul worker certificate password environment " +
                "variable is missing.");
        }

        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(
                CertificatePath,
                password,
                OperatingSystem.IsWindows()
                    ? X509KeyStorageFlags.DefaultKeySet
                    : X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (Exception exception)
            when (exception is IOException or
                System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidDataException(
                "The backhaul worker certificate could not be loaded.",
                exception);
        }
    }

    public WorkerBackhaulAdmissionRegistry BuildAdmissionRegistry(
        WorldInstanceRuntimeOptions worldInstances)
    {
        ArgumentNullException.ThrowIfNull(worldInstances);
        return new WorkerBackhaulAdmissionRegistry(
            worldInstances.ProcessServerNodeId,
            worldInstances.StaticOpenWorldInstances.Select(
                static route =>
                    new BackhaulOwnedWorldRoute(
                        route.ProcessRealmId,
                        route.ProcessMapId,
                        route.ProcessWorldInstanceId)),
            AdmissionCapacity,
            ReplayCapacity,
            TimeSpan.FromSeconds(ReplayRetentionSeconds),
            TimeSpan.FromSeconds(
                AdmissionLifetimeSafetyMarginSeconds),
            CleanupBatchSize);
    }

    public void NormalizeAndValidate(
        string optionsPath,
        WorldInstanceRuntimeOptions worldInstances,
        SecureNetworkOptions secure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(optionsPath);
        ArgumentNullException.ThrowIfNull(worldInstances);
        ArgumentNullException.ThrowIfNull(secure);
        if (!Enabled)
        {
            return;
        }

        if (secure.Enabled)
        {
            throw new InvalidDataException(
                "Backhaul worker mode cannot expose the public secure " +
                "login/game listener pair in the same process.");
        }

        if (!Path.IsPathRooted(CertificatePath))
        {
            var root = Path.GetDirectoryName(
                Path.GetFullPath(optionsPath)) ??
                Environment.CurrentDirectory;
            CertificatePath = Path.GetFullPath(
                Path.Combine(root, CertificatePath));
        }

        if (!File.Exists(CertificatePath) ||
            string.IsNullOrWhiteSpace(
                CertificatePasswordEnvironmentVariable) ||
            CertificatePasswordEnvironmentVariable.Length > 128 ||
            Port is < 1 or > ushort.MaxValue ||
            AdmissionCapacity is < 1 or >
                WorkerBackhaulAdmissionRegistry.MaximumCapacity ||
            ReplayCapacity is < 1 or >
                WorkerBackhaulAdmissionRegistry.MaximumReplayCapacity ||
            ReplayRetentionSeconds is < 0 or > 600 ||
            AdmissionLifetimeSafetyMarginSeconds is < 0 or > 60 ||
            CleanupBatchSize is < 1 or > 1_024 ||
            MaximumConcurrentTlsHandshakes is < 1 or >
                BackhaulHandshakeGate.MaximumConcurrency)
        {
            throw new InvalidDataException(
                "Backhaul worker configuration is invalid.");
        }

        _ = BindEndpoint;
        if (!IsPrivateOrLoopback(BindEndpoint.Address))
        {
            throw new InvalidDataException(
                "Backhaul worker BindHost must be loopback or a private " +
                "IPv4/IPv6 address.");
        }
        _ = RuntimeLimits;
        _ = BuildAllowedGatewayPins();
        if (worldInstances.StaticOpenWorldInstances.Length == 0)
        {
            throw new InvalidDataException(
                "Backhaul workers require at least one exact static " +
                "open-world route.");
        }

        worldInstances.RequireStaticOpenWorldOwnership = true;
    }

    private static IPAddress ResolveAddress(string host)
    {
        if (!IPAddress.TryParse(host, out var address) ||
            address.Equals(IPAddress.Any) ||
            address.Equals(IPAddress.IPv6Any))
        {
            throw new InvalidDataException(
                "Backhaul worker BindHost must be an explicit IP address.");
        }

        return address;
    }

    private static bool IsPrivateOrLoopback(IPAddress address)
    {
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

        return address.AddressFamily ==
                System.Net.Sockets.AddressFamily.InterNetworkV6 &&
            (bytes[0] & 0xFE) == 0xFC;
    }
}
