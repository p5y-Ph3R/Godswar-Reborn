using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Components.Combat;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// Atomically admits a status-only hostile cast against exact development
    /// dummies. Resource/cooldown ownership and all target status commits are
    /// one registry transaction; visual publication remains a handler concern.
    /// </summary>
    internal async Task<TrainingDummyHostileStatusCastDecision>
        ResolveTrainingDummyHostileStatusCastAsync(
            ClientSession attackingSession,
            uint casterObjectId,
            uint targetObjectId,
            SkillCombatDefinition skill,
            HostileStatusEffectDefinition definition,
            Func<long> nextAdmittedCombatRevision,
            DateTimeOffset now,
            CancellationToken cancellationToken,
            Action<GameSessionContext, HostileStatusEffectDefinition>?
                claimAppliedInterruption = null)
    {
        ArgumentNullException.ThrowIfNull(attackingSession);
        ArgumentNullException.ThrowIfNull(nextAdmittedCombatRevision);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsExactStatusOnlyDefinition(skill, definition))
        {
            return TrainingDummyHostileStatusCastDecision.Reject(
                TrainingDummySkillRejectionReason.UnsupportedSkill);
        }
        if (casterObjectId != LocalPlayerObjectId)
        {
            return TrainingDummyHostileStatusCastDecision.Reject(
                TrainingDummySkillRejectionReason.InvalidCasterObject);
        }
        if (!_sessions.TryGetValue(attackingSession, out var route) ||
            !route.WorldReady)
        {
            return TrainingDummyHostileStatusCastDecision.Reject(
                TrainingDummySkillRejectionReason.StaleWorldOwnership);
        }

        var targetSnapshots = ResolveStatusOnlyTargetSnapshots(
            attackingSession,
            route,
            targetObjectId,
            definition);
        if (targetSnapshots.Count == 0)
        {
            return TrainingDummyHostileStatusCastDecision.NotApplicable();
        }

        TryGetRuntimeIncomingDamageMitigation(
            attackingSession,
            now,
            out var attackerRuntime);
        var targetMitigations = new Dictionary<
            ClientSession,
            RuntimeIncomingDamageMitigation>();
        foreach (var target in targetSnapshots)
        {
            TryGetRuntimeIncomingDamageMitigation(
                target.Session,
                now,
                out var mitigation);
            targetMitigations[target.Session] = mitigation;
        }

        var targetDecisions =
            new List<TrainingDummyHostileStatusTargetDecision>();
        GameSessionContext? committedAttacker = null;
        var currentMana = 0;
        var readyAt = now;
        Exception? partialFailure = null;
        lock (_gate)
        {
            if (!_sessions.TryGetValue(attackingSession, out var attacker) ||
                !attacker.WorldReady ||
                attacker.WorldInstanceId != route.WorldInstanceId ||
                attacker.WorldRevision != route.WorldRevision ||
                !ReferenceEquals(attacker.Character, route.Character))
            {
                return TrainingDummyHostileStatusCastDecision.Reject(
                    TrainingDummySkillRejectionReason.StaleWorldOwnership);
            }

            lock (attacker.Character.VitalsSync)
            {
                currentMana = attacker.Character.CurrentMp;
            }
            if (_trainingDummies.TryGetCoreIdentity(
                    attacker.Character,
                    out _))
            {
                return TrainingDummyHostileStatusCastDecision.Reject(
                    TrainingDummySkillRejectionReason.AttackerIsTrainingDummy,
                    currentMana);
            }
            if (attacker.Character.Profession !=
                definition.RequiredProfession)
            {
                return TrainingDummyHostileStatusCastDecision.Reject(
                    TrainingDummySkillRejectionReason.
                        AttackerProfessionMismatch,
                    currentMana);
            }

            var currentTargets = ResolveCurrentStatusOnlyTargetsLocked(
                attacker,
                targetSnapshots,
                skill,
                definition,
                out var targetFailure);
            if (targetFailure != TrainingDummySkillRejectionReason.None)
            {
                return TrainingDummyHostileStatusCastDecision.Reject(
                    targetFailure,
                    currentMana);
            }
            if (currentTargets.Count == 0)
            {
                return TrainingDummyHostileStatusCastDecision.NotApplicable();
            }

            using (AcquirePvpVitalsLocks(
                       currentTargets.Prepend(attacker)))
            {
                if (GetPlayerSkillCastControl(attacker.Session, now) !=
                        PlayerSkillCastControl.None ||
                    IsTrainingDummyHostileSkillUseBlockedLocked(
                        attacker,
                        now))
                {
                    return TrainingDummyHostileStatusCastDecision.Reject(
                        TrainingDummySkillRejectionReason.ElementalControl,
                        attacker.Character.CurrentMp);
                }
                currentMana = attacker.Character.CurrentMp;
                if (currentMana < definition.ManaCost)
                {
                    return TrainingDummyHostileStatusCastDecision.Reject(
                        TrainingDummySkillRejectionReason.InsufficientMana,
                        currentMana);
                }

                var plans = new List<HostileStatusCastTargetPlan>(
                    currentTargets.Count);
                foreach (var target in currentTargets)
                {
                    var admission = EvaluatePvpBasicAttack(
                        attacker.Character,
                        target.Character,
                        now);
                    if (!IsExactTrainingAdmission(admission))
                    {
                        return TrainingDummyHostileStatusCastDecision.Reject(
                            TrainingDummySkillRejectionReason.AdmissionDenied,
                            currentMana);
                    }

                    var attackerCombat = CombatCharacterStatsAdapter
                        .ApplyRuntimeAttackerModifiers(
                            CombatCharacterStatsAdapter.FromCharacter(
                                attacker.Character),
                            attackerRuntime.StatusAggregate);
                    var mitigation = targetMitigations[target.Session];
                    var targetStats = target.Character.CalculatedStats ??
                        CharacterStats.FromCharacter(target.Character);
                    var targetCombat = CombatCharacterStatsAdapter
                        .ApplyRuntimeTargetModifiers(
                            CombatCharacterStatsAdapter.ToTarget(
                                target.Character.Level,
                                targetStats,
                                mitigation.PhysicalDefenseBonus,
                                mitigation.MagicDefenseBonus,
                                ToCombatBasisPoints(
                                    mitigation.PhysicalDamageReduction),
                                ToCombatBasisPoints(
                                    mitigation.MagicDamageReduction),
                                mitigation.
                                    PhysicalDamageTakenIncreaseBasisPoints,
                                mitigation.
                                    MagicDamageTakenIncreaseBasisPoints),
                            mitigation.StatusAggregate);
                    if (TryAdjustPvpElementalResolutionInputsLocked(
                            attacker,
                            target,
                            now,
                            attackerCombat,
                            targetCombat,
                            out var elementalInputs))
                    {
                        if (!elementalInputs.AttackerActionAllowed)
                        {
                            return TrainingDummyHostileStatusCastDecision
                                .Reject(
                                    TrainingDummySkillRejectionReason.
                                        ElementalControl,
                                    currentMana);
                        }
                        attackerCombat = elementalInputs.Attacker;
                        targetCombat = elementalInputs.Target;
                    }

                    plans.Add(new(
                        target,
                        admission,
                        attackerCombat.Hit,
                        targetCombat.Dodge,
                        attacker.Character.VitalsRevision,
                        target.Character.VitalsRevision));
                }

                if (!TryResolveCurrentHostileSkillOwnerLocked(
                        attackingSession,
                        attacker.Character,
                        out var cooldownOwner))
                {
                    return TrainingDummyHostileStatusCastDecision.Reject(
                        TrainingDummySkillRejectionReason.
                            StaleWorldOwnership,
                        currentMana);
                }
                if (!_hostileSkillCooldowns.TryClaim(
                        cooldownOwner,
                        checked((uint)definition.SkillId),
                        definition.Cooldown,
                        now,
                        out var cooldownLease,
                        out readyAt))
                {
                    return TrainingDummyHostileStatusCastDecision.Reject(
                        TrainingDummySkillRejectionReason.CooldownActive,
                        currentMana,
                        readyAt);
                }

                long actionRevision;
                try
                {
                    actionRevision = nextAdmittedCombatRevision();
                    if (actionRevision <= 0)
                    {
                        throw new InvalidOperationException(
                            "Admitted hostile-status revisions must be " +
                            "positive.");
                    }
                }
                catch
                {
                    _hostileSkillCooldowns.TryRelease(cooldownLease);
                    throw;
                }

                attacker.Character.CurrentMp = checked(
                    attacker.Character.CurrentMp - definition.ManaCost);
                attacker.Character.MarkVitalsChanged();
                committedAttacker = attacker;
                try
                {
                    for (var index = 0; index < plans.Count; index++)
                    {
                        var plan = plans[index];
                        var eventId = CombatEventIdentity.ForPlayerSkill(
                            attacker.CharacterId,
                            plan.Target.CharacterId,
                            plan.AttackerVitalsRevision,
                            plan.TargetVitalsRevision,
                            actionRevision,
                            checked((uint)definition.SkillId));
                        _ = TryCommitTrainingDummyHostileStatusLocked(
                            attacker,
                            plan.Target,
                            plan.Admission,
                            definition,
                            new HostileStatusTriggerEvidence(
                                definition.Trigger,
                                eventId,
                                index),
                            plan.EffectiveAttackerHit,
                            plan.EffectiveTargetDodge,
                            now,
                            claimAppliedInterruption,
                            out var application);
                        if (!application.Attempted)
                        {
                            throw new InvalidOperationException(
                                "A committed hostile-status cast lost its " +
                                $"target fence: {application.Disposition}.");
                        }
                        targetDecisions.Add(new(
                            plan.Target,
                            application));
                    }
                }
                catch (Exception ex)
                {
                    partialFailure = ex;
                }
            }
        }

        if (committedAttacker is not null)
        {
            UpdateCharacter(
                committedAttacker.Session,
                committedAttacker.Character,
                advanceWorldRevision: false);
            try
            {
                await PersistRoutineVitalsAsync(
                    committedAttacker,
                    CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine(
                    "[hostile-status] committed mana persistence deferred " +
                    $"character={committedAttacker.DisplayName} " +
                    $"skill={definition.SkillId}: {ex.Message}");
            }
        }

        if (partialFailure is not null)
        {
            Console.WriteLine(
                "[hostile-status] partial commit " +
                $"skill={definition.SkillId} " +
                $"targets={targetDecisions.Count} " +
                $"error={partialFailure.Message}");
            return new(
                true,
                TrainingDummySkillRejectionReason.PartialCommitFailure,
                committedAttacker,
                targetDecisions.AsReadOnly(),
                committedAttacker?.Character.CurrentMp ?? currentMana,
                readyAt);
        }

        return new(
            true,
            TrainingDummySkillRejectionReason.None,
            committedAttacker,
            targetDecisions.AsReadOnly(),
            committedAttacker?.Character.CurrentMp ?? currentMana,
            readyAt);
    }

}
