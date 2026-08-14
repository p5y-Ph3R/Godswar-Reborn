using Godswar.Server.State;

namespace Godswar.Server.World.Systems.Combat;

internal enum ResonanceDamageKind : byte
{
    PrometheusDetonation,
    ZeusBolt,
    ZeusChain,
    ZeusStormCrown,
    GaiaReflection,
    ElementalBurnTick
}

internal enum ResonanceTargetAuthority : byte
{
    None,
    AuthoritativeMonster,
    AdmittedPlayer
}

internal readonly record struct ResonanceTargetCandidate(
    long TargetId,
    int MapId,
    long DistanceMillimeters,
    bool IsAlive,
    bool IsBoss,
    ResonanceTargetAuthority Authority,
    PvpEligibilityResult PvpAdmission)
{
    public bool IsAdmitted(long sourceCharacterId) =>
        TargetId > 0 &&
        MapId >= 0 &&
        DistanceMillimeters >= 0 &&
        IsAlive &&
        (Authority == ResonanceTargetAuthority.AuthoritativeMonster ||
         Authority == ResonanceTargetAuthority.AdmittedPlayer &&
         TargetId != sourceCharacterId &&
         PvpAdmission.Admits(sourceCharacterId, TargetId, MapId));
}

internal readonly record struct ResonanceDamageIntent(
    ResonanceDamageKind Kind,
    long SourceCharacterId,
    long TargetId,
    ulong SourceEventId,
    long Damage,
    CombatEventProvenance Provenance)
{
    // Derived intents are deliberately terminal: adapters must not run hit,
    // crit, elemental application, life-steal, or reflection hooks for them.
    public bool CanTriggerSecondaryCombatEffects => false;
}

internal readonly record struct ResonanceControlIntent(
    long SourceCharacterId,
    long TargetId,
    ulong SourceEventId,
    int StunMilliseconds,
    CombatEventProvenance Provenance);

internal readonly record struct ResonancePassiveAdjustment(
    long MaximumHealth,
    long MovementSpeed);

internal readonly record struct OutgoingResonanceAdjustment(
    long OriginalDamage,
    long AdjustedDamage,
    bool HadesExecuteApplied,
    bool AeolusMomentumPendingCommit);

internal readonly record struct IncomingResonanceAdjustment(
    long OriginalDamage,
    long AdjustedDamage,
    long PreventedDamage,
    long RemainingHealth,
    bool Evaded,
    bool PoseidonGuardApplied,
    bool ApolloLethalProtectionApplied,
    long ConsumedBarrier,
    long GuardHealthRecovery,
    long GuardManaRecovery);

internal readonly record struct ResonancePostCommitResult(
    IReadOnlyList<ResonanceDamageIntent> DamageIntents,
    IReadOnlyList<ResonanceControlIntent> ControlIntents,
    long SourceHealthRecovery,
    long SourceManaRecovery,
    bool BurnApplied,
    bool BurnDetonated,
    long DetonatedBurnDamage);

internal readonly record struct ResonanceRecoveryResult(
    long RequestedHealth,
    long RequestedMana,
    long AppliedHealth,
    long AppliedMana,
    long BarrierAdded,
    long BarrierTotal);

internal readonly record struct ResonanceMovementResult(
    long AcceptedDistanceMillimeters,
    bool MomentumReady,
    long MomentumExpiresAtMilliseconds);

internal enum ResonanceEventPhase : byte
{
    OutgoingPreResolution,
    IncomingPreResolution,
    PostCommit,
    Movement,
    Recovery,
    Kill
}
