using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal sealed partial class MedusaSagaRuntimeGateway
{
    private MedusaPendingRuntimeSnapshot? _released;
    private MedusaPendingRuntimeSnapshot? _retired;

    public int RetireCalls { get; private set; }

    public MedusaPendingRuntimeSnapshot? DurableRetired => _retired;

    public MedusaPendingRuntimeSnapshot? DurableReleased => _released;

    public MedusaPendingRuntimeResult? LastRetireResult { get; private set; }

    public MedusaPendingRuntimeResult? ForcedRetireResult { get; set; }

    public MedusaPendingRuntimeResult? ForcedReleaseResult { get; set; }

    public Task<MedusaPendingRuntimeResult> RetireTerminalAsync(
        MedusaRuntimeRetirePermit permit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RetireCalls++;
        events.Add("runtime-retire");
        if (ForcedRetireResult is not null)
        {
            return Task.FromResult(ForcedRetireResult);
        }
        if (_retired is not null)
        {
            var replay = new MedusaPendingRuntimeResult(
                MedusaPendingRuntimeStatus.ExactReplay,
                _retired);
            if (!permit.Matches(replay))
            {
                return Task.FromResult(new MedusaPendingRuntimeResult(
                    MedusaPendingRuntimeStatus.IdentityConflict,
                    null));
            }
            LastRetireResult = replay;
            return Task.FromResult(LastRetireResult);
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
        _retired = new MedusaPendingRuntimeSnapshot(
            permit.AdmissionId,
            permit.WorldInstanceId,
            permit.Difficulty,
            permit.ContentMapId,
            permit.RosterHash,
            permit.AdmissionRequestHash,
            permit.EncounterContentFingerprint,
            permit.TransferToken,
            MedusaPendingRuntimeState.Retired,
            permit.CreatedAtUtc,
            permit.PreparedAtUtc,
            permit.StartedAtUtc,
            permit.TerminalAtUtc);
        Current = _retired;
        LastRetireResult = new MedusaPendingRuntimeResult(
            MedusaPendingRuntimeStatus.Applied,
            Current);
        return Task.FromResult(LastRetireResult);
    }
}

internal sealed partial class MedusaSagaTransferGateway
{
    private readonly HashSet<MedusaAdmissionId> _aborted = [];
    private readonly HashSet<MedusaAdmissionId> _egressed = [];

    public int EgressCalls { get; private set; }

    public MedusaRosterEgressResult? LastEgressResult { get; private set; }

    public MedusaRosterEgressResult? ForcedEgressResult { get; set; }

    public MedusaRosterTransferAbortResult? ForcedAbortResult { get; set; }

    public MedusaRosterTransferAbortResult? LastAbortResult { get; private set; }

    public Task<MedusaRosterEgressResult> EgressAsync(
        MedusaRosterEgressPermit permit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EgressCalls++;
        events.Add("egress");
        if (ForcedEgressResult is not null)
        {
            return Task.FromResult(ForcedEgressResult);
        }
        var replay = !_egressed.Add(permit.AdmissionId);
        _hidden.Remove(permit.AdmissionId);
        LastEgressResult = new MedusaRosterEgressResult(
            replay
                ? MedusaRosterEgressStatus.ExactReplay
                : MedusaRosterEgressStatus.Egressed,
            permit.AdmissionId,
            permit.WorldInstanceId,
            permit.AdmissionRequestHash,
            permit.RosterHash,
            permit.TerminalAtUtc);
        return Task.FromResult(LastEgressResult);
    }
}
