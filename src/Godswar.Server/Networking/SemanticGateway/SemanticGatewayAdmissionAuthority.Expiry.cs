namespace Godswar.Server.Networking.SemanticGateway;

internal sealed partial class SemanticGatewayAdmissionAuthority
{
    private int SweepExpiredLocked(
        DateTimeOffset now,
        int maximumWork)
    {
        var processed = 0;
        while (processed < maximumWork &&
            _expiry.Count > 0)
        {
            var next = _expiry.Min;
            if (next.ExpiresAt > now)
            {
                break;
            }

            _expiry.Remove(next);
            processed++;
            if (next.Kind == ExpiryKind.Admission)
            {
                if (_admissions.TryGetValue(
                        next.AdmissionId,
                        out var admission) &&
                    admission.Expiry == next)
                {
                    RemoveAdmissionLocked(
                        admission,
                        AdmissionRemovalReason.Expired);
                }
            }
            else if (next.Kind == ExpiryKind.LoginGeneration &&
                _generations.TryGetValue(
                    next.GenerationId,
                    out var generation) &&
                generation.Expiry == next)
            {
                InvalidateGenerationLocked(
                    generation,
                    GenerationRemovalReason.Expired);
            }
        }

        return processed;
    }

    private int InvalidateGenerationLocked(
        LoginGenerationEntry generation,
        GenerationRemovalReason reason)
    {
        if (!_generations.Remove(generation.Id))
        {
            return 0;
        }

        _expiry.Remove(generation.Expiry);
        _byAccount.Remove(generation.Principal.AccountId);
        _byUsername.Remove(generation.Principal.CanonicalUsername!);
        _connections.Remove(generation.LoginSource.ConnectionId);

        var admissionIds = generation.AdmissionIds.ToArray();
        foreach (var admissionId in admissionIds)
        {
            if (_admissions.TryGetValue(
                    admissionId,
                    out var admission))
            {
                RemoveAdmissionLocked(
                    admission,
                    reason == GenerationRemovalReason.Expired
                        ? AdmissionRemovalReason.Expired
                        : AdmissionRemovalReason.Invalidated);
            }
        }

        if (reason == GenerationRemovalReason.Superseded)
        {
            _loginGenerationsSuperseded++;
        }

        return admissionIds.Length;
    }

    private void RemoveAdmissionLocked(
        AdmissionEntry admission,
        AdmissionRemovalReason reason)
    {
        if (!_admissions.Remove(admission.Id))
        {
            return;
        }

        _expiry.Remove(admission.Expiry);
        _connections.Remove(admission.Source.ConnectionId);
        if (_generations.TryGetValue(
                admission.GenerationId,
                out var generation))
        {
            generation.AdmissionIds.Remove(admission.Id);
        }
        if (!_routes.Release(admission.Id))
        {
            throw new InvalidOperationException(
                "Gateway admission lost its static route reservation.");
        }

        if (admission.State == SemanticGatewayAdmissionState.Reserved)
        {
            if (_reservedCount <= 0)
            {
                throw new InvalidOperationException(
                    "Reserved admission accounting underflow.");
            }
            _reservedCount--;
        }
        else
        {
            if (_committedCount <= 0)
            {
                throw new InvalidOperationException(
                    "Committed admission accounting underflow.");
            }
            _committedCount--;
        }

        switch (reason)
        {
            case AdmissionRemovalReason.RolledBack:
                _admissionsRolledBack++;
                break;
            case AdmissionRemovalReason.Released:
                _admissionsReleased++;
                break;
            case AdmissionRemovalReason.Invalidated:
                _admissionsInvalidated++;
                break;
            case AdmissionRemovalReason.Expired:
                _admissionsExpired++;
                break;
        }
    }

    private void UpdateAdmissionExpiryLocked(
        AdmissionEntry admission,
        DateTimeOffset expiresAt)
    {
        _expiry.Remove(admission.Expiry);
        admission.ExpiresAt = expiresAt;
        admission.Expiry = NewExpiryLocked(
            expiresAt,
            ExpiryKind.Admission,
            default,
            admission.Id);
        _expiry.Add(admission.Expiry);
    }

    private void UpdateGenerationExpiryLocked(
        LoginGenerationEntry generation,
        DateTimeOffset expiresAt)
    {
        _expiry.Remove(generation.Expiry);
        generation.ExpiresAt = expiresAt;
        generation.Expiry = NewExpiryLocked(
            expiresAt,
            ExpiryKind.LoginGeneration,
            generation.Id,
            default);
        _expiry.Add(generation.Expiry);
    }

