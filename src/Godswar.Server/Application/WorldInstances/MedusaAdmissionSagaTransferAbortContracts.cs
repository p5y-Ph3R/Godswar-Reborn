using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Exact durable receipt for one pre-barrier preparation tombstone. A bare
/// success status is insufficient because cleanup must prove the gateway
/// retired the same admission, runtime target, roster, and Released revision.
/// </summary>
internal sealed class MedusaRosterTransferAbortResult
{
    public MedusaRosterTransferAbortResult(
        MedusaRosterTransferAbortStatus status,
        Guid operationId,
        MedusaAdmissionId admissionId,
        WorldInstanceId worldInstanceId,
        string admissionRequestHash,
        string rosterHash,
        long releasedRevision,
        DateTimeOffset releasedAtUtc)
    {
        if (!Enum.IsDefined(status) || operationId == Guid.Empty ||
            !admissionId.IsValid || !worldInstanceId.IsValid ||
            releasedRevision <= 0)
        {
            throw new ArgumentException(
                "An abort receipt requires complete exact identity.");
        }
        MedusaDurableAdmissionPolicy.ValidateHash(
            admissionRequestHash,
            nameof(admissionRequestHash));
        MedusaDurableAdmissionPolicy.ValidateHash(rosterHash, nameof(rosterHash));
        Status = status;
        OperationId = operationId;
        AdmissionId = admissionId;
        WorldInstanceId = worldInstanceId;
        AdmissionRequestHash = admissionRequestHash;
        RosterHash = rosterHash;
        ReleasedRevision = releasedRevision;
        ReleasedAtUtc = MedusaDurableAdmissionPolicy.CanonicalUtc(
            releasedAtUtc,
            nameof(releasedAtUtc));
    }

    public MedusaRosterTransferAbortStatus Status { get; }
    public Guid OperationId { get; }
    public MedusaAdmissionId AdmissionId { get; }
    public WorldInstanceId WorldInstanceId { get; }
    public string AdmissionRequestHash { get; }
    public string RosterHash { get; }
    public long ReleasedRevision { get; }
    public DateTimeOffset ReleasedAtUtc { get; }

    public bool Succeeded => Status is
        MedusaRosterTransferAbortStatus.Aborted or
        MedusaRosterTransferAbortStatus.ExactReplay;

    internal bool Matches(MedusaRosterTransferAbortPermit permit)
    {
        ArgumentNullException.ThrowIfNull(permit);
        return Succeeded &&
            OperationId == permit.OperationId &&
            AdmissionId == permit.AdmissionId &&
            WorldInstanceId == permit.WorldInstanceId &&
            AdmissionRequestHash == permit.AdmissionRequestHash &&
            RosterHash == permit.RosterHash &&
            ReleasedRevision == permit.ReleasedRevision &&
            ReleasedAtUtc == permit.ReleasedAtUtc;
    }
}
