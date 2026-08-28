using Godswar.Server.Application.WorldInstances;

namespace Godswar.Server.ProtocolChecks;

internal sealed class MedusaSagaPartyAuthority(
    PartyAdmissionLease lease,
    List<string> events) : IMedusaPartyAdmissionAuthority
{
    public int Calls { get; private set; }
    public MedusaPartyLeaseAcquisitionRequest? LastRequest { get; private set; }

    public bool CanPublishConflictingPartyMutation(DateTimeOffset atUtc) =>
        !lease.IsValidAt(atUtc);

    public Task<MedusaPartyLeaseAcquisitionResult> AcquireAsync(
        MedusaPartyLeaseAcquisitionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        LastRequest = request;
        events.Add("party");
        return Task.FromResult(new MedusaPartyLeaseAcquisitionResult(
            MedusaPartyLeaseAcquisitionStatus.Issued,
            lease));
    }
}
