using Godswar.Server.State;

namespace Godswar.Server.World.Systems.Combat;

internal readonly record struct ElementalDirectHitCommitResult(
    ElementalEffectApplication? ElementalApplication,
    bool ElementalApplicationAccepted,
    ResonancePostCommitResult Resonance);

/// <summary>
/// Owns the post-commit order shared by PvE and PvP. Resonance observes the
/// target's pre-hit status state so Prometheus detonates only an earlier Burn;
/// the current hit's generic elemental application is arbitrated afterward.
/// </summary>
internal static class ElementalDirectHitCommitPolicy
{
    public static ElementalDirectHitCommitResult Commit(
        DeterministicCombatEventContext combatEvent,
        ElementalEquipmentProfile sourceProfile,
        ElementalResonanceState sourceState,
        ElementalEquipmentProfile targetProfile,
        ElementalStatusState targetStatuses,
        ElementKind? authoredElement,
        ElementalEffectExecutionTuning tuning,
        long appliedDirectDamage,
        long sourceMaximumHealth,
        bool primaryTargetIsBoss,
        IEnumerable<ResonanceTargetCandidate> additionalTargets)
    {
        ArgumentNullException.ThrowIfNull(sourceProfile);
        ArgumentNullException.ThrowIfNull(sourceState);
        ArgumentNullException.ThrowIfNull(targetProfile);
        ArgumentNullException.ThrowIfNull(targetStatuses);
        ArgumentNullException.ThrowIfNull(additionalTargets);

        var resonance = ElementalResonanceExecutionPolicy
            .ProcessCommittedDirectHit(
                combatEvent,
                sourceProfile,
                sourceState,
                targetStatuses,
                appliedDirectDamage,
                sourceMaximumHealth,
                primaryTargetIsBoss,
                additionalTargets);

        ElementalEffectApplication? application = null;
        var applied = false;
        if (authoredElement.HasValue &&
            ElementalEffectExecutionPolicy.TryPlanDirectApplication(
                combatEvent,
                authoredElement.Value,
                sourceProfile,
                targetProfile,
                tuning,
                appliedDirectDamage,
                out var planned))
        {
            application = planned;
            applied = targetStatuses.TryApply(planned);
        }

        return new(application, applied, resonance);
    }
}
