using System.Collections.Immutable;
using Godswar.Server.Ecs;

namespace Godswar.Server.World.Components.Combat;

internal enum PlayerCombatIntentKind : byte
{
    BasicAttack = 1,
    SingleTargetSkill = 2,
    AreaSkill = 3
}

internal enum PlayerCombatRejectionReason : byte
{
    None = 0,
    SourceDead = 1,
    ReservationPending = 2,
    UnsupportedIntent = 3,
    InvalidCoordinates = 4,
    TargetUnavailable = 5,
    TargetGenerationMismatch = 6,
    TargetRevisionMismatch = 7,
    OutOfRange = 8,
    CooldownActive = 9,
    InsufficientMana = 10,
    ZeroDamage = 11
}

internal enum PlayerCombatMutationRejectionReason : byte
{
    None = 0,
    TargetRejected = 1,
    GenerationMismatch = 2,
    RevisionMismatch = 3,
    NoHealthChange = 4,
    InvalidDeathTransition = 5,
    OutcomeOutOfOrder = 6
}

internal readonly record struct PlayerCombatIdentityComponent(
    int CharacterId,
    uint ObjectId);

internal readonly record struct PlayerCombatTransformComponent(
    byte MapId,
    float X,
    float Z);

internal readonly record struct PlayerCombatOffenseComponent(
    byte Profession,
    int PhysicalAttack,
    int MagicAttack,
    int PhysicalDamageBonus,
    int MagicDamageBonus,
    int PhysicalAppendDamage,
    int MagicAppendDamage)
{
    public int Level { get; init; } = 1;
    public int Hit { get; init; }
    public int Critical { get; init; }
    public int IgnorePhysicalDefenseBasisPoints { get; init; }
    public int IgnoreMagicDefenseBasisPoints { get; init; }
    public int CriticalDamageBasisPoints { get; init; }
    public int CriticalDamageFlat { get; init; }
    public int LifeAbsorptionBasisPoints { get; init; }
    public int LifeAbsorptionFlat { get; init; }
    public int BasicAttackIntervalMilliseconds { get; init; } = 1500;
}

internal struct PlayerCombatResourceComponent
{
    public PlayerCombatResourceComponent(
        int currentHp,
        int maximumHp,
        int currentMp,
        int maximumMp,
        long vitalsRevision,
        DateTimeOffset nextBasicAttackAt,
        ulong combatRevision,
        ulong eventSequence)
    {
        CurrentHp = currentHp;
        MaximumHp = maximumHp;
        CurrentMp = currentMp;
        MaximumMp = maximumMp;
        VitalsRevision = vitalsRevision;
        NextBasicAttackAt = nextBasicAttackAt;
        CombatRevision = combatRevision;
        EventSequence = eventSequence;
    }

    public int CurrentHp;
    public int MaximumHp;
    public int CurrentMp;
    public int MaximumMp;
    public long VitalsRevision;
    public DateTimeOffset NextBasicAttackAt;
    public ulong CombatRevision;
    public ulong EventSequence;
}

/// <summary>
/// Immutable monster state observed by the combat boundary. Damage remains
/// authoritative in the monster runtime; this component is only a guarded
/// selection snapshot.
/// </summary>
internal readonly record struct PlayerCombatTargetComponent(
    uint ObjectId,
    byte MapId,
    float X,
    float Z,
    uint CurrentHealth,
    bool IsSpawned,
    bool IsAlive,
    bool IsVisible,
    uint SpawnGeneration,
    ulong HealthRevision,
    float BasicAttackRange)
{
    public int Level { get; init; } = 1;
    public int PhysicalDefense { get; init; }
    public int MagicDefense { get; init; }
    public int Dodge { get; init; }
    public int CriticalResistance { get; init; }
    public int PhysicalDamageReductionBasisPoints { get; init; }
    public int MagicDamageReductionBasisPoints { get; init; }
    public int CriticalDamageReductionBasisPoints { get; init; }
    public int PhysicalFlatAbsorption { get; init; }
    public int MagicFlatAbsorption { get; init; }
    public int CriticalDamageFlatReduction { get; init; }
    public int DamageReboundBasisPoints { get; init; }
    public int DamageReboundFlat { get; init; }
}

/// <summary>
/// Scalar copy of the combat fields used by SkillCombatResolver.
/// </summary>
internal readonly record struct PlayerCombatSkillSnapshot(
    uint SkillId,
    int Target,
    int AffectObject,
    float Distance,
    float AreaRadius,
    int ManaCost,
    int Property,
    decimal Power1,
    decimal Power2);

internal readonly record struct PlayerCombatIntentComponent(
    ulong IntentId,
    PlayerCombatIntentKind Kind,
    DateTimeOffset RequestedAt,
    uint TargetObjectId,
    uint ExpectedTargetSpawnGeneration,
    ulong ExpectedTargetHealthRevision,
    float ReportedAttackerX,
    float ReportedAttackerZ,
    bool HasReportedTargetPosition,
    float ReportedTargetX,
    float ReportedTargetZ,
    PlayerCombatSkillSnapshot Skill);