    private ExpiryEntry NewExpiryLocked(
        DateTimeOffset expiresAt,
        ExpiryKind kind,
        GatewayLoginGenerationId generationId,
        GatewayAdmissionId admissionId)
    {
        var sequence = checked(++_expirySequence);
        return new(
            expiresAt,
            sequence,
            kind,
            generationId,
            admissionId);
    }

    private static SemanticGatewayLoginGenerationLease CreateLoginLease(
        LoginGenerationEntry generation) =>
        new(
            generation.Id,
            generation.Sequence,
            generation.Principal,
            generation.LoginSource,
            generation.RealmGrant,
            generation.ExpiresAt);

    private static SemanticGatewayAdmissionLease CreateAdmissionLease(
        AdmissionEntry admission) =>
        new(
            admission.Id,
            admission.GenerationId,
            admission.Principal,
            admission.Source,
            admission.Route,
            admission.State,
            admission.ReservedAt,
            admission.ExpiresAt);

    private enum ExpiryKind : byte
    {
        LoginGeneration = 1,
        Admission = 2
    }

    private enum GenerationRemovalReason : byte
    {
        Superseded = 1,
        Expired = 2,
        Cancelled = 3
    }

    private enum AdmissionRemovalReason : byte
    {
        RolledBack = 1,
        Released = 2,
        Invalidated = 3,
        Expired = 4
    }

    private readonly record struct ConnectionOwner(
        GatewayLoginGenerationId GenerationId,
        GatewayAdmissionId AdmissionId);

    private readonly record struct ExpiryEntry(
        DateTimeOffset ExpiresAt,
        long Sequence,
        ExpiryKind Kind,
        GatewayLoginGenerationId GenerationId,
        GatewayAdmissionId AdmissionId);

    private sealed class ExpiryEntryComparer : IComparer<ExpiryEntry>
    {
        public static readonly ExpiryEntryComparer Instance = new();

        public int Compare(ExpiryEntry left, ExpiryEntry right)
        {
            var result = left.ExpiresAt.CompareTo(right.ExpiresAt);
            return result != 0
                ? result
                : left.Sequence.CompareTo(right.Sequence);
        }
    }

    private sealed class LoginGenerationEntry
    {
        public LoginGenerationEntry(
            GatewayLoginGenerationId id,
            long sequence,
            SemanticGatewayPrincipal principal,
            SemanticGatewayConnectionSource loginSource,
            SemanticGatewayRealmGrant realmGrant,
            DateTimeOffset expiresAt,
            ExpiryEntry expiry)
        {
            Id = id;
            Sequence = sequence;
            Principal = principal;
            LoginSource = loginSource;
            RealmGrant = realmGrant ??
                throw new ArgumentNullException(nameof(realmGrant));
            ExpiresAt = expiresAt;
            Expiry = expiry;
        }

        public GatewayLoginGenerationId Id { get; }

        public long Sequence { get; }

        public SemanticGatewayPrincipal Principal { get; }

        public SemanticGatewayConnectionSource LoginSource { get; }

        public SemanticGatewayRealmGrant RealmGrant { get; }

        public DateTimeOffset ExpiresAt { get; set; }

        public ExpiryEntry Expiry { get; set; }

        public SemanticGatewayLoginGenerationState State { get; set; } =
            SemanticGatewayLoginGenerationState.Pending;

        public int AdmissionsIssued { get; set; }

        public HashSet<GatewayAdmissionId> AdmissionIds { get; } = [];
    }

    private sealed class AdmissionEntry
    {
        public AdmissionEntry(
            GatewayAdmissionId id,
            GatewayLoginGenerationId generationId,
            SemanticGatewayPrincipal principal,
            SemanticGatewayConnectionSource source,
            SemanticGatewayRouteSelection route,
            SemanticGatewayAdmissionState state,
            DateTimeOffset reservedAt,
            DateTimeOffset expiresAt,
            ExpiryEntry expiry)
        {
            Id = id;
            GenerationId = generationId;
            Principal = principal;
            Source = source;
            Route = route;
            State = state;
            ReservedAt = reservedAt;
            ExpiresAt = expiresAt;
            Expiry = expiry;
        }

        public GatewayAdmissionId Id { get; }

        public GatewayLoginGenerationId GenerationId { get; }

        public SemanticGatewayPrincipal Principal { get; }

        public SemanticGatewayConnectionSource Source { get; }

        public SemanticGatewayRouteSelection Route { get; }

        public SemanticGatewayAdmissionState State { get; set; }

        public DateTimeOffset ReservedAt { get; }

        public DateTimeOffset ExpiresAt { get; set; }

        public ExpiryEntry Expiry { get; set; }
    }
}
