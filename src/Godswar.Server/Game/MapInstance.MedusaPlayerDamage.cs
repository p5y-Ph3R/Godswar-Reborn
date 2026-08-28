using Godswar.Server.Application.Characters;
using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    internal MedusaPlayerMonsterDamageCommit
        TryCommitPlayerMonsterDamageForSessionGuarded(
            ClientSession session,
            uint objectId,
            Guid expectedMonsterRuntimeInstanceId,
            int attackerCharacterId,
            uint expectedSpawnGeneration,
            ulong expectedHealthRevision,
            in PlayerMonsterCombatAuthority expectedAuthority,
            DateTimeOffset committedAt,
            in CombatResolution source)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_medusaOwnershipGate)
        {
            lock (_membershipGate)
            {
                if (!expectedAuthority.IsValid ||
                    expectedMonsterRuntimeInstanceId == Guid.Empty ||
                    !_sessions.TryGetValue(session, out var context) ||
                    context.WorldInstanceId != WorldInstanceId ||
                    context.WorldInstanceId !=
                        expectedAuthority.WorldInstanceId ||
                    context.WorldRevision !=
                        expectedAuthority.WorldRevision ||
                    context.WorldMembershipEpoch !=
                        expectedAuthority.WorldMembershipEpoch ||
                    context.Ownership != expectedAuthority.Ownership ||
                    context.MapId != MapId ||
                    context.CharacterId != attackerCharacterId ||
                    context.Character.Id != attackerCharacterId ||
                    context.Character.CurrentMap != MapId ||
                    !context.WorldReady)
                {
                    return RejectedPlayerDamage(
                        MedusaPlayerMonsterDamageOutcome
                            .CurrentMembershipRequired,
                        source);
                }

                return CommitPlayerMonsterDamageCore(
                    objectId,
                    expectedMonsterRuntimeInstanceId,
                    attackerCharacterId,
                    expectedAuthority.Ownership,
                    expectedAuthority.LifeRevision,
                    expectedAuthority.WorldMembershipEpoch,
                    expectedSpawnGeneration,
                    expectedHealthRevision,
                    committedAt,
                    source);
            }
        }
    }

    private MedusaPlayerMonsterDamageCommit
        CommitPlayerMonsterDamageCore(
            uint objectId,
            Guid expectedMonsterRuntimeInstanceId,
            int attackerCharacterId,
            PlayerOwnershipFence attackerOwnership,
            long attackerLifeRevision,
            long attackerWorldMembershipEpoch,
            uint expectedSpawnGeneration,
            ulong expectedHealthRevision,
            DateTimeOffset committedAt,
            in CombatResolution source)
    {
        if (!IsValidCommittedResolution(source))
        {
            return RejectedPlayerDamage(
                MedusaPlayerMonsterDamageOutcome.InvalidResolution,
                source);
        }

        lock (_medusaOwnershipGate)
        {
            var owner = _medusaInstanceOwner;
            lock (_monsterRuntimeGate)
            {
                if (owner is null)
                {
                    return CommitUnboundPlayerDamageLocked(
                        objectId,
                        expectedMonsterRuntimeInstanceId,
                        attackerCharacterId,
                        expectedSpawnGeneration,
                        expectedHealthRevision,
                        committedAt,
                        source);
                }

                if (!HasCompleteMedusaDamageState(owner))
                {
                    return RejectedPlayerDamage(
                        MedusaPlayerMonsterDamageOutcome
                            .AttachmentStateConflict,
                        source);
                }

                if (!_monsterRuntime!.TryGetSnapshot(
                        objectId,
                        out var target) ||
                    target.SpawnGeneration !=
                        expectedSpawnGeneration ||
                    target.RuntimeInstanceId !=
                        expectedMonsterRuntimeInstanceId ||
                    target.RuntimeInstanceId !=
                        _medusaMonsterAttachment!.RuntimeInstanceId)
                {
                    return RejectedPlayerDamage(
                        MedusaPlayerMonsterDamageOutcome
                            .StaleMonsterGeneration,
                        source);
                }
                if (target.HealthRevision != expectedHealthRevision)
                {
                    return RejectedPlayerDamage(
                        MedusaPlayerMonsterDamageOutcome
                            .StaleHealthRevision,
                        source);
                }
                if (!target.IsAlive ||
                    !target.IsSpawned ||
                    target.CombatPhase is MonsterCombatPhase.Returning or
                        MonsterCombatPhase.AwaitingRetirement)
                {
                    return RejectedPlayerDamage(
                        MedusaPlayerMonsterDamageOutcome.RuntimeRejected,
                        source);
                }

                var preview = owner.PreviewPlayerDamage(
                    attackerCharacterId,
                    attackerOwnership,
                    attackerLifeRevision,
                    attackerWorldMembershipEpoch,
                    objectId,
                    expectedSpawnGeneration,
                    committedAt,
                    source,
                    out _,
                    out var authoritative);
                if (preview !=
                    MedusaPlayerMonsterDamageOutcome.AppliedMedusa)
                {
                    if (preview is
                        MedusaPlayerMonsterDamageOutcome
                            .DeadlineBoundaryUnresolved or
                        MedusaPlayerMonsterDamageOutcome.TimedOut)
                    {
                        var observation = owner.ObserveTime(committedAt);
                        var expectedGate = preview ==
                            MedusaPlayerMonsterDamageOutcome.TimedOut
                                ? MedusaOwnedOperationGateOutcome.TimedOut
                                : MedusaOwnedOperationGateOutcome
                                    .DeadlineBoundaryUnresolved;
                        if (observation.GateOutcome != expectedGate)
                        {
                            return RejectedPlayerDamage(
                                MedusaPlayerMonsterDamageOutcome
                                    .OwnerClockInvariantFault,
                                source);
                        }
                    }
                    return RejectedPlayerDamage(preview, source);
                }

                var lethal = authoritative.Damage >= target.CurrentHealth;
                MedusaInstanceOwnerBoundAggregate.PreparedPlayerDefeat?
                    preparedDefeat = null;
                if (lethal)
                {
                    if (!owner.TryPrepareDefeat(
                        attackerCharacterId,
                        objectId,
                        expectedSpawnGeneration,
                        committedAt,
                        out preparedDefeat,
                        out _))
                    {
                        return RejectedPlayerDamage(
                            MedusaPlayerMonsterDamageOutcome
                                .DefeatPreflightRejected,
                            authoritative);
                    }
                }

                var clock = owner.PreparePlayerDamageClock(committedAt);
                bool applied;
                MonsterDamageResult damage;
                try
                {
                    applied = _monsterRuntime.TryApplyDamage(
                        objectId,
                        authoritative.Damage,
                        attackerCharacterId,
                        expectedSpawnGeneration,
                        committedAt,
                        out damage);
                }
                catch
                {
                    owner.RollBackPlayerDamageClock(clock);
                    throw;
                }
                if (!applied ||
                    damage.HealthMutation is null ||
                    damage.BeforeHealth == damage.AfterHealth)
                {
                    owner.RollBackPlayerDamageClock(clock);
                    return RejectedPlayerDamage(
                        MedusaPlayerMonsterDamageOutcome.RuntimeRejected,
                        authoritative);
                }
                MedusaInstanceOwnerBoundAggregate
                    .CommitPlayerDamageClock(clock);

                MedusaOwnedDefeatResult? defeat = null;
                if (damage.Killed)
                {
                    if (preparedDefeat is null)
                    {
                        defeat = MedusaInstanceOwnerBoundAggregate
                            .DefeatInvariantFault();
                    }
                    else
                    {
                        ApplyProtocolCheckPreparedDefeatFault(
                            owner,
                            preparedDefeat);
                        defeat = owner.CompletePreparedDefeat(
                            preparedDefeat);
                    }
                }
                else if (lethal)
                {
                    defeat = MedusaInstanceOwnerBoundAggregate
                        .DefeatInvariantFault();
                }

                return new(
                    MedusaPlayerMonsterDamageOutcome.AppliedMedusa,
                    authoritative,
                    damage,
                    defeat);
            }
        }
    }

    private MedusaPlayerMonsterDamageCommit
        CommitUnboundPlayerDamageLocked(
            uint objectId,
            Guid expectedMonsterRuntimeInstanceId,
            int attackerCharacterId,
            uint expectedSpawnGeneration,
            ulong expectedHealthRevision,
            DateTimeOffset committedAt,
            in CombatResolution source)
    {
        if (_monsterRuntime is not null &&
            _monsterRuntime.TryGetSnapshot(objectId, out var target) &&
            target.RuntimeInstanceId ==
                expectedMonsterRuntimeInstanceId &&
            target.SpawnGeneration == expectedSpawnGeneration &&
            target.HealthRevision == expectedHealthRevision &&
            _monsterRuntime.TryApplyDamage(
                objectId,
                source.Damage,
                attackerCharacterId,
                expectedSpawnGeneration,
                committedAt,
                out var damage) &&
            damage.HealthMutation is not null &&
            damage.BeforeHealth != damage.AfterHealth)
        {
            return new(
                MedusaPlayerMonsterDamageOutcome.AppliedUnbound,
                source,
                damage,
                Defeat: null);
        }

        return RejectedPlayerDamage(
            MedusaPlayerMonsterDamageOutcome.RuntimeRejected,
            source);
    }

    private bool HasCompleteMedusaDamageState(
        MedusaInstanceOwnerBoundAggregate owner) =>
        _medusaMonsterAttachment is { } attachment &&
        owner.MatchesAttachment(attachment) &&
        attachment.RuntimeMode == MonsterRuntimeMode.Ecs &&
        attachment.RespawnPolicy == MonsterRespawnPolicy.Never &&
        attachment.MonsterCount ==
            MedusaIslandRosterPolicy.TotalSpawnCount +
            MedusaIslandAmbientSpawnPolicy.CountFor(
                attachment.Difficulty) &&
        attachment.RuntimeInstanceId != Guid.Empty &&
        attachment.Fingerprint.Length == 64 &&
        _monsterRespawnPolicy == MonsterRespawnPolicy.Never &&
        _monsterRuntime is not null &&
        _monsterRuntime.MapId == attachment.ContentMapId.Value &&
        _monsterRuntime.Count == attachment.MonsterCount;

    private static bool IsValidCommittedResolution(
        in CombatResolution source) =>
        source.Hit &&
        source.Damage > 0 &&
        source.Channel is CombatDamageChannel.Physical or
            CombatDamageChannel.Magic &&
        source.Outcome is CombatHitOutcome.Normal or
            CombatHitOutcome.Critical;

    private static MedusaPlayerMonsterDamageOutcome OutcomeFor(
        in MedusaOwnedDefeatPreview preview)
    {
        if (preview.MechanicsOutcome ==
                MedusaMechanicSourceRetireOutcome
                    .PeriodicDamageRequired ||
            preview.HasDuePeriodicDamage)
        {
            return MedusaPlayerMonsterDamageOutcome
                .PeriodicDamageHandoffUnavailable;
        }
        if (preview.RunOutcome !=
            MedusaDefeatClaimPreviewOutcome.Eligible)
        {
            return preview.RunOutcome switch
            {
                MedusaDefeatClaimPreviewOutcome.CharacterNotAdmitted =>
                    MedusaPlayerMonsterDamageOutcome.CharacterNotAdmitted,
                MedusaDefeatClaimPreviewOutcome.UnknownSpawn =>
                    MedusaPlayerMonsterDamageOutcome.UnknownMonster,
                MedusaDefeatClaimPreviewOutcome.StaleSpawnGeneration =>
                    MedusaPlayerMonsterDamageOutcome
                        .StaleMonsterGeneration,
                MedusaDefeatClaimPreviewOutcome.DuplicateDefeat =>
                    MedusaPlayerMonsterDamageOutcome.DuplicateDefeat,
                MedusaDefeatClaimPreviewOutcome.TimestampMovedBackward =>
                    MedusaPlayerMonsterDamageOutcome
                        .TimestampMovedBackward,
                MedusaDefeatClaimPreviewOutcome
                    .DeadlineBoundaryUnresolved =>
                    MedusaPlayerMonsterDamageOutcome
                        .DeadlineBoundaryUnresolved,
                MedusaDefeatClaimPreviewOutcome.TimedOut =>
                    MedusaPlayerMonsterDamageOutcome.TimedOut,
                MedusaDefeatClaimPreviewOutcome.RunNotActive =>
                    MedusaPlayerMonsterDamageOutcome.RunNotActive,
                _ => MedusaPlayerMonsterDamageOutcome
                    .DefeatPreflightRejected
            };
        }

        return MedusaPlayerMonsterDamageOutcome.DefeatPreflightRejected;
    }

    private static MedusaPlayerMonsterDamageCommit RejectedPlayerDamage(
        MedusaPlayerMonsterDamageOutcome outcome,
        in CombatResolution source) => new(
        outcome,
        source,
        DamageResult: null,
        Defeat: null);
}
