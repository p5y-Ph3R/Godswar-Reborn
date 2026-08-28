using System.Collections.Immutable;
using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game.WorldInstances;

internal enum MedusaEncounterEffectKind : byte
{
    Stun = 1,
    Freeze = 2,
    Bleed = 3,
    Shackle = 4,
    OutgoingPhysicalAmplifier = 5,
    OutgoingMagicalAmplifier = 6
}

[Flags]
internal enum MedusaEncounterControlRestriction : byte
{
    None = 0,
    Movement = 1,
    BasicAttack = 2,
    SkillCast = 4,
    ItemUse = 8,
    AllActions = Movement | BasicAttack | SkillCast | ItemUse
}

internal enum MedusaEncounterClientProjectionMode : byte
{
    NativeProjectionSupported = 1,
    CompatibilityUnresolved = 2,
    CustomProjectionRequired = 3
}

internal enum MedusaMechanicHitOutcome : byte
{
    Applied = 1,
    Refreshed = 2,
    CharacterNotAdmitted = 3,
    UnknownMonster = 4,
    StaleMonsterGeneration = 5,
    MonsterHasNoAuthoredMechanic = 6,
    MonsterRetired = 7,
    TimestampMovedBackward = 8,
    EffectWindowUnrepresentable = 9,
    ApplicationSequenceExhausted = 10,
    PeriodicDamageRequired = 11
}

internal enum MedusaMechanicsClockOutcome : byte
{
    Advanced = 1,
    TimestampMovedBackward = 2,
    PeriodicDamageRequired = 3,
    DeadlineBoundaryUnresolved = 4
}

internal enum MedusaMechanicSourceRetireOutcome : byte
{
    Retired = 1,
    AlreadyRetired = 2,
    UnknownMonster = 3,
    StaleMonsterGeneration = 4,
    TimestampMovedBackward = 5,
    PeriodicDamageRequired = 6,
    DeadlineBoundaryUnresolved = 7
}

internal enum MedusaOutgoingDamageOutcome : byte
{
    Resolved = 1,
    CharacterNotAdmitted = 2,
    UnknownDamageChannel = 3,
    UnknownHitOutcome = 4,
    TimestampMovedBackward = 5
}

internal enum MedusaPeriodicDamageKind : byte
{
    /// <summary>
    /// Stock status 18 Effect=27 (DecHP). This is deliberately not inferred
    /// to be physical or magical damage.
    /// </summary>
    DirectHealthLoss = 1
}

/// <summary>
/// Describes what may be sent to the stock client. NativeReferenceStatusId is
/// evidence, not necessarily the content-map ID. The stock lookup uses the
/// secondary scene IDs: map 200 resolves AffectMap 209 and map 204 resolves
/// AffectMap 223, so statuses 235/236 are natively projectable in both scenes.
/// </summary>
internal readonly record struct MedusaEncounterClientProjection(
    MedusaEncounterClientProjectionMode Mode,
    uint NativeReferenceStatusId,
    uint? EmittableStatusId,
    short? MatchedNativeClientSceneId,
    ImmutableArray<short> NativeAffectedClientSceneIds)
{
    public bool RequiresCustomProjection =>
        Mode == MedusaEncounterClientProjectionMode.CustomProjectionRequired;

    public bool RequiresCompatibilityDecision =>
        Mode == MedusaEncounterClientProjectionMode.CompatibilityUnresolved;

    public bool MayEmitNativeReferenceStatus =>
        Mode == MedusaEncounterClientProjectionMode
            .NativeProjectionSupported &&
        EmittableStatusId == NativeReferenceStatusId;
}

/// <summary>
/// Status 18 supplies Values=200, Interval=2, and Time=15. The authoritative
/// reconstruction therefore emits no immediate tick, then 200 damage at
/// +2,+4,...,+14 seconds. A tick is never emitted at expiration.
/// </summary>
internal readonly record struct MedusaBleedProfile(
    MedusaPeriodicDamageKind DamageKind,
    uint DamagePerTick,
    TimeSpan TickInterval,
    int MaximumTicks,
    bool TicksImmediately,
    bool TicksAtExpiration);

