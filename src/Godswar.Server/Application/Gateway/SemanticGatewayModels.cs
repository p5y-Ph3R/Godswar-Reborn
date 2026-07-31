using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.Gateway;

internal enum SemanticGatewayWorkerState : byte
{
    Available = 1,
    Draining = 2,
    Unavailable = 3
}

internal enum SemanticGatewayWorkerUpdateStatus : byte
{
    Updated = 1,
    NoChange = 2,
    WorkerNotFound = 20,
    RevisionConflict = 21
}

internal enum SemanticGatewayRouteSelectionStatus : byte
{
    Selected = 1,
    RouteNotFound = 20,
    RouteIdentityMismatch = 21,
    WorkerNotFound = 22,
    WorkerDraining = 23,
    WorkerUnavailable = 24,
    WorkerCapacityExceeded = 25,
    RouteCapacityExceeded = 26,
    DuplicateAdmission = 27,
    DirectoryCapacityExceeded = 28
}

internal enum SemanticGatewayLoginStatus : byte
{
    Started = 1,
    IdentityConflict = 20,
    ConnectionConflict = 21,
    CapacityExceeded = 22
}

internal enum SemanticGatewayLoginLookupStatus : byte
{
    Found = 1,
    NotFound = 20,
    Expired = 21,
    SourceAddressMismatch = 22,
    NotActivated = 23
}

internal enum SemanticGatewayAdmissionStatus : byte
{
    Reserved = 1,
    Committed = 2,
    Refreshed = 3,
    RolledBack = 4,
    Released = 5,
    GenerationNotFound = 20,
    GenerationExpired = 21,
    PrincipalMismatch = 22,
    ConnectionConflict = 23,
    CapacityExceeded = 24,
    GenerationCapacityExceeded = 25,
    RouteRejected = 26,
    AdmissionNotFound = 27,
    AdmissionExpired = 28,
    BindingMismatch = 29,
    StateConflict = 30,
    GenerationNotActivated = 31
}

internal enum SemanticGatewayLoginGenerationState : byte
{
    Pending = 1,
    Activated = 2
}

internal enum SemanticGatewayAdmissionState : byte
{
    Reserved = 1,
    Committed = 2
}

/// <summary>
/// Exact requested simulation identity. The gateway never substitutes a
/// same-map instance when the supplied world-instance ID does not match.
/// </summary>
internal readonly record struct SemanticGatewayRouteTarget
{
    public SemanticGatewayRouteTarget(
        RealmId realmId,
        MapId mapId,
        WorldInstanceId worldInstanceId)
    {
        if (!realmId.IsValid)
        {
            throw new ArgumentException(
                "A valid realm ID is required.",
                nameof(realmId));
        }
        if (!mapId.IsValid)
        {
            throw new ArgumentException(
                "A valid map ID is required.",
                nameof(mapId));
        }
        if (!worldInstanceId.IsValid)
        {
            throw new ArgumentException(
                "A valid world-instance ID is required.",
                nameof(worldInstanceId));
        }

        RealmId = realmId;
        MapId = mapId;
        WorldInstanceId = worldInstanceId;
    }

    public RealmId RealmId { get; }

    public MapId MapId { get; }

    public WorldInstanceId WorldInstanceId { get; }

    public bool IsValid =>
        RealmId.IsValid &&
        MapId.IsValid &&
        WorldInstanceId.IsValid;
}

internal sealed record SemanticGatewayWorkerDefinition
{
    public SemanticGatewayWorkerDefinition(
        ServerNodeId nodeId,
        int admissionCapacity,
        SemanticGatewayWorkerState initialState =
            SemanticGatewayWorkerState.Available)
    {
        if (!nodeId.IsValid)
        {
            throw new ArgumentException(
                "A valid server-node ID is required.",
                nameof(nodeId));
        }
        if (admissionCapacity is <= 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(admissionCapacity),
                "Worker admission capacity must be between 1 and 100,000.");
        }
        if (!Enum.IsDefined(initialState))
        {
            throw new ArgumentOutOfRangeException(nameof(initialState));
        }

