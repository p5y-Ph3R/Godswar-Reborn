using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game.WorldInstances;

internal enum MedusaMonsterPlayerHitCaptureOutcome : byte
{
    Unbound = 1,
    Captured = 2,
    CurrentMembershipRequired = 3,
    AttachmentStateConflict = 4,
    RuntimeModeUnsupported = 5,
    UnknownMonster = 6,
    StaleMonsterGeneration = 7,
    StaleMonsterHealthRevision = 8,
    StaleMonsterRuntime = 9,
    MonsterNotAttackable = 10,
    RosterBindingMismatch = 11,
    CharacterNotAdmitted = 12,
    RunNotActive = 13,
    TimestampMovedBackward = 14,
    DeadlineBoundaryUnresolved = 15,
    TimedOut = 16,
    PeriodicDamageHandoffUnavailable = 17,
    InvalidAttackEvent = 18,
    MechanicUnavailable = 19,
    OwnerClockInvariantFault = 20
}

internal enum MedusaMonsterPlayerHitCommitOutcome : byte
{
    AppliedWithEffect = 1,
    AppliedWithoutAuthoredEffect = 2,
    // Value 3 was the bounded-checkpoint Bleed-suppression outcome. It is
    // retired now that an accepted Bleed application is committed to the
    // owner; keep the numeric gap so diagnostics retain stable values.
    AppliedWithoutEffectTargetDead = 4,
    VitalsRejected = 5,
    ReplayRejected = 6,
    ReplayIdentityConflict = 7,
    AuthorityRejected = 8,
    PeriodicDamageHandoffUnavailable = 9,
    AcceptedWithoutDamage = 10,
    AppliedWithoutEffectInvariantFault = 11
}

/// <summary>
/// Exact owner-routed monster identity captured before combat resolution.
/// The player route fence is included because the source instance is selected
/// through that current session membership, never through a shared map ID.
/// </summary>
internal readonly record struct MedusaMonsterPlayerSourceAuthority(
    PlayerMonsterCombatAuthority Route,
    long WorldDescriptorRevision,
    Guid AttachmentRuntimeInstanceId,
    string AttachmentFingerprint,
    DateTimeOffset AttachmentStartedAt,
    uint ObjectId,
    uint SpawnGeneration,
    ulong HealthRevision,
    string RosterSpawnId,
    string TemplateKey,
    MedusaEncounterEnemyRole Role,
    MedusaEncounterDifficulty Difficulty,
    bool ApplyAuthoredEffect,
    ulong AttackEventId,
    DateTimeOffset CommittedAt)
{
    public bool IsValid =>
        Route.IsValid &&
        Route.Ownership.IsValid &&
        WorldDescriptorRevision >= 0 &&
        AttachmentRuntimeInstanceId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(AttachmentFingerprint) &&
        AttachmentStartedAt.Offset == TimeSpan.Zero &&
        ObjectId != 0 &&
        SpawnGeneration != 0 &&
        !string.IsNullOrWhiteSpace(RosterSpawnId) &&
        !string.IsNullOrWhiteSpace(TemplateKey) &&
        Enum.IsDefined(Role) &&
        Enum.IsDefined(Difficulty) &&
        AttackEventId != 0 &&
        CommittedAt.Offset == TimeSpan.Zero;
}

internal readonly record struct MedusaMonsterPlayerTargetAuthority(
    WorldInstanceId WorldInstanceId,
    long WorldRevision,
    PlayerOwnershipFence Ownership,
    int CharacterId,
    uint ObjectId,
    long LifeRevision,
    long VitalsRevision,
    long WorldMembershipEpoch)
{
    public bool IsValid =>
        WorldInstanceId.IsValid &&
        WorldRevision >= 0 &&
        Ownership.IsValid &&
        CharacterId > 0 &&
        ObjectId != 0 &&
        LifeRevision >= 0 &&
        VitalsRevision >= 0 &&
        WorldMembershipEpoch > 0;
}

