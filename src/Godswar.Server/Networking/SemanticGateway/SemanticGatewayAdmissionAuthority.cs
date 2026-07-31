using System.Net;

namespace Godswar.Server.Networking.SemanticGateway;

/// <summary>
/// Bounded, thread-safe B18C2 session/admission authority. It owns only
/// disposable gateway coordination; durable player value and fencing remain
/// PostgreSQL responsibilities.
/// </summary>
internal sealed partial class SemanticGatewayAdmissionAuthority
{
    private readonly Dictionary<GatewayAdmissionId, AdmissionEntry>
        _admissions = [];
    private readonly Dictionary<int, GatewayLoginGenerationId>
        _byAccount = [];
    private readonly Dictionary<string, GatewayLoginGenerationId>
        _byUsername = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<GatewayConnectionId, ConnectionOwner>
        _connections = [];
    private readonly SortedSet<ExpiryEntry> _expiry =
        new(ExpiryEntryComparer.Instance);
    private readonly object _gate = new();
    private readonly Dictionary<GatewayLoginGenerationId, LoginGenerationEntry>
        _generations = [];
    private readonly SemanticGatewayAuthorityLimits _limits;
    private readonly StaticSemanticGatewayRouteDirectory _routes;
    private readonly TimeProvider _timeProvider;

    private long _admissionsCommitted;
    private long _admissionsExpired;
    private long _admissionsInvalidated;
    private long _admissionsRefreshed;
    private long _admissionsReleased;
    private long _admissionsReserved;
    private long _admissionsRolledBack;
    private long _bindingRejections;
    private long _capacityRejections;
    private int _committedCount;
    private long _expirySequence;
    private long _identityConflicts;
    private DateTimeOffset _lastObservedUtc = DateTimeOffset.MinValue;
    private long _loginGenerationSequence;
    private long _loginGenerationsStarted;
    private long _loginGenerationsSuperseded;
    private int _reservedCount;
    private long _routeRejections;

