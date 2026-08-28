using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private sealed partial class MedusaInstanceOwnerBoundAggregate
    {
        private const int MonsterPlayerReplayCapacity = 4_096;

        private readonly Dictionary<
            MedusaMonsterPlayerReplayKey,
            MedusaMonsterPlayerReplayEntry> _monsterPlayerHitReplay =
            new(MonsterPlayerReplayCapacity);
        private readonly Queue<MedusaMonsterPlayerReplayKey>
            _monsterPlayerHitReplayOrder =
            new(MonsterPlayerReplayCapacity);

        public MedusaMonsterPlayerHitCaptureOutcome
            PreviewMonsterPlayerHit(
                int targetCharacterId,
                PlayerOwnershipFence targetOwnership,
                long targetLifeRevision,
                long targetWorldMembershipEpoch,
                uint sourceObjectId,
                uint sourceSpawnGeneration,
                DateTimeOffset committedAt,
                out MedusaEncounterEffectKind? effectKind)
        {
            effectKind = null;
            EnsureCoupledClocks(out _, out _);
            if (!_run.IsCharacterAdmitted(targetCharacterId))
            {
                return MedusaMonsterPlayerHitCaptureOutcome
                    .CharacterNotAdmitted;
            }

            var mechanics = _mechanics.PreviewMonsterHit(
                targetCharacterId,
                targetOwnership,
                targetLifeRevision,
                targetWorldMembershipEpoch,
                sourceObjectId,
                sourceSpawnGeneration,
                committedAt);
            if (mechanics is not (
                    MedusaMechanicHitOutcome
                        .MonsterHasNoAuthoredMechanic or
                    MedusaMechanicHitOutcome.Applied or
                    MedusaMechanicHitOutcome.Refreshed))
            {
                return CaptureOutcomeFor(mechanics);
            }
            var runClock = _run.PreviewTime(committedAt);
            if (runClock != MedusaRunClockOutcome.Active)
            {
                return CaptureOutcomeFor(runClock);
            }
            if (mechanics ==
                MedusaMechanicHitOutcome.MonsterHasNoAuthoredMechanic)
            {
                return MedusaMonsterPlayerHitCaptureOutcome.Captured;
            }
            if (!TryResolveEffectKind(
                    sourceObjectId,
                    sourceSpawnGeneration,
                    out var resolvedKind))
            {
                return MedusaMonsterPlayerHitCaptureOutcome
                    .MechanicUnavailable;
            }

            effectKind = resolvedKind;
            return MedusaMonsterPlayerHitCaptureOutcome.Captured;
        }

        public MedusaPreparedMonsterPlayerEffect ReserveMonsterPlayerEffect(
            int targetCharacterId,
            PlayerOwnershipFence targetOwnership,
            long targetLifeRevision,
            long targetWorldMembershipEpoch,
            uint sourceObjectId,
            uint sourceSpawnGeneration,
            DateTimeOffset committedAt,
            bool applyAuthoredEffect)
        {
            var preview = PreviewMonsterPlayerHit(
                targetCharacterId,
                targetOwnership,
                targetLifeRevision,
                targetWorldMembershipEpoch,
                sourceObjectId,
                sourceSpawnGeneration,
                committedAt,
                out var effectKind);
            if (!applyAuthoredEffect)
            {
                effectKind = null;
            }
            if (preview != MedusaMonsterPlayerHitCaptureOutcome.Captured)
            {
                return new(
                    preview,
                    effectKind,
                    reservation: null,
                    runSnapshot: null,
                    mechanicsSnapshot: null);
            }

            var runSnapshot = _run.CaptureMonsterHitClockSnapshot();
            var mechanicsSnapshot =
                _mechanics.CaptureMonsterHitTransactionSnapshot();
            try
            {
                var runClock = _run.ObserveTime(committedAt);
                if (runClock != MedusaRunClockOutcome.Active)
                {
                    throw new InvalidOperationException(
                        "A preflighted Medusa hit changed run-clock outcome " +
                        "while its owner lane remained held.");
                }

                if (effectKind is null)
                {
                    var clock = _mechanics.ObserveTime(committedAt);
                    if (clock.Outcome !=
                            MedusaMechanicsClockOutcome.Advanced ||
                        clock.PeriodicDamage is not null)
                    {
                        throw new InvalidOperationException(
                            "A preflighted Medusa no-effect hit failed to " +
                            "advance its coupled mechanics clock.");
                    }

                    EnsureCoupledClocks(out _, out _);
                    return new(
                        preview,
                        effectKind,
                        reservation: null,
                        runSnapshot,
                        mechanicsSnapshot);
                }

                var reserved = _mechanics.ReserveMonsterHit(
                    targetCharacterId,
                    targetOwnership,
                    targetLifeRevision,
                    targetWorldMembershipEpoch,
                    sourceObjectId,
                    sourceSpawnGeneration,
                    committedAt);
                if (reserved.Reservation is null ||
                    reserved.Outcome is not (
                        MedusaMechanicHitOutcome.Applied or
                        MedusaMechanicHitOutcome.Refreshed) ||
                    reserved.PeriodicDamage is not null)
                {
                    throw new InvalidOperationException(
                        "A preflighted Medusa effect failed while run and " +
                        "mechanics remained under one owner.");
                }

                EnsureCoupledClocks(out _, out _);
                return new(
                    preview,
                    effectKind,
                    reserved.Reservation,
                    runSnapshot,
                    mechanicsSnapshot);
            }
            catch
            {
                _mechanics.RestoreMonsterHitTransactionSnapshot(
                    mechanicsSnapshot);
                _run.RestoreMonsterHitClockSnapshot(runSnapshot);
                throw;
            }
        }

        public bool TryBeginMonsterPlayerReplay(
            in MedusaMonsterPlayerSourceAuthority source,
            in MedusaMonsterPlayerTargetAuthority target,
            out bool identityConflict)
        {
            var key = new MedusaMonsterPlayerReplayKey(
                target.CharacterId,
                source.AttackEventId);
            if (_monsterPlayerHitReplay.TryGetValue(
                    key,
                    out var existing))
            {
                identityConflict =
                    existing.Source != source ||
                    existing.Target != target;
                return false;
            }

            if (_monsterPlayerHitReplay.Count >=
                    MonsterPlayerReplayCapacity &&
                _monsterPlayerHitReplayOrder.Count == 0)
            {
                // Validate durable eviction capacity before player HP can be
                // touched.  The owner lane permits no second pending claim.
                throw new InvalidOperationException(
                    "Medusa replay order is unavailable at capacity.");
            }

            // A claim is tentative until player vitals commit.  Do not evict
            // an unrelated durable replay entry here: a later rollback must
            // be able to restore the ledger exactly.  The owner lane permits
            // only one such pending entry, so the temporary bound is
            // capacity + 1 and durable eviction can happen at completion.
            _monsterPlayerHitReplay.Add(
                key,
                new(source, target));
            identityConflict = false;
            return true;
        }

        public void CompleteMonsterPlayerReplay(
            in MedusaMonsterPlayerSourceAuthority source,
            in MedusaMonsterPlayerTargetAuthority target)
        {
            var key = new MedusaMonsterPlayerReplayKey(
                target.CharacterId,
                source.AttackEventId);
            while (_monsterPlayerHitReplay.Count >
                   MonsterPlayerReplayCapacity)
            {
                if (!_monsterPlayerHitReplayOrder.TryDequeue(
                        out var evicted))
                {
                    break;
                }
                if (evicted != key)
                {
                    _monsterPlayerHitReplay.Remove(evicted);
                }
            }
            _monsterPlayerHitReplayOrder.Enqueue(key);
        }

        public void RollBackMonsterPlayerReplay(
            in MedusaMonsterPlayerSourceAuthority source,
            in MedusaMonsterPlayerTargetAuthority target)
        {
            _monsterPlayerHitReplay.Remove(new(
                target.CharacterId,
                source.AttackEventId));
        }

        public MedusaMechanicHitResult FinalizeMonsterPlayerEffect(
            MedusaPreparedMonsterPlayerEffect prepared)
        {
            // Reservation ownership, target identity, dictionary capacity,
            // and sequence allocation were all proven before player HP. The
            // post-HP path is deliberately the capability's direct,
            // allocation-free replacement with no fallible revalidation.
            var result = prepared.Reservation!.FinalizeEffect();
            prepared.Completed = true;
            return result;
        }

        public void CommitMonsterPlayerEffectWithoutPublication(
            MedusaPreparedMonsterPlayerEffect prepared)
        {
            if (prepared.Completed)
            {
                return;
            }

            _mechanics.CancelReservedMonsterHit(prepared.Reservation);
            prepared.Completed = true;
        }

        public void RollBackMonsterPlayerEffect(
            MedusaPreparedMonsterPlayerEffect? prepared)
        {
            if (prepared is null || prepared.Completed)
            {
                return;
            }

            _mechanics.CancelReservedMonsterHit(prepared.Reservation);
            if (prepared.MechanicsSnapshot is { } mechanics)
            {
                _mechanics.RestoreMonsterHitTransactionSnapshot(mechanics);
            }
            if (prepared.RunSnapshot is { } run)
            {
                _run.RestoreMonsterHitClockSnapshot(run);
            }
            prepared.Completed = true;
        }

        public MedusaPeriodicDamageReserveResult
            ClearMonsterPlayerEffectsForLife(
            int characterId,
            PlayerOwnershipFence targetOwnership,
            long targetLifeRevision,
            long targetWorldMembershipEpoch,
            DateTimeOffset observedAt)
        {
            EnsureCoupledClocks(out _, out _);
            var periodic = _mechanics.ReservePeriodicDamage(observedAt);
            if (periodic.Outcome is
                MedusaPeriodicDamageReserveOutcome.Reserved or
                MedusaPeriodicDamageReserveOutcome.TimestampMovedBackward)
            {
                return periodic;
            }

            if (_run.OwnerState != MedusaRunState.Active)
            {
                _mechanics.ClearCharacterLifeAtCurrentClock(
                    characterId,
                    targetOwnership,
                    targetLifeRevision,
                    targetWorldMembershipEpoch);
                return new(
                    MedusaPeriodicDamageReserveOutcome.NoneDue,
                    Reservation: null);
            }

            var runClock = _run.ObserveTime(observedAt);
            var mechanicsClock = _mechanics.ObserveTime(observedAt);
            if (!MechanicsClockMatchesRunClock(runClock, mechanicsClock) ||
                !HasCoupledClockScalars())
            {
                return new(
                    MedusaPeriodicDamageReserveOutcome.InvariantFault,
                    Reservation: null);
            }
            if (runClock ==
                    MedusaRunClockOutcome.DeadlineBoundaryUnresolved ||
                mechanicsClock.Outcome ==
                    MedusaMechanicsClockOutcome
                        .DeadlineBoundaryUnresolved)
            {
                return new(
                    MedusaPeriodicDamageReserveOutcome
                        .DeadlineBoundaryUnresolved,
                    Reservation: null);
            }

            _mechanics.ClearCharacterLifeAtCurrentClock(
                characterId,
                targetOwnership,
                targetLifeRevision,
                targetWorldMembershipEpoch);
            return new(
                MedusaPeriodicDamageReserveOutcome.NoneDue,
                Reservation: null);
        }

        private bool TryResolveEffectKind(
            uint sourceObjectId,
            uint sourceSpawnGeneration,
            out MedusaEncounterEffectKind kind)
        {
            kind = default;
            if (!_bindings.TryGetValue(
                    new(sourceObjectId, sourceSpawnGeneration),
                    out var binding) ||
                !MedusaIslandRosterPolicy.TryGetSpawn(
                    binding.RosterSpawnId,
                    out var roster) ||
                roster.Skill is not { } skill ||
                !MedusaEncounterMechanicsPolicy.TryGetEffectDefinition(
                    skill.Mechanic,
                    _run.ContentMapId.Value,
                    out var effect))
            {
                return false;
            }

            kind = effect.Kind;
            return true;
        }

        private static MedusaMonsterPlayerHitCaptureOutcome
            CaptureOutcomeFor(MedusaRunClockOutcome outcome) =>
            outcome switch
            {
                MedusaRunClockOutcome.TimestampMovedBackward =>
                    MedusaMonsterPlayerHitCaptureOutcome
                        .TimestampMovedBackward,
                MedusaRunClockOutcome.DeadlineBoundaryUnresolved =>
                    MedusaMonsterPlayerHitCaptureOutcome
                        .DeadlineBoundaryUnresolved,
                MedusaRunClockOutcome.TimedOut =>
                    MedusaMonsterPlayerHitCaptureOutcome.TimedOut,
                _ => MedusaMonsterPlayerHitCaptureOutcome.RunNotActive
            };

        private static MedusaMonsterPlayerHitCaptureOutcome
            CaptureOutcomeFor(MedusaMechanicHitOutcome outcome) =>
            outcome switch
            {
                MedusaMechanicHitOutcome.CharacterNotAdmitted =>
                    MedusaMonsterPlayerHitCaptureOutcome
                        .CharacterNotAdmitted,
                MedusaMechanicHitOutcome.UnknownMonster =>
                    MedusaMonsterPlayerHitCaptureOutcome.UnknownMonster,
                MedusaMechanicHitOutcome.StaleMonsterGeneration =>
                    MedusaMonsterPlayerHitCaptureOutcome
                        .StaleMonsterGeneration,
                MedusaMechanicHitOutcome.MonsterRetired =>
                    MedusaMonsterPlayerHitCaptureOutcome
                        .MonsterNotAttackable,
                MedusaMechanicHitOutcome.TimestampMovedBackward =>
                    MedusaMonsterPlayerHitCaptureOutcome
                        .TimestampMovedBackward,
                MedusaMechanicHitOutcome.PeriodicDamageRequired =>
                    MedusaMonsterPlayerHitCaptureOutcome
                        .PeriodicDamageHandoffUnavailable,
                _ => MedusaMonsterPlayerHitCaptureOutcome
                    .MechanicUnavailable
            };

        private readonly record struct MedusaMonsterPlayerReplayKey(
            int CharacterId,
            ulong AttackEventId);

        private readonly record struct MedusaMonsterPlayerReplayEntry(
            MedusaMonsterPlayerSourceAuthority Source,
            MedusaMonsterPlayerTargetAuthority Target);
    }
}