internal readonly record struct PlayerCombatReservedTarget(
    int TargetOrder,
    uint ObjectId,
    uint BeforeHealth,
    uint ExpectedSpawnGeneration,
    ulong ExpectedHealthRevision,
    uint RequestedDamage);

internal struct PlayerCombatReservationComponent
{
    public PlayerCombatReservationComponent(
        ulong intentId,
        PlayerCombatIntentKind kind,
        uint skillId,
        int reservedMana,
        DateTimeOffset previousNextBasicAttackAt,
        bool refundOnRejectedTarget,
        ImmutableArray<PlayerCombatReservedTarget> targets,
        int acceptedTargetCount = 0)
    {
        IntentId = intentId;
        Kind = kind;
        SkillId = skillId;
        ReservedMana = reservedMana;
        PreviousNextBasicAttackAt = previousNextBasicAttackAt;
        RefundOnRejectedTarget = refundOnRejectedTarget;
        Targets = targets;
        NextOutcomeIndex = 0;
        AcceptedTargetCount = acceptedTargetCount;
        RejectedTargetCount = 0;
    }

    public ulong IntentId;
    public PlayerCombatIntentKind Kind;
    public uint SkillId;
    public int ReservedMana;
    public DateTimeOffset PreviousNextBasicAttackAt;
    public bool RefundOnRejectedTarget;
    public ImmutableArray<PlayerCombatReservedTarget> Targets;
    public int NextOutcomeIndex;
    public int AcceptedTargetCount;
    public int RejectedTargetCount;
}

/// <summary>
/// Scalar result supplied by the future monster-mutation adapter.
/// </summary>
internal readonly record struct PlayerCombatMutationOutcomeComponent(
    ulong IntentId,
    int TargetOrder,
    uint TargetObjectId,
    uint SpawnGeneration,
    ulong BeforeHealthRevision,
    bool Applied,
    uint BeforeHealth,
    uint AfterHealth,
    ulong AfterHealthRevision,
    bool Killed,
    PlayerCombatMutationRejectionReason RejectionReason);

internal readonly record struct PlayerCombatKillGuard(
    ulong CombatIntentId,
    uint MonsterObjectId,
    uint MonsterSpawnGeneration,
    ulong MonsterHealthRevision);

internal struct PlayerCombatKillLedgerComponent
{
    public PlayerCombatKillLedgerComponent(
        ImmutableArray<PlayerCombatKillGuard> pending)
    {
        Pending = pending.IsDefault
            ? ImmutableArray<PlayerCombatKillGuard>.Empty
            : pending;
    }

    public ImmutableArray<PlayerCombatKillGuard> Pending;

    public void Add(in PlayerCombatKillGuard guard)
    {
        var pending = Pending.IsDefault
            ? ImmutableArray<PlayerCombatKillGuard>.Empty
            : Pending;
        if (!pending.Contains(guard))
        {
            Pending = pending.Add(guard);
        }
    }

    public bool TryConsume(in PlayerCombatKillGuard guard)
    {
        var pending = Pending.IsDefault
            ? ImmutableArray<PlayerCombatKillGuard>.Empty
            : Pending;
        var index = pending.IndexOf(guard);
        if (index < 0)
        {
            return false;
        }

        Pending = pending.RemoveAt(index);
        return true;
    }
}

internal struct PlayerCommittedProgressionComponent
{
    public PlayerCommittedProgressionComponent(
        int level,
        long experience,
        int talentExperience,
        int talentPoints,
        long revision,
        ulong lastProjectionId)
    {
        Level = level;
        Experience = experience;
        TalentExperience = talentExperience;
        TalentPoints = talentPoints;
        Revision = revision;
        LastProjectionId = lastProjectionId;
    }

    public int Level;
    public long Experience;
    public int TalentExperience;
    public int TalentPoints;
    public long Revision;
    public ulong LastProjectionId;
}

internal readonly record struct CommittedLevelUpSnapshot(
    int Level,
    long CurrentExperience,
    int NextLevelExperience);

/// <summary>
/// Immutable copy of an already committed CharacterProgressionResult.
/// </summary>
internal readonly record struct CommittedCharacterProgressionSnapshot(
    int ExperienceGained,
    int PreviousLevel,
    int CurrentLevel,
    long CurrentExperience,
    int NextLevelExperience,
    ImmutableArray<CommittedLevelUpSnapshot> LevelUps,
    int TalentExperienceGained,
    int CurrentTalentExperience,
    int TalentPointsGained,
    int CurrentTalentPoints);

internal readonly record struct MonsterKillProgressionProjectionComponent(
    ulong ProjectionId,
    ulong CombatIntentId,
    uint MonsterObjectId,
    uint MonsterSpawnGeneration,
    ulong MonsterHealthRevision,
    long ExpectedProgressionRevision,
    CommittedCharacterProgressionSnapshot Committed);
