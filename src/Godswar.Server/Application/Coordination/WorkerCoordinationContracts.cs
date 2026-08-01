using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.Coordination;

internal enum CoordinationOperationStatus : byte
{
    Applied = 1,
    Current = 2,
    NotFound = 3,
    Conflict = 4,
    Unavailable = 5,
    Overloaded = 6,
    CircuitOpen = 7,
    DeadlineExceeded = 8
}

internal enum CoordinatedWorkerState : byte
{
    Available = 1,
    Draining = 2
}

internal enum CoordinatedPresenceState : byte
{
    EnteringWorld = 1,
    Online = 2,
    Draining = 3
}

internal readonly record struct CoordinationDeadline(
    DateTimeOffset ExpiresAtUtc)
{
    public static CoordinationDeadline FromNow(
        TimeSpan timeout,
        TimeProvider? timeProvider = null)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        return new CoordinationDeadline(
            (timeProvider ?? TimeProvider.System).GetUtcNow() + timeout);
    }

    public TimeSpan Remaining(TimeProvider? timeProvider = null)
    {
        var remaining =
            ExpiresAtUtc -
            (timeProvider ?? TimeProvider.System).GetUtcNow();
        return remaining > TimeSpan.Zero
            ? remaining
            : TimeSpan.Zero;
    }
}

internal readonly record struct CoordinatedWorldRoute(
    RealmId RealmId,
    MapId MapId,
    WorldInstanceId WorldInstanceId)
{
    public bool IsValid =>
        RealmId.IsValid &&
        MapId.IsValid &&
        WorldInstanceId.IsValid;

    public void Validate()
    {
        if (!IsValid)
        {
            throw new ArgumentException(
                "A coordinated route requires valid realm, map, and " +
                "world-instance identities.");
        }
    }
}

internal sealed record WorkerRegistrationRequest
{
    public const int MaximumBuildRevisionLength = 64;
    public const int MaximumContentRevisionLength = 64;
    public const int MaximumCapabilityLength = 32;
    public const int MaximumCapabilities = 16;
    public const int MaximumRoutes = 65_536;

    public required ServerNodeId NodeId { get; init; }

    public required Guid BootId { get; init; }

    public required string BuildRevision { get; init; }

    public required string ContentRevision { get; init; }

    public required CoordinatedWorkerState State { get; init; }

    public required IReadOnlyList<string> Capabilities { get; init; }

    public required IReadOnlyList<CoordinatedWorldRoute> Routes { get; init; }

    public RealmId RealmId =>
        Routes.Count == 0 ? default : Routes[0].RealmId;

    public void Validate()
    {
        if (!NodeId.IsValid)
        {
            throw new ArgumentException("A valid server node is required.");
        }
        if (BootId == Guid.Empty)
        {
            throw new ArgumentException("A nonzero worker boot ID is required.");
        }
        ValidateToken(
            BuildRevision,
            MaximumBuildRevisionLength,
            nameof(BuildRevision));
        ValidateToken(
            ContentRevision,
            MaximumContentRevisionLength,
            nameof(ContentRevision));
        if (!Enum.IsDefined(State))
        {
            throw new ArgumentOutOfRangeException(nameof(State));
        }
        if (Capabilities.Count > MaximumCapabilities ||
            Capabilities.Distinct(StringComparer.Ordinal).Count() !=
                Capabilities.Count)
        {
            throw new ArgumentException(
                "Worker capabilities must be bounded and unique.");
        }
        foreach (var capability in Capabilities)
        {
            ValidateToken(
                capability,
                MaximumCapabilityLength,
                nameof(Capabilities));
        }
        if (Routes.Count is < 1 or > MaximumRoutes ||
            Routes.Distinct().Count() != Routes.Count)
        {
            throw new ArgumentException(
                "Worker routes must be bounded, nonempty, and unique.");
        }
        foreach (var route in Routes)
        {
            route.Validate();
        }
        if (Routes.Any(route => route.RealmId != RealmId))
        {
            throw new ArgumentException(
                "One worker registration may own routes in only one realm.");
        }
    }

    private static void ValidateToken(
        string value,
        int maximumLength,
        string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > maximumLength ||
            value.Any(character =>
                character is < (char)0x21 or > (char)0x7E))
        {
            throw new ArgumentException(
                "Coordination metadata must be bounded printable ASCII.",
                name);
        }
    }
}

internal readonly record struct WorkerRegistrationLease(
    ServerNodeId NodeId,
    Guid BootId,
    long Revision,
    CoordinatedWorkerState State,
    DateTimeOffset ProvenUntilUtc)
{
    public bool IsValid =>
        NodeId.IsValid &&
        BootId != Guid.Empty &&
        Revision > 0 &&
        Enum.IsDefined(State) &&
        ProvenUntilUtc > DateTimeOffset.UnixEpoch;
}

internal readonly record struct WorkerRegistrationResult(
    CoordinationOperationStatus Status,
    WorkerRegistrationLease? Lease)
{
    public bool Succeeded =>
        Status is
            CoordinationOperationStatus.Applied or
            CoordinationOperationStatus.Current &&
        Lease is not null;
}

