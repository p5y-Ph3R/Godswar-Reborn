using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Store-derived terminal egress authority. The gateway must serialize this
/// against its assignment ledger and durably tombstone entry before success.
/// </summary>
internal sealed class MedusaRosterEgressPermit
{
    private MedusaRosterEgressPermit(MedusaAdmissionSnapshot terminal)
    {
        OperationId = MedusaAdmissionSagaOperationIds.RosterEgress(
            terminal.AdmissionId);
        AdmissionId = terminal.AdmissionId;
        WorldInstanceId = terminal.WorldInstanceId;
        AdmissionRequestHash = terminal.RequestHash;
        RosterHash = terminal.RosterHash;
        Party = terminal.Party;
        Source = terminal.Source;
        TerminalState = terminal.State;
        TerminalAtUtc = terminal.TerminalAtUtc!.Value;
    }

    public Guid OperationId { get; }
    public MedusaAdmissionId AdmissionId { get; }
    public WorldInstanceId WorldInstanceId { get; }
    public string AdmissionRequestHash { get; }
    public string RosterHash { get; }
    public PartyAdmissionLease Party { get; }
    public MedusaAdmissionSource Source { get; }
    public MedusaAdmissionState TerminalState { get; }
    public DateTimeOffset TerminalAtUtc { get; }

    internal static bool TryCreate(
        MedusaAdmissionSnapshot terminal,
        out MedusaRosterEgressPermit permit)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (terminal.State is not (
                MedusaAdmissionState.Completed or
                MedusaAdmissionState.Abandoned or
                MedusaAdmissionState.TimedOut) ||
            terminal.TerminalAtUtc is null ||
            terminal.CleanupEvidence is not null)
        {
            permit = null!;
            return false;
        }
        permit = new(terminal);
        return true;
    }
}

internal enum MedusaRosterEgressStatus : byte
{
    Egressed = 1,
    ExactReplay = 2,
    IdentityConflict = 3,
    OwnershipUnavailable = 4
}

internal sealed class MedusaRosterEgressResult
{
    public MedusaRosterEgressResult(
        MedusaRosterEgressStatus status,
        MedusaAdmissionId admissionId,
        WorldInstanceId worldInstanceId,
        string admissionRequestHash,
        string rosterHash,
        DateTimeOffset? egressedAtUtc)
    {
        if (!Enum.IsDefined(status) || !admissionId.IsValid ||
            !worldInstanceId.IsValid)
        {
            throw new ArgumentException(
                "An egress result requires valid exact identity.");
        }
        MedusaDurableAdmissionPolicy.ValidateHash(
            admissionRequestHash,
            nameof(admissionRequestHash));
        MedusaDurableAdmissionPolicy.ValidateHash(rosterHash, nameof(rosterHash));
        var succeeded = status is
            MedusaRosterEgressStatus.Egressed or
            MedusaRosterEgressStatus.ExactReplay;
        if (succeeded)
        {
            egressedAtUtc = MedusaDurableAdmissionPolicy.CanonicalUtc(
                egressedAtUtc ??
                    throw new ArgumentNullException(nameof(egressedAtUtc)),
                nameof(egressedAtUtc));
        }
        else if (egressedAtUtc is not null)
        {
            throw new ArgumentException(
                "Rejected egress cannot expose completion evidence.",
                nameof(egressedAtUtc));
        }

        Status = status;
        AdmissionId = admissionId;
        WorldInstanceId = worldInstanceId;
        AdmissionRequestHash = admissionRequestHash;
        RosterHash = rosterHash;
        EgressedAtUtc = egressedAtUtc;
    }

    public MedusaRosterEgressStatus Status { get; }
    public MedusaAdmissionId AdmissionId { get; }
    public WorldInstanceId WorldInstanceId { get; }
    public string AdmissionRequestHash { get; }
    public string RosterHash { get; }
    public DateTimeOffset? EgressedAtUtc { get; }

    public bool Succeeded => Status is
        MedusaRosterEgressStatus.Egressed or
        MedusaRosterEgressStatus.ExactReplay;

    internal bool Matches(MedusaRosterEgressPermit permit)
    {
        ArgumentNullException.ThrowIfNull(permit);
        return Succeeded &&
            AdmissionId == permit.AdmissionId &&
            WorldInstanceId == permit.WorldInstanceId &&
            AdmissionRequestHash == permit.AdmissionRequestHash &&
            RosterHash == permit.RosterHash &&
            EgressedAtUtc == permit.TerminalAtUtc;
    }
}
