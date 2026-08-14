using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal enum PvpBasicAttackRejectionReason : byte
{
    None,
    TargetUnavailable,
    StaleWorldOwnership,
    InvalidPosition,
    OutOfRange,
    AdmissionDenied,
    ElementalControl
}

internal readonly record struct PvpElementalDamageCommit(
    ResonanceDamageKind Kind,
    GameSessionContext Source,
    GameSessionContext Target,
    ulong SourceEventId,
    int AppliedDamage,
    int CurrentHealth,
    bool Killed);

internal readonly record struct PvpElementalControlCommit(
    GameSessionContext Target,
    int StunMilliseconds);

internal sealed record PvpBasicAttackDecision(
    bool Accepted,
    PvpBasicAttackRejectionReason RejectionReason,
    PvpEligibilityResult Eligibility,
    CombatResolution Resolution,
    GameSessionContext? Attacker,
    GameSessionContext? Target,
    uint AppliedDamage,
    uint LifeAbsorptionHealing,
    uint ReboundDamage,
    bool TargetKilled,
    bool AttackerKilled,
    int AttackerCurrentHealth,
    int TargetCurrentHealth)
{
    public IReadOnlyList<ElementalEffectApplication>
        ElementalApplications { get; init; } = [];

    public IReadOnlyList<PvpElementalDamageCommit>
        ElementalDamageCommits { get; init; } = [];

    public IReadOnlyList<PvpElementalControlCommit>
        ElementalControlCommits { get; init; } = [];

    public IReadOnlyList<GameSessionContext> ChangedVitals { get; init; } = [];

    public IReadOnlyList<GameSessionContext> KilledPlayers { get; init; } = [];

    public long ElementalHealthRecovery { get; init; }

    public long ElementalManaRecovery { get; init; }

    public static PvpBasicAttackDecision Reject(
        PvpBasicAttackRejectionReason reason,
        PvpEligibilityResult eligibility = default) =>
        new(
            false,
            reason,
            eligibility,
            default,
            null,
            null,
            0,
            0,
            0,
            false,
            false,
            0,
            0);
}