internal sealed record CoordinatedRouteSnapshot(
    CoordinatedWorldRoute Route,
    ServerNodeId NodeId,
    Guid BootId,
    long Revision,
    CoordinatedWorkerState WorkerState,
    string BuildRevision,
    string ContentRevision,
    DateTimeOffset ProvenUntilUtc);

internal readonly record struct CoordinatedRouteLookup(
    CoordinationOperationStatus Status,
    CoordinatedRouteSnapshot? Route)
{
    public bool IsFound =>
        Status == CoordinationOperationStatus.Current &&
        Route is not null;
}

internal sealed record PlayerLeaseInstallRequest
{
    public required int AccountId { get; init; }

    public required int CharacterId { get; init; }

    public required PlayerOwnershipFence Ownership { get; init; }

    public required Guid LeaseToken { get; init; }

    public required ServerNodeId NodeId { get; init; }

    public required Guid WorkerBootId { get; init; }

    public required CoordinatedWorldRoute Route { get; init; }

    public required CoordinatedPresenceState Presence { get; init; }

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(AccountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(CharacterId);
        Ownership.Validate();
        if (LeaseToken == Guid.Empty)
        {
            throw new ArgumentException("A nonzero lease token is required.");
        }
        if (!NodeId.IsValid || WorkerBootId == Guid.Empty)
        {
            throw new ArgumentException(
                "A valid worker identity is required.");
        }
        Route.Validate();
        if (!Enum.IsDefined(Presence))
        {
            throw new ArgumentOutOfRangeException(nameof(Presence));
        }
    }
}

internal sealed record CoordinatedPlayerLease(
    int AccountId,
    int CharacterId,
    PlayerOwnershipFence Ownership,
    Guid LeaseToken,
    ServerNodeId NodeId,
    Guid WorkerBootId,
    CoordinatedWorldRoute Route,
    CoordinatedPresenceState Presence,
    long Version,
    DateTimeOffset ProvenUntilUtc)
{
    public bool IsValid =>
        AccountId > 0 &&
        CharacterId > 0 &&
        Ownership.IsValid &&
        LeaseToken != Guid.Empty &&
        NodeId.IsValid &&
        WorkerBootId != Guid.Empty &&
        Route.IsValid &&
        Enum.IsDefined(Presence) &&
        Version > 0 &&
        ProvenUntilUtc > DateTimeOffset.UnixEpoch;
}

internal readonly record struct PlayerLeaseResult(
    CoordinationOperationStatus Status,
    CoordinatedPlayerLease? Lease)
{
    public bool Succeeded =>
        Status is
            CoordinationOperationStatus.Applied or
            CoordinationOperationStatus.Current &&
        Lease is not null;
}

internal readonly record struct PlayerLeaseLookup(
    CoordinationOperationStatus Status,
    CoordinatedPlayerLease? Lease)
{
    public bool IsFound =>
        Status == CoordinationOperationStatus.Current &&
        Lease is not null;
}

internal readonly record struct WorkerCoordinationSnapshot(
    bool IsReady,
    int Capacity,
    int MaximumConcurrentOperations,
    int InFlightOperations,
    int RegisteredRoutes,
    int ActivePlayerLeases,
    long AcceptedOperations,
    long ConflictOperations,
    long TimeoutOperations,
    long UnavailableOperations,
    long OverloadRejections,
    long CircuitOpenRejections,
    DateTimeOffset LastSuccessAtUtc);

internal interface IWorkerCoordination : IAsyncDisposable
{
    ValueTask<WorkerRegistrationResult> RegisterWorkerAsync(
        WorkerRegistrationRequest request,
        TimeSpan ttl,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default);

    ValueTask<WorkerRegistrationResult> RenewWorkerAsync(
        WorkerRegistrationLease lease,
        CoordinatedWorkerState state,
        TimeSpan ttl,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default);

    ValueTask<CoordinationOperationStatus> ReleaseWorkerAsync(
        WorkerRegistrationLease lease,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default);

    ValueTask<CoordinatedRouteLookup> FindRouteAsync(
        CoordinatedWorldRoute route,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default);

    ValueTask<PlayerLeaseResult> InstallPlayerLeaseAsync(
        PlayerLeaseInstallRequest request,
        TimeSpan ttl,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default);

    ValueTask<PlayerLeaseResult> RenewPlayerLeaseAsync(
        CoordinatedPlayerLease lease,
        CoordinatedWorldRoute route,
        CoordinatedPresenceState presence,
        TimeSpan ttl,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default);

    ValueTask<CoordinationOperationStatus> ReleasePlayerLeaseAsync(
        CoordinatedPlayerLease lease,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default);

    ValueTask<PlayerLeaseLookup> FindPlayerLeaseAsync(
        int characterId,
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default);

    ValueTask<bool> CheckHealthAsync(
        CoordinationDeadline deadline,
        CancellationToken cancellationToken = default);

    WorkerCoordinationSnapshot GetSnapshot();
}

internal interface IWorkerCoordinationReadinessSource
{
    bool IsReady { get; }

    WorkerCoordinationSnapshot GetSnapshot();
}
