using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal readonly record struct PveElementalCommittedHit(
    ulong CombatEventId,
    int TargetOrder,
    MonsterDamageResult DamageResult);

internal readonly record struct PveElementalDamageCommit(
    ResonanceDamageKind Kind,
    ulong SourceEventId,
    MonsterDamageResult DamageResult);

internal readonly record struct PveElementalSourceRecoveryCommit(
    int BeforeHealth,
    int AfterHealth,
    int BeforeMana,
    int AfterMana,
    long BeforeVitalsRevision,
    long AfterVitalsRevision)
{
    public bool Applied =>
        BeforeHealth != AfterHealth || BeforeMana != AfterMana;
}

internal sealed record PveElementalCommitResult(
    IReadOnlyList<ElementalEffectApplication> Applications,
    IReadOnlyList<PveElementalDamageCommit> DamageCommits,
    IReadOnlyList<MonsterStunResult> ControlCommits,
    PveElementalSourceRecoveryCommit SourceRecovery)
{
    public static PveElementalCommitResult Empty { get; } =
        new([], [], [], default);
}
