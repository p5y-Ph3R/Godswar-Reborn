namespace Godswar.Server.Application.WorldInstances;

internal sealed partial class MedusaAdmissionSagaCoordinator
{
    /// <summary>
    /// Resumes an exact snapshot returned by the durable recovery source.
    /// It reconstructs only the original server command identity; ExecuteAsync
    /// re-reads the row and refuses any stale or changed durable operation.
    /// No party capability is reacquired for an already-reserved admission.
    /// </summary>
    public Task<MedusaAdmissionSagaResult> ResumeAsync(
        MedusaAdmissionSnapshot recoverySnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recoverySnapshot);
        var leader = recoverySnapshot.Party.Members.Single(member =>
            member.AccountId == recoverySnapshot.Party.LeaderAccountId &&
            member.CharacterId == recoverySnapshot.Party.LeaderCharacterId);
        return ExecuteAsync(
            new MedusaAdmissionStartCommand(
                new MedusaAdmissionOperationIdentity(
                    recoverySnapshot.AdmissionId,
                    recoverySnapshot.WorldInstanceId),
                recoverySnapshot.Difficulty,
                recoverySnapshot.Source,
                recoverySnapshot.EncounterContentFingerprint,
                leader.AccountId,
                leader.CharacterId,
                leader.Ownership,
                recoverySnapshot.ReservedAtUtc),
            cancellationToken);
    }
}
