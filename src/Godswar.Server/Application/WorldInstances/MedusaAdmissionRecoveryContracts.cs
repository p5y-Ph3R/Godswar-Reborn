using System.Collections.Immutable;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

internal readonly record struct MedusaAdmissionRecoveryCursor
{
    public MedusaAdmissionRecoveryCursor(
        DateTimeOffset lastChangedAtUtc,
        MedusaAdmissionId admissionId)
    {
        if (!admissionId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(admissionId));
        }
        LastChangedAtUtc = MedusaDurableAdmissionPolicy.CanonicalUtc(
            lastChangedAtUtc,
            nameof(lastChangedAtUtc));
        AdmissionId = admissionId;
    }

    public DateTimeOffset LastChangedAtUtc { get; }

    public MedusaAdmissionId AdmissionId { get; }

    public bool IsValid =>
        LastChangedAtUtc != default && AdmissionId.IsValid;
}

internal sealed record MedusaAdmissionRecoveryPage(
    ImmutableArray<MedusaAdmissionSnapshot> Admissions,
    MedusaAdmissionRecoveryCursor? NextCursor);

/// <summary>
/// Bounded durable recovery source for an unwired reconciler. Pending,
/// barrier, running, and Released rows are discoverable without another NPC
/// click. Released cleanup is idempotent; terminal rows are not re-admitted.
/// Active-member lookup is a transactionally consistent observation, never
/// map-only routing or transfer authority; the eventual commit gateway must
/// revalidate the exact admission/member state before mutating a route.
/// Pending terminal/Released rows retain their exact member assignment until
/// deterministic egress and runtime-retire evidence advances them to a cleaned
/// state. Scans omit cleaned history, so failures retry without replaying all
/// historical cleanup. No production scheduler or scale index is claimed.
/// </summary>
internal interface IMedusaDurableAdmissionRecoverySource
{
    Task<MedusaAdmissionRecoveryPage> ScanRecoverableAsync(
        RealmId realmId,
        MedusaAdmissionRecoveryCursor? after,
        int maximumCount,
        CancellationToken cancellationToken = default);

    Task<MedusaAdmissionSnapshot?> FindActiveByMemberAsync(
        RealmId realmId,
        int characterId,
        CancellationToken cancellationToken = default);
}
