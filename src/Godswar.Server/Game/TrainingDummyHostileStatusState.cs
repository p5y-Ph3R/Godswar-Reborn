using Godswar.Server.Application.Characters;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal enum HostileStatusApplicationDisposition : byte
{
    None = 0,
    Applied,
    ProcMiss,
    ReplaySuppressed,
    HigherPriorityActive,
    InvalidTrigger,
    InvalidEvent,
    StaleWorldOwnership,
    InvalidAttacker,
    TargetIsNotExactTrainingDummy,
    AdmissionDenied,
    TargetDead
}

internal readonly record struct HostileStatusTriggerEvidence(
    HostileStatusApplicationTrigger Trigger,
    ulong EventId,
    int TargetOrder,
    uint AppliedDamage = 0);

internal sealed record ActiveTrainingDummyHostileStatus(
    HostileStatusEffectDefinition Definition,
    DateTimeOffset AppliedAt,
    DateTimeOffset ExpiresAt,
    ulong SourceEventId,
    int SourceTargetOrder,
    int SourceCharacterId,
    long Revision)
{
    public uint RemainingSeconds(DateTimeOffset now) =>
        (uint)Math.Clamp(
            (long)Math.Ceiling((ExpiresAt - now).TotalSeconds),
            0L,
            uint.MaxValue);
}

internal readonly record struct HostileStatusApplicationDecision(
    HostileStatusApplicationDisposition Disposition,
    HostileStatusProcDecision Proc,
    ActiveTrainingDummyHostileStatus? ActiveStatus)
{
    public bool Attempted =>
        Disposition is HostileStatusApplicationDisposition.Applied or
            HostileStatusApplicationDisposition.ProcMiss or
            HostileStatusApplicationDisposition.HigherPriorityActive or
            HostileStatusApplicationDisposition.ReplaySuppressed;

    public bool Applied =>
        Disposition == HostileStatusApplicationDisposition.Applied;
}

internal sealed record TrainingDummyHostileStatusSnapshot(
    int CharacterId,
    long Revision,
    IReadOnlyList<ActiveTrainingDummyHostileStatus> ActiveStatuses)
{
    public static TrainingDummyHostileStatusSnapshot Empty { get; } =
        new(0, 0, []);
}

internal readonly record struct TrainingDummyHostileIncomingModifiers(
    int PhysicalDefense,
    int MagicDefense,
    int PhysicalDamageTakenIncreaseBasisPoints,
    int MagicDamageTakenIncreaseBasisPoints,
    int PhysicalDamageReductionBasisPoints,
    int MagicDamageReductionBasisPoints);

internal sealed class TrainingDummyHostileStatusState(
    GameSessionContext target)
{
    internal const int MaximumRecentEvents = 128;

    public int CharacterId { get; } = target.CharacterId;

    public GameCharacter Character { get; } = target.Character;

    public PlayerOwnershipFence Ownership { get; } = target.Ownership;

    public Dictionary<int, ActiveTrainingDummyHostileStatus>
        ActiveStatuses { get; } = [];

    public Dictionary<HostileStatusEventKey, DateTimeOffset>
        RecentEvents { get; } = [];

    public long Revision { get; set; }

    public bool Matches(GameSessionContext current) =>
        current.CharacterId == CharacterId &&
        ReferenceEquals(current.Character, Character) &&
        current.Ownership == Ownership;
}

internal readonly record struct HostileStatusEventKey(
    ulong EventId,
    int TargetOrder,
    int SkillId,
    int Kind);
