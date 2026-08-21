namespace Godswar.Server.Networking.SemanticGateway;

internal sealed partial class SemanticGatewayAdmissionAuthority
{
    public bool ActivateLogin(
        SemanticGatewayLoginGenerationLease generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        lock (_gate)
        {
            var now = ObserveUtcNowLocked();
            SweepExpiredLocked(
                now,
                _limits.MaximumExpiryWorkPerOperation);
            if (!TryFindGenerationLocked(generation, out var current))
            {
                return false;
            }
            if (current.ExpiresAt <= now)
            {
                InvalidateGenerationLocked(
                    current,
                    GenerationRemovalReason.Expired);
                return false;
            }
            if (current.State !=
                SemanticGatewayLoginGenerationState.Pending)
            {
                return false;
            }

            current.State =
                SemanticGatewayLoginGenerationState.Activated;
            return true;
        }
    }

    public bool CancelLogin(
        SemanticGatewayLoginGenerationLease generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        lock (_gate)
        {
            var now = ObserveUtcNowLocked();
            SweepExpiredLocked(
                now,
                _limits.MaximumExpiryWorkPerOperation);
            if (!TryFindGenerationLocked(generation, out var current))
            {
                return false;
            }

            InvalidateGenerationLocked(
                current,
                GenerationRemovalReason.Cancelled);
            return true;
        }
    }

    public SemanticGatewayAdmissionResult Commit(
        SemanticGatewayAdmissionClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        lock (_gate)
        {
            var now = ObserveUtcNowLocked();
            var found = FindClaimForOperationLocked(claim, now);
            if (found.Status != SemanticGatewayAdmissionStatus.Committed)
            {
                return new(found.Status, null);
            }

            var admission = found.Admission!;
            if (admission.State != SemanticGatewayAdmissionState.Reserved)
            {
                return new(
                    SemanticGatewayAdmissionStatus.StateConflict,
                    CreateAdmissionLease(admission));
            }

            var routeStatus = _routes.ValidateReservation(
                admission.Id,
                admission.Route);
            if (routeStatus !=
                SemanticGatewayRouteSelectionStatus.Selected)
            {
                _routeRejections++;
                RemoveAdmissionLocked(
                    admission,
                    AdmissionRemovalReason.Invalidated);
                return new(
                    SemanticGatewayAdmissionStatus.RouteRejected,
                    null,
                    routeStatus);
            }

            UpdateAdmissionExpiryLocked(
                admission,
                AddTtl(now, _limits.CommittedAdmissionTtl));
            admission.State = SemanticGatewayAdmissionState.Committed;
            _reservedCount--;
            _committedCount++;
            _admissionsCommitted++;
            return new(
                SemanticGatewayAdmissionStatus.Committed,
                CreateAdmissionLease(admission));
        }
    }

    public SemanticGatewayAdmissionResult RefreshCommitted(
        SemanticGatewayAdmissionClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        lock (_gate)
        {
            var now = ObserveUtcNowLocked();
            var found = FindClaimForOperationLocked(claim, now);
            if (found.Status != SemanticGatewayAdmissionStatus.Committed)
            {
                return new(found.Status, null);
            }

            var admission = found.Admission!;
            if (admission.State != SemanticGatewayAdmissionState.Committed)
            {
                return new(
                    SemanticGatewayAdmissionStatus.StateConflict,
                    CreateAdmissionLease(admission));
            }

            var routeStatus = _routes.ValidateActiveReservation(
                admission.Id,
                admission.Route);
            if (routeStatus !=
                SemanticGatewayRouteSelectionStatus.Selected)
            {
                _routeRejections++;
                return new(
                    SemanticGatewayAdmissionStatus.RouteRejected,
                    null,
                    routeStatus);
            }

            var expiresAt = AddTtl(
                now,
                _limits.CommittedAdmissionTtl);
            UpdateAdmissionExpiryLocked(admission, expiresAt);
            if (_generations.TryGetValue(
                    admission.GenerationId,
                    out var generation))
            {
                var generationExpiry = AddTtl(
                    now,
                    _limits.LoginGenerationTtl);
                UpdateGenerationExpiryLocked(
                    generation,
                    generationExpiry);
            }

            _admissionsRefreshed++;
            return new(
                SemanticGatewayAdmissionStatus.Refreshed,
                CreateAdmissionLease(admission));
        }
    }

    public SemanticGatewayAdmissionResult ResolveCommitted(
        SemanticGatewayAdmissionClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        lock (_gate)
        {
            var now = ObserveUtcNowLocked();
            var found = FindClaimForOperationLocked(claim, now);
            if (found.Status != SemanticGatewayAdmissionStatus.Committed)
            {
                return new(found.Status, null);
            }
            if (found.Admission!.State !=
                SemanticGatewayAdmissionState.Committed)
            {
                return new(
                    SemanticGatewayAdmissionStatus.StateConflict,
                    CreateAdmissionLease(found.Admission));
            }

            return new(
                SemanticGatewayAdmissionStatus.Committed,
                CreateAdmissionLease(found.Admission));
        }
    }

