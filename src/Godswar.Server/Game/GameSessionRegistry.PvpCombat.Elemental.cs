using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal readonly record struct PvpElementalResolutionInputs(
    CombatAttackerStats Attacker,
    CombatTargetStats Target,
    bool AttackerActionAllowed);

internal readonly record struct PvpElementalDamageAdjustment(
    OutgoingResonanceAdjustment Outgoing,
    IncomingResonanceAdjustment Incoming);

internal readonly record struct PvpElementalPostCommit(
    IReadOnlyList<ElementalEffectApplication> Applications,
    ResonancePostCommitResult SourceResonance,
    ResonanceDamageIntent? Reflection,
    bool ResonanceStunApplied);

internal sealed partial class GameSessionRegistry
{
    private bool TryAdjustPvpElementalResolutionInputsLocked(
        GameSessionContext attacker,
        GameSessionContext target,
        DateTimeOffset now,
        in CombatAttackerStats attackerStats,
        in CombatTargetStats targetStats,
        out PvpElementalResolutionInputs adjusted)
    {
        adjusted = new(attackerStats, targetStats, true);
        if (!TryGetPvpElementalStates(
                attacker,
                target,
                out var sourceState,
                out var targetState))
        {
            return false;
        }

        var baseAttackerStats = attackerStats;
        var baseTargetStats = targetStats;
        var resolvedInputs = adjusted;
        LockPvpElementalStates(
            attacker.CharacterId,
            sourceState,
            targetState,
            () =>
            {
                var at = now.ToUnixTimeMilliseconds();
                var sourceStatus = sourceState.Statuses.ApplyAdjustments(
                    at,
                    movementSpeed: 0,
                    physicalDefense: 0,
                    magicDefense: 0,
                    hitRating: Math.Max(0, baseAttackerStats.Hit),
                    healingReceived: 0);
                var targetStatus = targetState.Statuses.ApplyAdjustments(
                    at,
                    movementSpeed: 0,
                    physicalDefense: Math.Max(
                        0,
                        baseTargetStats.PhysicalDefense),
                    magicDefense: Math.Max(
                        0,
                        baseTargetStats.MagicDefense),
                    hitRating: 0,
                    healingReceived: 0);
                resolvedInputs = new(
                    baseAttackerStats with
                    {
                        Hit = ClampCombatInt(sourceStatus.HitRating)
                    },
                    baseTargetStats with
                    {
                        PhysicalDefense = ClampCombatInt(
                            targetStatus.PhysicalDefense),
                        MagicDefense = ClampCombatInt(
                            targetStatus.MagicDefense)
                    },
                    sourceStatus.MovementAllowed);
            });
        adjusted = resolvedInputs;
        return true;
    }

    private bool TryAdjustPvpElementalDamageLocked(
        GameSessionContext attacker,
        GameSessionContext target,
        DeterministicCombatEventContext combatEvent,
        long resolvedDamage,
        out PvpElementalDamageAdjustment adjustment)
    {
        adjustment = default;
        if (!TryGetPvpElementalStates(
                attacker,
                target,
                out var sourceState,
                out var targetState))
        {
            return false;
        }

        var resolvedAdjustment = adjustment;
        LockPvpElementalStates(
            attacker.CharacterId,
            sourceState,
            targetState,
            () =>
            {
                var outgoing = ElementalResonanceExecutionPolicy
                    .AdjustOutgoingDirectDamage(
                        combatEvent,
                        attacker.Character.ElementalEquipment,
                        sourceState.Resonance,
                        resolvedDamage,
                        target.Character.CurrentHp,
                        target.Character.MaxHp);
                var incoming = ElementalResonanceExecutionPolicy
                    .AdjustIncomingDirectDamage(
                        combatEvent,
                        target.Character.ElementalEquipment,
                        targetState.Resonance,
                        outgoing.AdjustedDamage,
                        target.Character.CurrentHp,
                        target.Character.MaxHp,
                        target.Character.MaxMp);
                resolvedAdjustment = new(outgoing, incoming);
            });
        adjustment = resolvedAdjustment;
        return true;
    }

