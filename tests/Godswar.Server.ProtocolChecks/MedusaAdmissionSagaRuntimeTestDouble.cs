using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal sealed partial class MedusaSagaRuntimeGateway(List<string> events) :
    IMedusaPendingStartRuntimeGateway
{
    public DateTimeOffset InitialPreparedAtUtc { get; set; }
    public MedusaPendingRuntimeStatus EnsureFailure { get; set; }
    public MedusaPendingRuntimeSnapshot? Current { get; private set; }
    public MedusaPendingStartRuntimeRequest? LastEnsureRequest { get; private set; }
    public int StartCalls { get; private set; }
    public int ReleaseCalls { get; private set; }

    public void LoseProcess() => Current = null;

    public Task<MedusaPendingRuntimeResult> EnsurePendingStartAsync(
        MedusaPendingStartRuntimeRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastEnsureRequest = request;
        events.Add("ensure");
        if (_retired is not null || _released is not null)
        {
            return Task.FromResult(new MedusaPendingRuntimeResult(
                MedusaPendingRuntimeStatus.RejectedNoPublication,
                _retired ?? _released));
        }
        if (EnsureFailure != 0)
        {
            return Task.FromResult(new MedusaPendingRuntimeResult(
                EnsureFailure,
                null));
        }
        if (Current is not null)
        {
            return Task.FromResult(new MedusaPendingRuntimeResult(
                MedusaPendingRuntimeStatus.ExactReplay,
                Current));
        }
        var preparedAt = request.ExpectedPreparedAtUtc ?? InitialPreparedAtUtc;
        Current = new MedusaPendingRuntimeSnapshot(
            request.AdmissionId,
            request.WorldInstanceId,
            request.Difficulty,
            request.ContentMapId,
            request.RosterHash,
            request.AdmissionRequestHash,
            request.EncounterContentFingerprint,
            request.ExpectedTransferToken,
            MedusaPendingRuntimeState.PendingStart,
            request.CreatedAtUtc,
            preparedAt,
            null,
            null);
        return Task.FromResult(new MedusaPendingRuntimeResult(
            MedusaPendingRuntimeStatus.Applied,
            Current));
    }

    public Task<MedusaPendingRuntimeResult> StartAsync(
        MedusaRuntimeStartPermit permit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCalls++;
        events.Add("start");
        if (_retired is not null || _released is not null)
        {
            return Task.FromResult(new MedusaPendingRuntimeResult(
                MedusaPendingRuntimeStatus.RejectedNoChange,
                _retired ?? _released));
        }
        if (Current is null)
        {
            throw new InvalidOperationException("Ensure must precede Start.");
        }
        if (Current.State == MedusaPendingRuntimeState.Retired)
        {
            return Task.FromResult(new MedusaPendingRuntimeResult(
                MedusaPendingRuntimeStatus.RejectedNoChange,
                Current));
        }
        var replay = Current.State == MedusaPendingRuntimeState.Running;
        Current = new MedusaPendingRuntimeSnapshot(
            Current.AdmissionId,
            Current.WorldInstanceId,
            Current.Difficulty,
            Current.ContentMapId,
            Current.RosterHash,
            Current.AdmissionRequestHash,
            Current.EncounterContentFingerprint,
            Current.TransferToken,
            MedusaPendingRuntimeState.Running,
            Current.CreatedAtUtc,
            Current.PreparedAtUtc,
            permit.StartedAtUtc,
            null);
        return Task.FromResult(new MedusaPendingRuntimeResult(
            replay
                ? MedusaPendingRuntimeStatus.ExactReplay
                : MedusaPendingRuntimeStatus.Applied,
            Current));
    }

    public Task<MedusaPendingRuntimeResult> ReleaseEmptyAsync(
        MedusaRuntimeReleasePermit permit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReleaseCalls++;
        events.Add("runtime-release");
        if (ForcedReleaseResult is not null)
        {
            return Task.FromResult(ForcedReleaseResult);
        }
        if (_released is not null)
        {
            var replay = new MedusaPendingRuntimeResult(
                MedusaPendingRuntimeStatus.ExactReplay,
                _released);
            return Task.FromResult(permit.Matches(replay)
                ? replay
                : new MedusaPendingRuntimeResult(
                    MedusaPendingRuntimeStatus.IdentityConflict,
                    null));
        }
        if (Current is { } current &&
            (current.AdmissionId != permit.AdmissionId ||
             current.WorldInstanceId != permit.WorldInstanceId ||
             current.AdmissionRequestHash != permit.AdmissionRequestHash ||
             current.RosterHash != permit.RosterHash ||
             current.CreatedAtUtc != permit.CreatedAtUtc ||
             current.PreparedAtUtc != permit.PreparedAtUtc))
        {
            return Task.FromResult(new MedusaPendingRuntimeResult(
                MedusaPendingRuntimeStatus.IdentityConflict,
                null));
        }
        _released = new MedusaPendingRuntimeSnapshot(
            permit.AdmissionId,
            permit.WorldInstanceId,
            permit.Difficulty,
            permit.ContentMapId,
            permit.RosterHash,
            permit.AdmissionRequestHash,
            permit.EncounterContentFingerprint,
            permit.TransferToken,
            MedusaPendingRuntimeState.Released,
            permit.CreatedAtUtc,
            permit.PreparedAtUtc,
            null,
            permit.ReleasedAtUtc);
        Current = _released;
        return Task.FromResult(new MedusaPendingRuntimeResult(
            MedusaPendingRuntimeStatus.Applied,
            Current));
    }
}