internal readonly record struct MedusaEncounterEffectDefinition(
    MedusaEncounterEffectKind Kind,
    MedusaIslandRosterMechanic Mechanic,
    TimeSpan Duration,
    MedusaEncounterControlRestriction ControlRestriction,
    MedusaDamageChannel? OutgoingDamageChannel,
    int OutgoingDamageMultiplier,
    MedusaBleedProfile? Bleed,
    MedusaEncounterClientProjection ClientProjection)
{
    public bool IsServerAuthoritative => true;

    public bool UsesNativeStatusOddsAsProbability => false;
}

internal readonly record struct MedusaActiveEncounterEffectSnapshot(
    MedusaEncounterEffectDefinition Definition,
    PlayerOwnershipFence TargetOwnership,
    long TargetLifeRevision,
    string SourceRosterSpawnId,
    uint SourceObjectId,
    uint SourceSpawnGeneration,
    ulong ApplicationSequence,
    DateTimeOffset AppliedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? NextPeriodicTickAt,
    int EmittedPeriodicTicks,
    long TargetWorldMembershipEpoch);

internal readonly record struct MedusaEncounterEffectTarget(
    PlayerOwnershipFence Ownership,
    long LifeRevision,
    long WorldMembershipEpoch);

internal sealed record MedusaEncounterCharacterMechanicsSnapshot(
    int CharacterId,
    MedusaEncounterEffectTarget? EffectTarget,
    MedusaEncounterControlRestriction ControlRestriction,
    int PhysicalOutgoingDamageMultiplier,
    int MagicalOutgoingDamageMultiplier,
    ImmutableArray<MedusaActiveEncounterEffectSnapshot> ActiveEffects);

/// <summary>
/// Pure, exact-life view used by command authority. Unlike the diagnostic
/// snapshot, this view excludes effects that have expired at EvaluatedAt
/// without advancing either encounter clock or consuming periodic damage.
/// </summary>
internal sealed record MedusaActiveCharacterEffectView(
    int CharacterId,
    MedusaEncounterEffectTarget EffectTarget,
    DateTimeOffset EvaluatedAt,
    DateTimeOffset RunDeadline,
    MedusaEncounterControlRestriction ControlRestriction,
    int PhysicalOutgoingDamageMultiplier,
    int MagicalOutgoingDamageMultiplier,
    ImmutableArray<MedusaActiveEncounterEffectSnapshot> ActiveEffects);

internal sealed record MedusaEncounterMechanicsSnapshot(
    WorldInstanceId WorldInstanceId,
    MedusaEncounterDifficulty Difficulty,
    MapId ContentMapId,
    DateTimeOffset StartedAt,
    DateTimeOffset LastObservedAt,
    ImmutableArray<MedusaEncounterCharacterMechanicsSnapshot> Characters)
{
    public MedusaPeriodicDamageIdentity? OutstandingPeriodicDamage {
        get;
        init;
    }
}

internal readonly record struct MedusaMechanicHitResult(
    MedusaMechanicHitOutcome Outcome,
    MedusaActiveEncounterEffectSnapshot? Effect,
    MedusaEncounterMechanicsRuntime.PeriodicDamageReservation?
        PeriodicDamage);

internal readonly record struct MedusaMechanicsClockResult(
    MedusaMechanicsClockOutcome Outcome,
    MedusaEncounterMechanicsRuntime.PeriodicDamageReservation?
        PeriodicDamage);

internal readonly record struct MedusaMechanicSourceRetireResult(
    MedusaMechanicSourceRetireOutcome Outcome,
    MedusaEncounterMechanicsRuntime.PeriodicDamageReservation?
        PeriodicDamage);

internal readonly record struct MedusaOutgoingDamageResult(
    MedusaOutgoingDamageOutcome Outcome,
    int AppliedMultiplier,
    CombatResolution Resolution);
