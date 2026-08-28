using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Stable server-issued identity for one admission operation. No production
/// issuer exists yet; callers must never regenerate either value on retry.
/// </summary>
internal readonly record struct MedusaAdmissionOperationIdentity
{
    public MedusaAdmissionOperationIdentity(
        MedusaAdmissionId admissionId,
        WorldInstanceId worldInstanceId)
    {
        if (!admissionId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(admissionId));
        }
        if (!worldInstanceId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(worldInstanceId));
        }
        AdmissionId = admissionId;
        WorldInstanceId = worldInstanceId;
    }

    public MedusaAdmissionId AdmissionId { get; }

    public WorldInstanceId WorldInstanceId { get; }

    public bool IsValid => AdmissionId.IsValid && WorldInstanceId.IsValid;
}

internal sealed class MedusaAdmissionStartCommand
{
    public MedusaAdmissionStartCommand(
        MedusaAdmissionOperationIdentity operation,
        MedusaEncounterDifficulty difficulty,
        MedusaAdmissionSource source,
        string encounterContentFingerprint,
        int requestingAccountId,
        int requestingCharacterId,
        PlayerOwnershipFence requestingOwnership,
        DateTimeOffset receivedAtUtc)
    {
        if (!operation.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }
        if (!source.IsValid || operation.WorldInstanceId == source.WorldInstanceId)
        {
            throw new ArgumentException(
                "A valid distinct admission source is required.",
                nameof(source));
        }
        if (!MedusaIslandEncounterPolicy.TryGetDifficulty(difficulty, out _))
        {
            throw new ArgumentOutOfRangeException(nameof(difficulty));
        }
        MedusaDurableAdmissionPolicy.ValidateHash(
            encounterContentFingerprint,
            nameof(encounterContentFingerprint));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestingAccountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestingCharacterId);
        requestingOwnership.Validate();

        Operation = operation;
        Difficulty = difficulty;
        Source = source;
        EncounterContentFingerprint = encounterContentFingerprint;
        RequestingAccountId = requestingAccountId;
        RequestingCharacterId = requestingCharacterId;
        RequestingOwnership = requestingOwnership;
        ReceivedAtUtc = MedusaDurableAdmissionPolicy.CanonicalUtc(
            receivedAtUtc,
            nameof(receivedAtUtc));
    }

    public MedusaAdmissionOperationIdentity Operation { get; }
    public MedusaEncounterDifficulty Difficulty { get; }
    public MedusaAdmissionSource Source { get; }
    public string EncounterContentFingerprint { get; }
    public int RequestingAccountId { get; }
    public int RequestingCharacterId { get; }
    public PlayerOwnershipFence RequestingOwnership { get; }
    public DateTimeOffset ReceivedAtUtc { get; }
}

internal sealed record MedusaPartyLeaseAcquisitionRequest(
    MedusaAdmissionOperationIdentity Operation,
    MedusaRealmDay RealmDay,
    MedusaEncounterDifficulty Difficulty,
    MedusaAdmissionSource Source,
    int RequestingAccountId,
    int RequestingCharacterId,
    PlayerOwnershipFence RequestingOwnership,
    DateTimeOffset ReceivedAtUtc);

internal enum MedusaPartyLeaseAcquisitionStatus : byte
{
    Issued = 1,
    ExactReplay = 2,
    Rejected = 3,
    Unavailable = 4,
    IdentityConflict = 5
}

internal sealed class MedusaPartyLeaseAcquisitionResult
{
    public MedusaPartyLeaseAcquisitionResult(
        MedusaPartyLeaseAcquisitionStatus status,
        PartyAdmissionLease? lease)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        var succeeded = status is
            MedusaPartyLeaseAcquisitionStatus.Issued or
            MedusaPartyLeaseAcquisitionStatus.ExactReplay;
        if (succeeded != (lease is not null))
        {
            throw new ArgumentException(
                "Only a successful acquisition may carry a party capability.",
                nameof(lease));
        }
        Status = status;
        Lease = lease;
    }

    public MedusaPartyLeaseAcquisitionStatus Status { get; }
    public PartyAdmissionLease? Lease { get; }

    public bool Succeeded => Status is
        MedusaPartyLeaseAcquisitionStatus.Issued or
        MedusaPartyLeaseAcquisitionStatus.ExactReplay;
}

/// <summary>
/// Future party authority. Its implementation owns party membership, leader
/// policy, revision, online ownership, realm, level, and exact source-route
/// assertions. Issued/ExactReplay is a non-revocable capability through the
/// lease expiry: conflicting leader, roster, and party-revision mutations must
/// serialize after expiry. This foundation intentionally supplies no issuer.
/// </summary>
internal interface IMedusaPartyAdmissionAuthority
{
    Task<MedusaPartyLeaseAcquisitionResult> AcquireAsync(
        MedusaPartyLeaseAcquisitionRequest request,
        CancellationToken cancellationToken = default);
}

internal enum MedusaAdmissionSagaStatus : byte
{
    Running = 1,
    AlreadyRunning = 2,
    Released = 3,
    AlreadyTerminal = 4,
    InvalidCommand = 10,
    PartyRejected = 11,
    PartyUnavailable = 12,
    ReservationConflict = 13,
    MemberAttemptConflict = 14,
    RuntimeRejectedCompensated = 15,
    TransferRejectedCompensated = 16,
    ReconcileRequired = 17,
    MemberActiveAdmissionConflict = 18
}

internal sealed record MedusaAdmissionSagaResult(
    MedusaAdmissionSagaStatus Status,
    MedusaAdmissionSnapshot? Admission,
    MedusaPendingRuntimeSnapshot? Runtime);