    private bool TryCommitPvpElementalHitLocked(
        GameSessionContext attacker,
        GameSessionContext target,
        DeterministicCombatEventContext committedEvent,
        long appliedDirectDamage,
        IReadOnlyList<ResonanceTargetCandidate> additionalTargets,
        out PvpElementalPostCommit committed)
    {
        committed = default;
        if (!TryGetPvpElementalStates(
                attacker,
                target,
                out var sourceState,
                out var targetState))
        {
            return false;
        }

        var resolvedCommit = committed;
        LockPvpElementalStates(
            attacker.CharacterId,
            sourceState,
            targetState,
            () =>
            {
                var applications = new List<ElementalEffectApplication>(1);
                ElementKind? authoredElement =
                    AuthoredElementalCombatV1.TrySelectDirectHitElement(
                        attacker.Character.ElementalEquipment,
                        out var selectedElement)
                        ? selectedElement
                        : null;
                var committedHit = ElementalDirectHitCommitPolicy.Commit(
                    committedEvent,
                    attacker.Character.ElementalEquipment,
                    sourceState.Resonance,
                    target.Character.ElementalEquipment,
                    targetState.Statuses,
                    authoredElement,
                    AuthoredElementalCombatV1.EffectTuning,
                    appliedDirectDamage,
                    attacker.Character.MaxHp,
                    primaryTargetIsBoss: false,
                    additionalTargets);
                if (committedHit is
                    {
                        ElementalApplicationAccepted: true,
                        ElementalApplication: { } application
                    })
                {
                    applications.Add(application);
                }

                var resonance = committedHit.Resonance;
                var reflection = ElementalResonanceExecutionPolicy
                    .PlanCommittedReflection(
                        committedEvent,
                        target.Character.ElementalEquipment,
                        targetState.Resonance,
                        appliedDirectDamage,
                        attacker.Character.MaxHp);
                var stunApplied = false;
                foreach (var control in resonance.ControlIntents.Where(
                             value =>
                                 value.TargetId == target.CharacterId &&
                                 value.StunMilliseconds > 0))
                {
                    stunApplied |= targetState.Statuses.TryApply(
                        ResonanceStunApplication(
                            committedEvent,
                            control));
                }

                resolvedCommit = new(
                    applications.AsReadOnly(),
                    resonance,
                    reflection,
                    stunApplied);
            });
        committed = resolvedCommit;
        return true;
    }

    private bool TryGetPvpElementalStates(
        GameSessionContext attacker,
        GameSessionContext target,
        out ElementalCombatSessionState sourceState,
        out ElementalCombatSessionState targetState)
    {
        sourceState = null!;
        targetState = null!;
        var sourceFence = new ElementalCombatSessionFence(
            attacker.CharacterId,
            attacker.MapId,
            attacker.Ownership);
        var targetFence = new ElementalCombatSessionFence(
            target.CharacterId,
            target.MapId,
            target.Ownership);
        return sourceFence.IsValid &&
            targetFence.IsValid &&
            TryGetElementalCombatSession(
                attacker.Session,
                sourceFence,
                out sourceState) &&
            TryGetElementalCombatSession(
                target.Session,
                targetFence,
                out targetState);
    }

    private static void LockPvpElementalStates(
        int attackerCharacterId,
        ElementalCombatSessionState sourceState,
        ElementalCombatSessionState targetState,
        Action action)
    {
        var first = attackerCharacterId < targetState.Identity.CharacterId
            ? sourceState
            : targetState;
        var second = ReferenceEquals(first, sourceState)
            ? targetState
            : sourceState;
        lock (first.Gate)
        lock (second.Gate)
        {
            action();
        }
    }

    private static ElementalEffectApplication ResonanceStunApplication(
        DeterministicCombatEventContext combatEvent,
        ResonanceControlIntent control) =>
        new(
            ElementKind.Lightning,
            ElementalEffectKind.Shock,
            combatEvent.SourceCharacterId,
            control.TargetId,
            combatEvent.EventId,
            combatEvent.AuthoritativeTimeMilliseconds,
            checked(combatEvent.AuthoritativeTimeMilliseconds +
                control.StunMilliseconds),
            EffectivePotencyBasisPoints:
                ElementalBasisPointMath.Denominator,
            ApplicationChanceBasisPoints:
                ElementalBasisPointMath.Denominator,
            TargetResistanceBasisPoints: 0,
            PeriodicDamageTotal: 0,
            PeriodicTickCount: 0,
            CombatEventProvenance.Resonance);

    private static int ClampCombatInt(long value) =>
        checked((int)Math.Clamp(value, 0, int.MaxValue));
}
