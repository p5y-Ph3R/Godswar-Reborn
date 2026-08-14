using Godswar.Server.Application.Characters;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal readonly record struct ElementalCombatSessionFence(
    int CharacterId,
    byte MapId,
    PlayerOwnershipFence Ownership)
{
    public bool IsValid => CharacterId > 0 && Ownership.IsValid;
}

internal readonly record struct ElementalCommittedHitResult(
    ElementalEffectApplication? ElementalApplication,
    bool ElementalApplicationAccepted,
    ResonancePostCommitResult Resonance);

internal readonly record struct ElementalMovementHookResult(
    bool Accepted,
    bool ShockBlocked,
    bool GaleApplied,
    ElementalStatusAdjustment StatusAdjustment,
    ResonanceMovementResult Resonance);

internal readonly record struct ElementalMovementAuthority(
    bool MovementAllowed,
    float MovementMultiplier,
    long EncodedMovementMultiplier);

internal readonly record struct ElementalRecoveryCommit(
    bool PulseAccepted,
    bool VitalsChanged,
    long RecoveryRevision,
    ulong EventId,
    ResonanceRecoveryResult Recovery);