internal readonly record struct MedusaMonsterPlayerHitCapture(
    MedusaMonsterPlayerHitCaptureOutcome Outcome,
    MonsterCombatProfile MonsterProfile,
    MedusaMonsterPlayerSourceAuthority? SourceAuthority,
    MedusaMonsterPlayerTargetAuthority TargetAuthority,
    MedusaEncounterEffectKind? AuthoredEffectKind)
{
    public bool IsBound =>
        Outcome != MedusaMonsterPlayerHitCaptureOutcome.Unbound;

    public bool IsCaptured =>
        Outcome == MedusaMonsterPlayerHitCaptureOutcome.Captured &&
        SourceAuthority is { } source &&
        source.IsValid &&
        TargetAuthority.IsValid;
}

internal readonly record struct MedusaMonsterPlayerHitCommit(
    MedusaMonsterPlayerHitCommitOutcome Outcome,
    PlayerMonsterDamageEcsDecision VitalsDecision,
    MedusaMechanicHitResult? MechanicsResult)
{
    public bool DamageApplied => VitalsDecision.Applied;

    public bool EffectApplied => Outcome ==
        MedusaMonsterPlayerHitCommitOutcome.AppliedWithEffect;
}

/// <summary>
/// Opaque, exact-hit capability prepared by the registry before player HP is
/// touched. Its concrete implementation owns the already-selected cast sink
/// and notification barrier; the map owner may only claim it at the one
/// post-vitals effect-finalization point.
/// </summary>
internal abstract class MedusaCapturedEffectInterruption
{
    private protected MedusaCapturedEffectInterruption()
    {
    }

    internal abstract MedusaEncounterEffectKind EffectKind { get; }

    internal abstract bool Matches(
        ClientSession session,
        GameCharacter character,
        in MedusaMonsterPlayerSourceAuthority source,
        in MedusaMonsterPlayerTargetAuthority target);

    internal bool ClaimNonThrowing()
    {
        try
        {
            ClaimCore();
            return true;
        }
        catch
        {
            // HP has already committed when the owner invokes this method.
            // Notification failure must never split vitals from mechanics.
            return false;
        }
    }

    private protected abstract void ClaimCore();
}

/// <summary>
/// Non-forgeable server capability base. Its only implementation is private
/// to the registry and invokes the concrete live ECS adapter.
/// </summary>
internal abstract class MedusaCapturedPlayerVitalsCommit
{
    private protected MedusaCapturedPlayerVitalsCommit()
    {
    }

    internal abstract ClientSession Session { get; }

    internal abstract GameCharacter Character { get; }

    internal abstract uint PlayerObjectId { get; }

    internal abstract long ExpectedLifeRevision { get; }

    internal abstract PlayerMonsterDamageEcsRequest Request { get; }

    internal abstract long CurrentLifeRevision { get; }

    internal abstract PlayerMonsterDamageEcsDecision? LastDecision { get; }

    internal abstract bool LifeAdvanceAuthorityLost { get; }

    internal abstract PlayerMonsterDamageEcsDecision Invoke();

    internal bool Matches(
        ClientSession session,
        GameCharacter character,
        in MedusaMonsterPlayerSourceAuthority source,
        in MedusaMonsterPlayerTargetAuthority target) =>
        ReferenceEquals(Session, session) &&
        ReferenceEquals(Character, character) &&
        PlayerObjectId == target.ObjectId &&
        ExpectedLifeRevision == target.LifeRevision &&
        Request.AttackEventId == source.AttackEventId &&
        Request.MonsterObjectId == source.ObjectId &&
        Request.MonsterSpawnGeneration == source.SpawnGeneration &&
        Request.ExpectedCharacterId == target.CharacterId &&
        Request.ExpectedPlayerObjectId == target.ObjectId &&
        Request.ExpectedLifeRevision == target.LifeRevision &&
        Request.ExpectedVitalsRevision == target.VitalsRevision &&
        Request.ResolvedAt.ToUniversalTime() == source.CommittedAt;
}