        NodeId = nodeId;
        AdmissionCapacity = admissionCapacity;
        InitialState = initialState;
    }

    public ServerNodeId NodeId { get; }

    public int AdmissionCapacity { get; }

    public SemanticGatewayWorkerState InitialState { get; }
}

internal sealed record SemanticGatewayStaticRoute
{
    public SemanticGatewayStaticRoute(
        RealmId realmId,
        MapId mapId,
        WorldInstanceId worldInstanceId,
        ServerNodeId nodeId,
        int admissionCapacity)
    {
        Target = new SemanticGatewayRouteTarget(
            realmId,
            mapId,
            worldInstanceId);
        if (!nodeId.IsValid)
        {
            throw new ArgumentException(
                "A valid route server-node ID is required.",
                nameof(nodeId));
        }
        if (admissionCapacity is <= 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(admissionCapacity),
                "Route admission capacity must be between 1 and 100,000.");
        }

        NodeId = nodeId;
        AdmissionCapacity = admissionCapacity;
    }

    public SemanticGatewayRouteTarget Target { get; }

    public ServerNodeId NodeId { get; }

    public int AdmissionCapacity { get; }
}

internal sealed record SemanticGatewayRouteSelection(
    SemanticGatewayRouteTarget Target,
    ServerNodeId NodeId,
    long WorkerRevision);

internal readonly record struct SemanticGatewayRouteSelectionResult(
    SemanticGatewayRouteSelectionStatus Status,
    SemanticGatewayRouteSelection? Selection)
{
    public bool IsSelected =>
        Status == SemanticGatewayRouteSelectionStatus.Selected &&
        Selection is not null;
}

internal readonly record struct SemanticGatewayWorkerUpdateResult(
    SemanticGatewayWorkerUpdateStatus Status,
    SemanticGatewayWorkerSnapshot? Worker);

internal sealed record SemanticGatewayLoginGenerationLease(
    GatewayLoginGenerationId GenerationId,
    long Sequence,
    SemanticGatewayPrincipal Principal,
    SemanticGatewayConnectionSource LoginSource,
    DateTimeOffset ExpiresAt);

internal readonly record struct SemanticGatewayLoginResult(
    SemanticGatewayLoginStatus Status,
    SemanticGatewayLoginGenerationLease? Generation,
    int InvalidatedAdmissions)
{
    public bool IsStarted =>
        Status == SemanticGatewayLoginStatus.Started &&
        Generation is not null;
}

internal readonly record struct SemanticGatewayLoginLookupResult(
    SemanticGatewayLoginLookupStatus Status,
    SemanticGatewayLoginGenerationLease? Generation)
{
    public bool IsFound =>
        Status == SemanticGatewayLoginLookupStatus.Found &&
        Generation is not null;
}