    public SemanticGatewayAdmissionResult Rollback(
        SemanticGatewayAdmissionClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        lock (_gate)
        {
            var now = ObserveUtcNowLocked();
            var found = FindClaimForOperationLocked(claim, now);
            if (found.Status != SemanticGatewayAdmissionStatus.Committed)
            {
                return new(found.Status, null);
            }
            if (found.Admission!.State !=
                SemanticGatewayAdmissionState.Reserved)
            {
                return new(
                    SemanticGatewayAdmissionStatus.StateConflict,
                    CreateAdmissionLease(found.Admission));
            }

            var lease = CreateAdmissionLease(found.Admission);
            RemoveAdmissionLocked(
                found.Admission,
                AdmissionRemovalReason.RolledBack);
            return new(
                SemanticGatewayAdmissionStatus.RolledBack,
                lease);
        }
    }

    public SemanticGatewayAdmissionResult Release(
        SemanticGatewayAdmissionClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        lock (_gate)
        {
            var now = ObserveUtcNowLocked();
            var found = FindClaimForOperationLocked(claim, now);
            if (found.Status != SemanticGatewayAdmissionStatus.Committed)
            {
                return new(found.Status, null);
            }
            if (found.Admission!.State !=
                SemanticGatewayAdmissionState.Committed)
            {
                return new(
                    SemanticGatewayAdmissionStatus.StateConflict,
                    CreateAdmissionLease(found.Admission));
            }

            var lease = CreateAdmissionLease(found.Admission);
            RemoveAdmissionLocked(
                found.Admission,
                AdmissionRemovalReason.Released);
            return new(
                SemanticGatewayAdmissionStatus.Released,
                lease);
        }
    }

    private ClaimLookupResult FindClaimLocked(
        SemanticGatewayAdmissionClaim claim,
        DateTimeOffset now)
    {
        if (!claim.AdmissionId.IsValid ||
            !claim.GenerationId.IsValid ||
            !claim.Principal.IsValid ||
            !claim.Source.IsValid ||
            !claim.Target.IsValid ||
            !claim.NodeId.IsValid ||
            claim.WorkerRevision <= 0)
        {
            _bindingRejections++;
            return new(
                SemanticGatewayAdmissionStatus.BindingMismatch,
                null);
        }
        if (!_admissions.TryGetValue(
                claim.AdmissionId,
                out var admission))
        {
            return new(
                SemanticGatewayAdmissionStatus.AdmissionNotFound,
                null);
        }
        if (admission.ExpiresAt <= now)
        {
            RemoveAdmissionLocked(
                admission,
                AdmissionRemovalReason.Expired);
            return new(
                SemanticGatewayAdmissionStatus.AdmissionExpired,
                null);
        }
        if (admission.GenerationId != claim.GenerationId ||
            admission.Principal != claim.Principal ||
            admission.Source != claim.Source ||
            admission.Route.Target != claim.Target ||
            admission.Route.NodeId != claim.NodeId ||
            admission.Route.WorkerRevision != claim.WorkerRevision)
        {
            _bindingRejections++;
            return new(
                SemanticGatewayAdmissionStatus.BindingMismatch,
                null);
        }

        return new(
            SemanticGatewayAdmissionStatus.Committed,
            admission);
    }

    private ClaimLookupResult FindClaimForOperationLocked(
        SemanticGatewayAdmissionClaim claim,
        DateTimeOffset now)
    {
        if (claim.AdmissionId.IsValid &&
            _admissions.TryGetValue(
                claim.AdmissionId,
                out var admission))
        {
            if (_generations.TryGetValue(
                    admission.GenerationId,
                    out var generation) &&
                generation.ExpiresAt <= now)
            {
                InvalidateGenerationLocked(
                    generation,
                    GenerationRemovalReason.Expired);
                SweepExpiredLocked(
                    now,
                    _limits.MaximumExpiryWorkPerOperation);
                return new(
                    SemanticGatewayAdmissionStatus.GenerationExpired,
                    null);
            }
            if (admission.ExpiresAt <= now)
            {
                RemoveAdmissionLocked(
                    admission,
                    AdmissionRemovalReason.Expired);
                SweepExpiredLocked(
                    now,
                    _limits.MaximumExpiryWorkPerOperation);
                return new(
                    SemanticGatewayAdmissionStatus.AdmissionExpired,
                    null);
            }
        }

        SweepExpiredLocked(
            now,
            _limits.MaximumExpiryWorkPerOperation);
        return FindClaimLocked(claim, now);
    }

    private readonly record struct ClaimLookupResult(
        SemanticGatewayAdmissionStatus Status,
        AdmissionEntry? Admission);

    private bool TryFindGenerationLocked(
        SemanticGatewayLoginGenerationLease generation,
        out LoginGenerationEntry current)
    {
        if (_generations.TryGetValue(
                generation.GenerationId,
                out var found) &&
            found.Sequence == generation.Sequence &&
            found.Principal == generation.Principal &&
            found.LoginSource == generation.LoginSource &&
            found.RealmGrant == generation.RealmGrant)
        {
            current = found;
            return true;
        }

        current = null!;
        return false;
    }
}