    public SemanticGatewayAdmissionAuthority(
        StaticSemanticGatewayRouteDirectory routes,
        SemanticGatewayAuthorityLimits? limits = null,
        TimeProvider? timeProvider = null)
    {
        _routes = routes ??
            throw new ArgumentNullException(nameof(routes));
        _limits = limits ?? new SemanticGatewayAuthorityLimits();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SemanticGatewayLoginResult BeginLogin(
        SemanticGatewayPrincipal principal,
        SemanticGatewayConnectionSource loginSource)
    {
        RequirePrincipal(principal);
        RequireSource(loginSource);

        lock (_gate)
        {
            var now = ObserveUtcNowLocked();
            SweepExpiredLocked(
                now,
                _limits.MaximumExpiryWorkPerOperation);

            _byAccount.TryGetValue(
                principal.AccountId,
                out var accountGenerationId);
            _byUsername.TryGetValue(
                principal.CanonicalUsername!,
                out var usernameGenerationId);

            LoginGenerationEntry? previous = null;
            if (accountGenerationId.IsValid)
            {
                previous = _generations[accountGenerationId];
                if (previous.Principal != principal)
                {
                    _identityConflicts++;
                    return new(
                        SemanticGatewayLoginStatus.IdentityConflict,
                        null,
                        0);
                }
            }
            if (usernameGenerationId.IsValid &&
                usernameGenerationId != accountGenerationId)
            {
                _identityConflicts++;
                return new(
                    SemanticGatewayLoginStatus.IdentityConflict,
                    null,
                    0);
            }

            if (_connections.TryGetValue(
                    loginSource.ConnectionId,
                    out var connectionOwner) &&
                (previous is null ||
                    connectionOwner.GenerationId != previous.Id ||
                    connectionOwner.AdmissionId.IsValid))
            {
                _bindingRejections++;
                return new(
                    SemanticGatewayLoginStatus.ConnectionConflict,
                    null,
                    0);
            }

            var invalidatedAdmissions = 0;
            if (previous is not null)
            {
                invalidatedAdmissions = InvalidateGenerationLocked(
                    previous,
                    GenerationRemovalReason.Superseded);
            }

            if (_generations.Count >=
                _limits.MaximumLoginGenerations)
            {
                _capacityRejections++;
                return new(
                    SemanticGatewayLoginStatus.CapacityExceeded,
                    null,
                    invalidatedAdmissions);
            }

            var generationId = NewGenerationIdLocked();
            var generationSequence =
                checked(++_loginGenerationSequence);
            var expiresAt = AddTtl(now, _limits.LoginGenerationTtl);
            var expiry = NewExpiryLocked(
                expiresAt,
                ExpiryKind.LoginGeneration,
                generationId,
                default);
            var generation = new LoginGenerationEntry(
                generationId,
                generationSequence,
                principal,
                loginSource,
                expiresAt,
                expiry);
            _generations.Add(generationId, generation);
            _byAccount.Add(principal.AccountId, generationId);
            _byUsername.Add(
                principal.CanonicalUsername!,
                generationId);
            _connections.Add(
                loginSource.ConnectionId,
                new(generationId, default));
            _expiry.Add(expiry);
            _loginGenerationsStarted++;

            return new(
                SemanticGatewayLoginStatus.Started,
                CreateLoginLease(generation),
                invalidatedAdmissions);
        }
    }

    public SemanticGatewayAdmissionResult Reserve(
        GatewayLoginGenerationId generationId,
        SemanticGatewayPrincipal principal,
        SemanticGatewayConnectionSource source,
        SemanticGatewayRouteTarget target)
    {
        if (!generationId.IsValid)
        {
            throw new ArgumentException(
                "A valid login-generation ID is required.",
                nameof(generationId));
        }
        RequirePrincipal(principal);
        RequireSource(source);
        if (!target.IsValid)
        {
            throw new ArgumentException(
                "A valid exact route target is required.",
                nameof(target));
        }

        lock (_gate)
        {
            var now = ObserveUtcNowLocked();
            if (_generations.TryGetValue(
                    generationId,
                    out var dueGeneration) &&
                dueGeneration.ExpiresAt <= now)
            {
                InvalidateGenerationLocked(
                    dueGeneration,
                    GenerationRemovalReason.Expired);
                SweepExpiredLocked(
                    now,
                    _limits.MaximumExpiryWorkPerOperation);
                return new(
                    SemanticGatewayAdmissionStatus.GenerationExpired,
                    null);
            }
            SweepExpiredLocked(
                now,
                _limits.MaximumExpiryWorkPerOperation);
            if (!_generations.TryGetValue(
                    generationId,
                    out var generation))
            {
                return new(
                    SemanticGatewayAdmissionStatus.GenerationNotFound,
                    null);
            }
            if (generation.ExpiresAt <= now)
            {
                InvalidateGenerationLocked(
                    generation,
                    GenerationRemovalReason.Expired);
                return new(
                    SemanticGatewayAdmissionStatus.GenerationExpired,
                    null);
            }
            if (generation.Principal != principal)
            {
                _bindingRejections++;
                return new(
                    SemanticGatewayAdmissionStatus.PrincipalMismatch,
                    null);
            }
            if (generation.State !=
                SemanticGatewayLoginGenerationState.Activated)
            {
                return new(
                    SemanticGatewayAdmissionStatus
                        .GenerationNotActivated,
                    null);
            }
            if (_connections.ContainsKey(source.ConnectionId))
            {
                _bindingRejections++;
                return new(
                    SemanticGatewayAdmissionStatus.ConnectionConflict,
                    null);
            }
            if (_admissions.Count >= _limits.MaximumAdmissions)
            {
                _capacityRejections++;
                return new(
                    SemanticGatewayAdmissionStatus.CapacityExceeded,
                    null);
            }
            if (generation.AdmissionsIssued >=
                _limits.MaximumAdmissionsPerGeneration)
            {
                _capacityRejections++;
                return new(
                    SemanticGatewayAdmissionStatus
                        .GenerationCapacityExceeded,
                    null);
            }

            var admissionId = NewAdmissionIdLocked();
            var route = _routes.TryReserve(admissionId, target);
            if (!route.IsSelected)
            {
                _routeRejections++;
                return new(
                    SemanticGatewayAdmissionStatus.RouteRejected,
                    null,
                    route.Status);
            }

            var expiresAt = AddTtl(now, _limits.ReservationTtl);
            var expiry = NewExpiryLocked(
                expiresAt,
                ExpiryKind.Admission,
                default,
                admissionId);
            var admission = new AdmissionEntry(
                admissionId,
                generationId,
                principal,
                source,
                route.Selection!,
                SemanticGatewayAdmissionState.Reserved,
                now,
                expiresAt,
                expiry);
            try
            {
                _admissions.Add(admissionId, admission);
                generation.AdmissionIds.Add(admissionId);
                _connections.Add(
                    source.ConnectionId,
                    new(generationId, admissionId));
                _expiry.Add(expiry);
                generation.AdmissionsIssued++;
                _reservedCount++;
                _admissionsReserved++;
            }
            catch
            {
                _routes.Release(admissionId);
                _admissions.Remove(admissionId);
                generation.AdmissionIds.Remove(admissionId);
                _connections.Remove(source.ConnectionId);
                _expiry.Remove(expiry);
                throw;
            }

            return new(
                SemanticGatewayAdmissionStatus.Reserved,
                CreateAdmissionLease(admission));
        }
    }

    /// <summary>
    /// Finds the active generation revealed by the legacy game-login username.
    /// Username and returned generation form the identity; the normalized
    /// address is only an additional exact binding check. There is
    /// intentionally no address-only lookup.
    /// </summary>
    public SemanticGatewayLoginLookupResult TryFindLogin(
        string canonicalUsername,
        IPAddress observedGameAddress)
    {
        ArgumentNullException.ThrowIfNull(canonicalUsername);
        if (canonicalUsername.Length is < 1 or >
                SemanticGatewayPrincipal.MaximumUsernameLength ||
            canonicalUsername.Any(static value => value is < '!' or > '~'))
        {
            throw new ArgumentException(
                "Canonical username must be exact printable ASCII.",
                nameof(canonicalUsername));
        }
        var normalizedAddress =
            SemanticGatewayConnectionSource.Normalize(
                observedGameAddress ??
                throw new ArgumentNullException(
                    nameof(observedGameAddress)));

        lock (_gate)
        {
            var now = ObserveUtcNowLocked();
            if (_byUsername.TryGetValue(
                    canonicalUsername,
                    out var dueGenerationId) &&
                _generations.TryGetValue(
                    dueGenerationId,
                    out var dueGeneration) &&
                dueGeneration.ExpiresAt <= now)
            {
                InvalidateGenerationLocked(
                    dueGeneration,
                    GenerationRemovalReason.Expired);
                SweepExpiredLocked(
                    now,
                    _limits.MaximumExpiryWorkPerOperation);
                return new(
                    SemanticGatewayLoginLookupStatus.Expired,
                    null);
            }
            SweepExpiredLocked(
                now,
                _limits.MaximumExpiryWorkPerOperation);
            if (!_byUsername.TryGetValue(
                    canonicalUsername,
                    out var generationId) ||
                !_generations.TryGetValue(
                    generationId,
                    out var generation))
            {
                return new(
                    SemanticGatewayLoginLookupStatus.NotFound,
                    null);
            }
            if (generation.ExpiresAt <= now)
            {
                InvalidateGenerationLocked(
                    generation,
                    GenerationRemovalReason.Expired);
                return new(
                    SemanticGatewayLoginLookupStatus.Expired,
                    null);
            }
            if (!Equals(
                    generation.LoginSource.Address,
                    normalizedAddress))
            {
                _bindingRejections++;
                return new(
                    SemanticGatewayLoginLookupStatus
                        .SourceAddressMismatch,
                    null);
            }
            if (generation.State !=
                SemanticGatewayLoginGenerationState.Activated)
            {
                return new(
                    SemanticGatewayLoginLookupStatus.NotActivated,
                    null);
            }

            return new(
                SemanticGatewayLoginLookupStatus.Found,
                CreateLoginLease(generation));
        }
    }

    public int SweepExpired()
    {
        lock (_gate)
        {
            return SweepExpiredLocked(
                ObserveUtcNowLocked(),
                _limits.MaximumExpiryWorkPerOperation);
        }
    }

    public int SweepExpired(int maximumWork)
    {
        if (maximumWork is <= 0 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumWork),
                "Expiry work must be between 1 and 4,096 records.");
        }

