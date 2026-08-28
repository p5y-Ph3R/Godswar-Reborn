using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private MedusaMonsterPlayerHitCapture
        CaptureBoundMonsterPlayerHitLocked(
            ClientSession session,
            GameCharacter expectedCharacter,
            MonsterRuntimeSnapshot eventSource,
            ulong attackEventId,
            in PlayerMonsterCombatAuthority route,
            in MedusaMonsterPlayerTargetAuthority target,
            DateTimeOffset committedAt,
            in MonsterCombatProfile baseProfile)
    {
        var owner = _medusaInstanceOwner;
        var attachment = _medusaMonsterAttachment;
        var descriptor = _descriptor;
        if (attackEventId == 0)
        {
            return RejectedCapture(
                MedusaMonsterPlayerHitCaptureOutcome.InvalidAttackEvent,
                baseProfile,
                target);
        }
        if (owner is null ||
            attachment is null ||
            !HasCompleteMedusaDamageState(owner))
        {
            return RejectedCapture(
                MedusaMonsterPlayerHitCaptureOutcome
                    .AttachmentStateConflict,
                baseProfile,
                target);
        }
        if (_monsterRuntimeMode != MonsterRuntimeMode.Ecs ||
            _playerRuntimeMode != PlayerRuntimeMode.Ecs)
        {
            return RejectedCapture(
                MedusaMonsterPlayerHitCaptureOutcome
                    .RuntimeModeUnsupported,
                baseProfile,
                target);
        }
        if (!IsCurrentBoundTarget(
                session,
                expectedCharacter,
                route,
                target) ||
            descriptor.LifecycleState !=
                WorldInstanceLifecycleState.Active)
        {
            return RejectedCapture(
                MedusaMonsterPlayerHitCaptureOutcome
                    .CurrentMembershipRequired,
                baseProfile,
                target);
        }
        if (!TryValidateBoundSource(
                owner,
                attachment,
                eventSource,
                out var binding,
                out var sourceOutcome))
        {
            return RejectedCapture(sourceOutcome, baseProfile, target);
        }

        var preview = owner.PreviewMonsterPlayerHit(
            target.CharacterId,
            target.Ownership,
            target.LifeRevision,
            target.WorldMembershipEpoch,
            eventSource.ObjectId,
            eventSource.SpawnGeneration,
            committedAt,
            out var effectKind);
        if (preview != MedusaMonsterPlayerHitCaptureOutcome.Captured)
        {
            if (preview is
                MedusaMonsterPlayerHitCaptureOutcome
                    .DeadlineBoundaryUnresolved or
                MedusaMonsterPlayerHitCaptureOutcome.TimedOut)
            {
                var observation = owner.ObserveTime(committedAt);
                var expectedGate = preview ==
                    MedusaMonsterPlayerHitCaptureOutcome.TimedOut
                        ? MedusaOwnedOperationGateOutcome.TimedOut
                        : MedusaOwnedOperationGateOutcome
                            .DeadlineBoundaryUnresolved;
                if (observation.GateOutcome != expectedGate)
                {
                    return RejectedCapture(
                        MedusaMonsterPlayerHitCaptureOutcome
                            .OwnerClockInvariantFault,
                        baseProfile,
                        target);
                }
            }
            return RejectedCapture(preview, baseProfile, target);
        }

        MonsterCombatProfile profile;
        try
        {
            profile = MedusaIslandCombatOverride.ApplyMonsterAttackProfile(
                binding.Difficulty,
                binding.Role,
                baseProfile);
        }
        catch (Exception error) when (
            error is ArgumentOutOfRangeException or
                InvalidOperationException)
        {
            return RejectedCapture(
                MedusaMonsterPlayerHitCaptureOutcome
                    .RosterBindingMismatch,
                baseProfile,
                target);
        }

        var applyAuthoredEffect = ShouldApplyAuthoredEffect(
            binding,
            effectKind,
            profile,
            expectedCharacter,
            attackEventId);

        var source = new MedusaMonsterPlayerSourceAuthority(
            route,
            descriptor.Revision,
            attachment.RuntimeInstanceId,
            attachment.Fingerprint,
            attachment.StartedAt.ToUniversalTime(),
            eventSource.ObjectId,
            eventSource.SpawnGeneration,
            eventSource.HealthRevision,
            binding.RosterSpawnId,
            binding.TemplateKey,
            binding.Role,
            binding.Difficulty,
            applyAuthoredEffect,
            attackEventId,
            committedAt.ToUniversalTime());
        return new(
            MedusaMonsterPlayerHitCaptureOutcome.Captured,
            profile,
            source,
            target,
            applyAuthoredEffect ? effectKind : null);
    }

    private MedusaMonsterPlayerHitCommit
        CommitBoundMonsterPlayerHitLocked(
            ClientSession session,
            GameCharacter expectedCharacter,
            in MedusaMonsterPlayerSourceAuthority source,
            in MedusaMonsterPlayerTargetAuthority target,
            MedusaCapturedPlayerVitalsCommit commitVitals,
            MedusaCapturedEffectInterruption? effectInterruption)
    {
        var owner = _medusaInstanceOwner;
        var attachment = _medusaMonsterAttachment;
        if (!source.IsValid ||
            !target.IsValid ||
            !commitVitals.Matches(
                session,
                expectedCharacter,
                source,
                target) ||
            effectInterruption is not null &&
            !effectInterruption.Matches(
                session,
                expectedCharacter,
                source,
                target) ||
            commitVitals.CurrentLifeRevision != target.LifeRevision ||
            owner is null ||
            attachment is null ||
            !HasCompleteMedusaDamageState(owner) ||
            !IsCurrentBoundTarget(
                session,
                expectedCharacter,
                source.Route,
                target) ||
            expectedCharacter.CurrentHp <= 0 ||
            expectedCharacter.VitalsRevision != target.VitalsRevision ||
            !MatchesCurrentSourceAuthority(owner, attachment, source))
        {
            return RejectedCommit(
                MedusaMonsterPlayerHitCommitOutcome.AuthorityRejected);
        }

        if (!owner.TryBeginMonsterPlayerReplay(
                source,
                target,
                out var replayConflict))
        {
            return RejectedCommit(
                replayConflict
                    ? MedusaMonsterPlayerHitCommitOutcome
                        .ReplayIdentityConflict
                    : MedusaMonsterPlayerHitCommitOutcome.ReplayRejected);
        }

        MedusaPreparedMonsterPlayerEffect? prepared = null;
        var beforeHealth = expectedCharacter.CurrentHp;
        var beforeVitalsRevision = expectedCharacter.VitalsRevision;
        var replayCompleted = false;
        var decision = default(PlayerMonsterDamageEcsDecision);
        try
        {
            prepared = owner.ReserveMonsterPlayerEffect(
                target.CharacterId,
                target.Ownership,
                target.LifeRevision,
                target.WorldMembershipEpoch,
                source.ObjectId,
                source.SpawnGeneration,
                source.CommittedAt,
                source.ApplyAuthoredEffect);
            if (prepared.Outcome ==
                MedusaMonsterPlayerHitCaptureOutcome
                    .PeriodicDamageHandoffUnavailable)
            {
                owner.RollBackMonsterPlayerReplay(source, target);
                return RejectedCommit(
                    MedusaMonsterPlayerHitCommitOutcome
                        .PeriodicDamageHandoffUnavailable);
            }
            if (prepared.Outcome !=
                MedusaMonsterPlayerHitCaptureOutcome.Captured)
            {
                owner.RollBackMonsterPlayerReplay(source, target);
                return RejectedCommit(
                    MedusaMonsterPlayerHitCommitOutcome.AuthorityRejected);
            }
            if (effectInterruption is not null &&
                prepared.EffectKind != effectInterruption.EffectKind)
            {
                owner.RollBackMonsterPlayerEffect(prepared);
                owner.RollBackMonsterPlayerReplay(source, target);
                return RejectedCommit(
                    MedusaMonsterPlayerHitCommitOutcome.AuthorityRejected);
            }

            decision = commitVitals.Invoke();
            if (commitVitals.LifeAdvanceAuthorityLost)
            {
                owner.CommitMonsterPlayerEffectWithoutPublication(prepared);
                owner.CompleteMonsterPlayerReplay(source, target);
                replayCompleted = true;
                return new(
                    MedusaMonsterPlayerHitCommitOutcome
                        .AppliedWithoutEffectInvariantFault,
                    decision,
                    MechanicsResult: null);
            }
            if (!IsExactCapturedVitalsDecision(
                    expectedCharacter,
                    target,
                    commitVitals,
                    beforeHealth,
                    beforeVitalsRevision,
                    decision))
            {
                var stateChanged =
                    expectedCharacter.CurrentHp != beforeHealth ||
                    expectedCharacter.VitalsRevision !=
                        beforeVitalsRevision ||
                    commitVitals.CurrentLifeRevision !=
                        target.LifeRevision;
                if (!stateChanged)
                {
                    owner.RollBackMonsterPlayerEffect(prepared);
                    owner.RollBackMonsterPlayerReplay(source, target);
                    return RejectedCommit(
                        MedusaMonsterPlayerHitCommitOutcome
                            .AuthorityRejected);
                }

                owner.CommitMonsterPlayerEffectWithoutPublication(prepared);
                owner.CompleteMonsterPlayerReplay(source, target);
                replayCompleted = true;
                if (expectedCharacter.CurrentHp <= 0)
                {
                    FinalizeCommittedMonsterPlayerDeath(
                        owner,
                        source,
                        target);
                }
                decision = NormalizeIrreversibleVitalsDecision(
                    expectedCharacter,
                    commitVitals,
                    source,
                    target,
                    beforeHealth,
                    beforeVitalsRevision);
                return new(
                    MedusaMonsterPlayerHitCommitOutcome
                        .AppliedWithoutEffectInvariantFault,
                    decision,
                    MechanicsResult: null);
            }
            var acceptedZeroDamage =
                !decision.Applied &&
                decision.RejectionReason ==
                    MonsterPlayerDamageRejectionReason.ZeroDamage &&
                commitVitals.Request.ResolvedDamage == 0 &&
                decision.RequestedDamage == 0;
            if (acceptedZeroDamage)
            {
                // Zero damage is still a final authored attack event in the
                // ECS ledger.  Retain the coupled owner-clock observation and
                // replay identity, but never publish its reserved effect.
                owner.CommitMonsterPlayerEffectWithoutPublication(prepared);
                owner.CompleteMonsterPlayerReplay(source, target);
                replayCompleted = true;
                return new(
                    MedusaMonsterPlayerHitCommitOutcome
                        .AcceptedWithoutDamage,
                    decision,
                    MechanicsResult: null);
            }
            if (!decision.Applied || decision.AppliedDamage == 0)
            {
                owner.RollBackMonsterPlayerEffect(prepared);
                owner.RollBackMonsterPlayerReplay(source, target);
                return new(
                    MedusaMonsterPlayerHitCommitOutcome.VitalsRejected,
                    decision,
                    MechanicsResult: null);
            }
            owner.CompleteMonsterPlayerReplay(source, target);
            replayCompleted = true;
            if (decision.Killed)
            {
                owner.CommitMonsterPlayerEffectWithoutPublication(prepared);
                var deathFinalized = FinalizeCommittedMonsterPlayerDeath(
                    owner,
                    source,
                    target);
                return new(
                    deathFinalized
                        ? MedusaMonsterPlayerHitCommitOutcome
                            .AppliedWithoutEffectTargetDead
                        : MedusaMonsterPlayerHitCommitOutcome
                            .AppliedWithoutEffectInvariantFault,
                    decision,
                    MechanicsResult: null);
            }
            if (prepared.EffectKind is null)
            {
                owner.CommitMonsterPlayerEffectWithoutPublication(prepared);
                return new(
                    MedusaMonsterPlayerHitCommitOutcome
                        .AppliedWithoutAuthoredEffect,
                    decision,
                    MechanicsResult: null);
            }
            _ = effectInterruption?.ClaimNonThrowing();
            InvokeProtocolCheckMedusaFinalizeEffectFault();
            var mechanics = owner.FinalizeMonsterPlayerEffect(prepared);
            return new(
                MedusaMonsterPlayerHitCommitOutcome.AppliedWithEffect,
                decision,
                mechanics);
        }
        catch
        {
            var playerStateUnchanged =
                expectedCharacter.CurrentHp == beforeHealth &&
                expectedCharacter.VitalsRevision ==
                    beforeVitalsRevision &&
                commitVitals.CurrentLifeRevision ==
                    target.LifeRevision;
            if (playerStateUnchanged)
            {
                owner.RollBackMonsterPlayerEffect(prepared);
                owner.RollBackMonsterPlayerReplay(source, target);
                throw;
            }
            else
            {
                if (prepared is not null)
                {
                    owner.CommitMonsterPlayerEffectWithoutPublication(
                        prepared);
                }
                if (!replayCompleted)
                {
                    owner.CompleteMonsterPlayerReplay(source, target);
                }
                if (expectedCharacter.CurrentHp <= 0)
                {
                    FinalizeCommittedMonsterPlayerDeath(
                        owner,
                        source,
                        target);
                }

                decision = NormalizeIrreversibleVitalsDecision(
                    expectedCharacter,
                    commitVitals,
                    source,
                    target,
                    beforeHealth,
                    beforeVitalsRevision);
                return new(
                    MedusaMonsterPlayerHitCommitOutcome
                        .AppliedWithoutEffectInvariantFault,
                    decision,
                    MechanicsResult: null);
            }
        }
    }

    private static bool ShouldApplyAuthoredEffect(
        in MedusaOwnedMonsterBinding binding,
        MedusaEncounterEffectKind? effectKind,
        in MonsterCombatProfile profile,
        GameCharacter target,
        ulong attackEventId)
    {
        if (effectKind is not (
                MedusaEncounterEffectKind.Stun or
                MedusaEncounterEffectKind.Freeze or
                MedusaEncounterEffectKind.Shackle))
        {
            return true;
        }
        if (!MedusaIslandRosterPolicy.TryGetSpawn(
                binding.RosterSpawnId,
                out var spawn) ||
            spawn.Skill is not { } skill ||
            !skill.RequiresDeterministicRatingProc)
        {
            return false;
        }

        var targetStats = CharacterStats.FromCharacter(target);
        return HostileStatusProcPolicy.Evaluate(
                new(
                    profile.Level,
                    target.Level,
                    profile.Hit,
                    targetStats.Dodge,
                    skill.NativeStatusOddsRating,
                    targetStats.StatusResistance),
                attackEventId,
                targetOrder: 0)
            .Applied;
    }

}
