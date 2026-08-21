using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal enum TrainingDummySkillRejectionReason : byte
{
    None,
    UnsupportedSkill,
    InvalidCasterObject,
    TargetUnavailable,
    StaleWorldOwnership,
    AttackerIsTrainingDummy,
    AttackerProfessionMismatch,
    TargetIsNotExactTrainingDummy,
    AdmissionDenied,
    OutOfRange,
    ElementalControl,
    InsufficientMana,
    CooldownActive,
    PartialCommitFailure
}

internal sealed record TrainingDummyAreaSkillDecision(
    bool Handled,
    TrainingDummySkillRejectionReason RejectionReason,
    IReadOnlyList<PvpBasicAttackDecision> Combats,
    int CurrentMana,
    DateTimeOffset CooldownReadyAt)
{
    public bool Accepted =>
        Handled &&
        RejectionReason == TrainingDummySkillRejectionReason.None;

    public static TrainingDummyAreaSkillDecision NotApplicable() =>
        new(
            Handled: false,
            TrainingDummySkillRejectionReason.None,
            [],
            CurrentMana: 0,
            CooldownReadyAt: default);

    public static TrainingDummyAreaSkillDecision Reject(
        TrainingDummySkillRejectionReason reason,
        int currentMana = 0,
        DateTimeOffset cooldownReadyAt = default) =>
        new(
            Handled: true,
            reason,
            [],
            currentMana,
            cooldownReadyAt);
}

internal sealed record TrainingDummySkillDecision(
    TrainingDummySkillRejectionReason RejectionReason,
    PvpBasicAttackDecision Combat,
    int CurrentMana,
    DateTimeOffset CooldownReadyAt)
{
    public bool Accepted => Combat.Accepted;

    public static TrainingDummySkillDecision Reject(
        TrainingDummySkillRejectionReason reason,
        int currentMana = 0,
        DateTimeOffset cooldownReadyAt = default,
        PvpEligibilityResult eligibility = default) =>
        new(
            reason,
            PvpBasicAttackDecision.Reject(
                PvpBasicAttackRejectionReason.AdmissionDenied,
                eligibility),
            currentMana,
            cooldownReadyAt);
}

internal sealed record TrainingDummyHostileStatusTargetDecision(
    GameSessionContext Target,
    HostileStatusApplicationDecision Application);

internal sealed record TrainingDummyHostileStatusCastDecision(
    bool Handled,
    TrainingDummySkillRejectionReason RejectionReason,
    GameSessionContext? Attacker,
    IReadOnlyList<TrainingDummyHostileStatusTargetDecision> Targets,
    int CurrentMana,
    DateTimeOffset CooldownReadyAt)
{
    public bool Accepted =>
        Handled &&
        RejectionReason == TrainingDummySkillRejectionReason.None;

    public static TrainingDummyHostileStatusCastDecision NotApplicable() =>
        new(
            Handled: false,
            TrainingDummySkillRejectionReason.None,
            null,
            [],
            CurrentMana: 0,
            CooldownReadyAt: default);

    public static TrainingDummyHostileStatusCastDecision Reject(
        TrainingDummySkillRejectionReason reason,
        int currentMana = 0,
        DateTimeOffset cooldownReadyAt = default) =>
        new(
            Handled: true,
            reason,
            null,
            [],
            currentMana,
            cooldownReadyAt);
}
