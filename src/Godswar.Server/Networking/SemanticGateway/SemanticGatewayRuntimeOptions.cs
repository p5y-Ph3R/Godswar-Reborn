namespace Godswar.Server.Networking.SemanticGateway;

/// <summary>
/// Strict JSON shape for the opt-in B18C2 semantic gateway. Validation and
/// certificate loading are implemented in the companion loading file.
/// </summary>
internal sealed partial class SemanticGatewayRuntimeOptions
{
    public SemanticGatewayLoginEndpointOptions? Login { get; set; }

    public SemanticGatewayGameEndpointOptions? Game { get; set; }

    public SemanticGatewayCertificateOptions? GatewayCertificate
    {
        get;
        set;
    }

    public SemanticGatewayLimitOptions? Limits { get; set; }

    public SemanticGatewayAuthorityLimitOptions? Authority { get; set; }

    public SemanticGatewayWorkerOptions[]? Workers { get; set; }

    public SemanticGatewayRouteOptions[]? Routes { get; set; }
}

internal sealed class SemanticGatewayLoginEndpointOptions
{
    public string BindHost { get; set; } = string.Empty;

    public int BindPort { get; set; }
}

internal sealed class SemanticGatewayGameEndpointOptions
{
    public string BindHost { get; set; } = string.Empty;

    public int BindPort { get; set; }

    public string PublicHost { get; set; } = string.Empty;

    public int PublicPort { get; set; }
}

internal sealed class SemanticGatewayCertificateOptions
{
    public string Path { get; set; } = string.Empty;

    public string PasswordEnvironmentVariable { get; set; } =
        "GODSWAR_SEMANTIC_GATEWAY_CERTIFICATE_PASSWORD";
}

internal sealed class SemanticGatewayLimitOptions
{
    public int ListenBacklog { get; set; } = 512;

    public int MaximumConnections { get; set; } = 512;

    public int MaximumUnauthenticatedConnections { get; set; } = 128;

    public int MaximumUnauthenticatedConnectionsPerIp { get; set; } = 4;

    public int MaximumUnauthenticatedConnectionsPerPrefix { get; set; } = 32;

    public int BufferSizeBytes { get; set; } = 16 * 1_024;

    public int FirstPacketTimeoutMilliseconds { get; set; } = 10_000;

    public int IdleTimeoutMilliseconds { get; set; } = 90_000;

    public int DrainTimeoutMilliseconds { get; set; } = 5_000;

    public int MaximumConcurrentBackhaulTlsHandshakes { get; set; } = 32;

    public int BackhaulConnectTimeoutMilliseconds { get; set; } = 2_000;

    public int BackhaulTlsHandshakeTimeoutMilliseconds { get; set; } = 5_000;

    public int BackhaulOpenSessionTimeoutMilliseconds { get; set; } = 2_000;

    public int BackhaulWriteTimeoutMilliseconds { get; set; } = 5_000;
}

internal sealed class SemanticGatewayAuthorityLimitOptions
{
    public int MaximumLoginGenerations { get; set; } = 4_096;

    public int MaximumAdmissions { get; set; } = 4_096;

    public int MaximumAdmissionsPerGeneration { get; set; } = 1;

    public int MaximumExpiryWorkPerOperation { get; set; } = 64;

    public int LoginGenerationTtlSeconds { get; set; } = 15 * 60;

    public int ReservationTtlSeconds { get; set; } = 30;

    public int CommittedAdmissionTtlSeconds { get; set; } = 5 * 60;
}

internal sealed class SemanticGatewayWorkerOptions
{
    public string ServerNodeId { get; set; } = string.Empty;

    public string BackhaulHost { get; set; } = string.Empty;

    public int BackhaulPort { get; set; }

    public string TlsHost { get; set; } = string.Empty;

    public string[]? AllowedWorkerCertificateSha256 { get; set; }

    public int AdmissionCapacity { get; set; }

    public SemanticGatewayWorkerState? InitialState { get; set; }
}

internal sealed class SemanticGatewayRouteOptions
{
    public int RealmId { get; set; }

    public int MapId { get; set; }

    public Guid WorldInstanceId { get; set; }

    public string ServerNodeId { get; set; } = string.Empty;

    public int AdmissionCapacity { get; set; }

    public bool Bootstrap { get; set; }
}
