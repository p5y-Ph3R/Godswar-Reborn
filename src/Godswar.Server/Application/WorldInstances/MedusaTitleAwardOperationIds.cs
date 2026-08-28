namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// One deterministic completion operation is shared with admission
/// terminalization. Atomic title settlement owns that operation; callers must
/// not terminalize the admission separately first.
/// </summary>
internal static class MedusaTitleAwardOperationIds
{
    public static Guid Completion(MedusaAdmissionId admissionId) =>
        MedusaAdmissionSagaOperationIds.Completed(admissionId);
}