        lock (_gate)
        {
            return SweepExpiredLocked(
                ObserveUtcNowLocked(),
                Math.Min(
                    maximumWork,
                    _limits.MaximumExpiryWorkPerOperation));
        }
    }

    public SemanticGatewayAuthoritySnapshot GetSnapshot()
    {
        lock (_gate)
        {
            var now = ObserveUtcNowLocked();
            SweepExpiredLocked(
                now,
                _limits.MaximumExpiryWorkPerOperation);
            return new(
                _generations.Count,
                _limits.MaximumLoginGenerations,
                _reservedCount,
                _committedCount,
                _limits.MaximumAdmissions,
                _loginGenerationsStarted,
                _loginGenerationsSuperseded,
                _identityConflicts,
                _admissionsReserved,
                _admissionsCommitted,
                _admissionsRefreshed,
                _admissionsRolledBack,
                _admissionsReleased,
                _admissionsInvalidated,
                _admissionsExpired,
                _capacityRejections,
                _routeRejections,
                _bindingRejections,
                _routes.GetSnapshot());
        }
    }

    private GatewayLoginGenerationId NewGenerationIdLocked()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = GatewayLoginGenerationId.New();
            if (!_generations.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "Could not allocate a unique login-generation ID.");
    }

    private GatewayAdmissionId NewAdmissionIdLocked()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = GatewayAdmissionId.New();
            if (!_admissions.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "Could not allocate a unique gateway-admission ID.");
    }

    private DateTimeOffset ObserveUtcNowLocked()
    {
        var observed = _timeProvider.GetUtcNow().ToUniversalTime();
        if (observed < _lastObservedUtc)
        {
            return _lastObservedUtc;
        }

        _lastObservedUtc = observed;
        return observed;
    }

    private static DateTimeOffset AddTtl(
        DateTimeOffset now,
        TimeSpan ttl) =>
        now.Add(ttl);

    private static void RequirePrincipal(
        SemanticGatewayPrincipal principal)
    {
        if (!principal.IsValid)
        {
            throw new ArgumentException(
                "A valid semantic-gateway principal is required.",
                nameof(principal));
        }
    }

    private static void RequireSource(
        SemanticGatewayConnectionSource source)
    {
        if (!source.IsValid)
        {
            throw new ArgumentException(
                "A valid gateway connection/source binding is required.",
                nameof(source));
        }
    }
}
