using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private static PvpBasicAttackDecision ResolveTrainingAreaBase(
        GameSessionContext attacker,
        in TrainingDummyAreaTargetPlan plan,
        in SkillCombatDefinition skill,
        long actionRevision,
        DateTimeOffset now)
    {
        var snapshot = TrainingDummyDamageSkillPolicy.Snapshot(skill);
        var eventId = CombatEventIdentity.ForPlayerSkill(
            attacker.CharacterId,
            plan.Target.CharacterId,
            plan.AttackerVitalsRevision,
            plan.TargetVitalsRevision,
            actionRevision,
            snapshot.SkillId);
        var resolution = ZodiacDefensiveSkillProjection.ResolvePvpSkillDamage(
            plan.Target.Character,
            plan.AttackerCombat,
            plan.TargetCombat,
            snapshot,
            eventId);
        return AcceptedPvpDecision(
            plan.Eligibility,
            resolution,
            attacker,
            plan.Target,
            appliedDamage: 0,
            lifeHealing: 0,
            reboundDamage: 0,
            changed: []);
    }

    private PvpBasicAttackDecision CommitTrainingAreaTargetLocked(
        GameSessionContext attacker,
        in TrainingDummyAreaTargetPlan plan,
        PvpBasicAttackDecision resolved,
        in SkillCombatDefinition skill,
        DateTimeOffset now)
    {
        var attempt = new DeterministicCombatEventContext(
            resolved.Resolution.EventId,
            attacker.MapId,
            attacker.CharacterId,
            plan.Target.CharacterId,
            now.ToUnixTimeMilliseconds(),
            CombatEventProvenance.DirectSkill,
            Committed: false,
            IsPvp: true,
            plan.Eligibility);
        IReadOnlyList<PvpElementalCandidate> candidates = [];
        var committed = ResolveCommittedPvpHitLocked(
            attacker,
            plan.Target,
            plan.Eligibility,
            resolved.Resolution,
            attempt,
            plan.TargetCombat,
            candidates,
            now);
        if (TrainingDummyHostileStatusSkillCatalog.TryGet(
                skill.SkillId,
                out var status) &&
            status.Trigger ==
                HostileStatusApplicationTrigger.CommittedDamagingHit)
        {
            _ = TryCommitTrainingDummyHostileStatusLocked(
                attacker,
                plan.Target,
                plan.Eligibility,
                status,
                new HostileStatusTriggerEvidence(
                    status.Trigger,
                    committed.Resolution.EventId,
                    committed.Resolution.TargetOrder,
                    committed.AppliedDamage),
                plan.AttackerCombat.Hit,
                plan.TargetCombat.Dodge,
                now,
                claimAppliedInterruption: null,
                out var application);
            committed = committed with
            {
                HostileStatusApplication = application
            };
        }

        return committed;
    }

    private async Task InterruptTrainingSkillVictimsAsync(
        IReadOnlyList<PvpBasicAttackDecision> combats,
        IReadOnlyList<ClientSession> deathInterruptionSessions)
    {
        var deaths = deathInterruptionSessions.Distinct().ToArray();
        var deathSet = deaths.ToHashSet();
        var tasks = deaths.Select(session =>
            RequestSkillCastInterruptionAsync(
                session,
                SkillCastInterruptionReason.Death,
                CancellationToken.None)).ToList();
        tasks.AddRange(combats
            .SelectMany(static combat => combat.ElementalControlCommits)
            .Select(static value => value.Target.Session)
            .Where(session => !deathSet.Contains(session))
            .Distinct()
            .Select(session => RequestSkillCastInterruptionAsync(
                session,
                SkillCastInterruptionReason.Stunned,
                CancellationToken.None)));
        await Task.WhenAll(tasks);
    }

    private readonly record struct TrainingDummyAreaTargetPlan(
        GameSessionContext Target,
        PvpEligibilityResult Eligibility,
        CombatAttackerStats AttackerCombat,
        CombatTargetStats TargetCombat,
        long AttackerVitalsRevision,
        long TargetVitalsRevision);
}