internal sealed record SemanticGatewayAdmissionLease(
    GatewayAdmissionId AdmissionId,
    GatewayLoginGenerationId GenerationId,
    SemanticGatewayPrincipal Principal,
    SemanticGatewayConnectionSource Source,
    SemanticGatewayRouteSelection Route,
    SemanticGatewayAdmissionState State,
    DateTimeOffset ReservedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Full worker-side admission claim. Every field participates in validation;
/// neither an IP address nor an admission ID alone can authorize a session.
/// </summary>
internal sealed record SemanticGatewayAdmissionClaim(
    GatewayAdmissionId AdmissionId,
    GatewayLoginGenerationId GenerationId,
    SemanticGatewayPrincipal Principal,
    SemanticGatewayConnectionSource Source,
    SemanticGatewayRouteTarget Target,
    ServerNodeId NodeId,
    long WorkerRevision);

internal readonly record struct SemanticGatewayAdmissionResult(
    SemanticGatewayAdmissionStatus Status,
    SemanticGatewayAdmissionLease? Admission,
    SemanticGatewayRouteSelectionStatus? RouteRejection = null)
{
    public bool Succeeded =>
        Status is SemanticGatewayAdmissionStatus.Reserved or
            SemanticGatewayAdmissionStatus.Committed or
            SemanticGatewayAdmissionStatus.Refreshed or
            SemanticGatewayAdmissionStatus.RolledBack or
            SemanticGatewayAdmissionStatus.Released;
}

internal sealed record SemanticGatewayWorkerSnapshot(
    ServerNodeId NodeId,
    SemanticGatewayWorkerState State,
    long Revision,
    int ActiveAdmissions,
    int AdmissionCapacity,
    int RouteCount);

internal sealed record SemanticGatewayRouteDirectorySnapshot(
    int WorkerCount,
    int RouteCount,
    int ActiveReservations,
    int AvailableWorkers,
    int DrainingWorkers,
    int UnavailableWorkers,
    IReadOnlyList<SemanticGatewayWorkerSnapshot> Workers);

internal sealed record SemanticGatewayAuthoritySnapshot(
    int ActiveLoginGenerations,
    int LoginGenerationCapacity,
    int ReservedAdmissions,
    int CommittedAdmissions,
    int AdmissionCapacity,
    long LoginGenerationsStarted,
    long LoginGenerationsSuperseded,
    long IdentityConflicts,
    long AdmissionsReserved,
    long AdmissionsCommitted,
    long AdmissionsRefreshed,
    long AdmissionsRolledBack,
    long AdmissionsReleased,
    long AdmissionsInvalidated,
    long AdmissionsExpired,
    long CapacityRejections,
    long RouteRejections,
    long BindingRejections,
    SemanticGatewayRouteDirectorySnapshot Routes);

internal sealed record SemanticGatewayAuthorityLimits
{
    public SemanticGatewayAuthorityLimits(
        int maximumLoginGenerations = 4_096,
        int maximumAdmissions = 4_096,
        int maximumAdmissionsPerGeneration = 1,
        int maximumExpiryWorkPerOperation = 64,
        TimeSpan? loginGenerationTtl = null,
        TimeSpan? reservationTtl = null,
        TimeSpan? committedAdmissionTtl = null)
    {
        MaximumLoginGenerations = RequireRange(
            maximumLoginGenerations,
            1,
            100_000,
            nameof(maximumLoginGenerations));
        MaximumAdmissions = RequireRange(
            maximumAdmissions,
            1,
            100_000,
            nameof(maximumAdmissions));
        MaximumAdmissionsPerGeneration = RequireRange(
            maximumAdmissionsPerGeneration,
            1,
            32,
            nameof(maximumAdmissionsPerGeneration));
        MaximumExpiryWorkPerOperation = RequireRange(
            maximumExpiryWorkPerOperation,
            1,
            4_096,
            nameof(maximumExpiryWorkPerOperation));

        LoginGenerationTtl = RequireTtl(
            loginGenerationTtl ?? TimeSpan.FromMinutes(15),
            nameof(loginGenerationTtl));
        ReservationTtl = RequireTtl(
            reservationTtl ?? TimeSpan.FromSeconds(30),
            nameof(reservationTtl));
        CommittedAdmissionTtl = RequireTtl(
            committedAdmissionTtl ?? TimeSpan.FromMinutes(5),
            nameof(committedAdmissionTtl));
        if (LoginGenerationTtl < ReservationTtl ||
            LoginGenerationTtl < CommittedAdmissionTtl)
        {
            throw new ArgumentException(
                "Login-generation TTL must cover reservation and committed " +
                "admission TTLs.");
        }
    }

    public int MaximumLoginGenerations { get; }

    public int MaximumAdmissions { get; }

    public int MaximumAdmissionsPerGeneration { get; }

    public int MaximumExpiryWorkPerOperation { get; }

    public TimeSpan LoginGenerationTtl { get; }

    public TimeSpan ReservationTtl { get; }

    public TimeSpan CommittedAdmissionTtl { get; }

    private static int RequireRange(
        int value,
        int minimum,
        int maximum,
        string name)
    {
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                name,
                $"Value must be between {minimum} and {maximum}.");
        }

        return value;
    }

    private static TimeSpan RequireTtl(TimeSpan value, string name)
    {
        if (value < TimeSpan.FromSeconds(1) ||
            value > TimeSpan.FromHours(24))
        {
            throw new ArgumentOutOfRangeException(
                name,
                "TTL must be between one second and 24 hours.");
        }

        return value;
    }
}
