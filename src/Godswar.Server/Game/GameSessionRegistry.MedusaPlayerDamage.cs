using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// Routes by the attacker's explicit world-instance membership. Shared
    /// content map 200 is never sufficient to select a Medusa difficulty or
    /// owner.
    /// </summary>
    internal bool TryCommitPlayerMonsterDamageGuarded(
        ClientSession session,
        byte mapId,
        uint objectId,
        Guid expectedMonsterRuntimeInstanceId,
        int attackerCharacterId,
        uint expectedSpawnGeneration,
        ulong expectedHealthRevision,
        in PlayerMonsterCombatAuthority expectedAuthority,
        DateTimeOffset committedAt,
        in CombatResolution resolution,
        out MedusaPlayerMonsterDamageCommit commit)
    {
        ArgumentNullException.ThrowIfNull(session);
        WorldInstanceId completedMedusaInstance = default;
        MedusaRunTerminalClearWorkItem? terminalClear = null;
        MonsterAttackPublicationRecipient terminalFallback = default;
        var defeatInvariantFault = false;
        var clockInvariantFault = false;
        bool applied;
        lock (_gate)
        {
            if (!TryResolvePlayerMonsterAuthorityLocked(
                    session,
                    mapId,
                    out var context,
                    out var runtime,
                    out var currentAuthority) ||
                context.CharacterId != attackerCharacterId ||
                context.Character.Id != attackerCharacterId ||
                !expectedAuthority.IsValid ||
                currentAuthority != expectedAuthority ||
                !_playerLifeRevisions.TryGetValue(
                    session,
                    out var currentLifeRevision) ||
                currentLifeRevision !=
                    expectedAuthority.LifeRevision ||
                expectedMonsterRuntimeInstanceId == Guid.Empty)
            {
                commit = default;
                return false;
            }

            var requestedResolution = resolution;
            var requestedAuthority = expectedAuthority;
            terminalFallback = new(
                context,
                expectedAuthority.LifeRevision);
            if (runtime.Map.HasBoundMedusaEncounter())
            {
                if (!TryPrepareMedusaRunTerminalClearLocked(
                        context.WorldInstanceId,
                        out var preparedTerminalClear))
                {
                    commit = default;
                    return false;
                }
                terminalClear = preparedTerminalClear;
            }
            commit = InvokeWorldOwnerAuthoritativeMutation(
                runtime,
                map => map.TryCommitPlayerMonsterDamageForSessionGuarded(
                    session,
                    objectId,
                    expectedMonsterRuntimeInstanceId,
                    attackerCharacterId,
                    expectedSpawnGeneration,
                    expectedHealthRevision,
                    requestedAuthority,
                    committedAt,
                    requestedResolution));
            applied = commit.Applied;
            defeatInvariantFault = commit.Defeat?.GateOutcome ==
                MedusaOwnedOperationGateOutcome.InvariantFault;
            clockInvariantFault = commit.Outcome ==
                MedusaPlayerMonsterDamageOutcome.OwnerClockInvariantFault;
            if (commit.Outcome ==
                    MedusaPlayerMonsterDamageOutcome.TimedOut ||
                commit.Defeat is
                {
                    Claim.Outcome:
                        MedusaDefeatClaimOutcome.Completed
                })
            {
                completedMedusaInstance = context.WorldInstanceId;
            }
        }

        // Defeat completion or timeout terminalizes the owner before returning. Never acquire
        // a player-status gate or re-enter the owner while the registry gate
        // is held; start the exact current clear only after escaping it.
        if (defeatInvariantFault || clockInvariantFault)
        {
            if (terminalClear is not null)
            {
                terminalClear.FailClosedPreparedMembersNonThrowing();
            }
            else
            {
                FailClosedPreparedMedusaRunTerminalMember(
                    terminalFallback);
            }
        }
        else if (completedMedusaInstance.IsValid)
        {
            if (terminalClear is not null &&
                terminalClear.InstanceId == completedMedusaInstance)
            {
                terminalClear.ScheduleNonThrowing(committedAt);
            }
            else if (terminalClear is not null)
            {
                terminalClear.FailClosedPreparedMembersNonThrowing();
            }
            else
            {
                FailClosedPreparedMedusaRunTerminalMember(
                    terminalFallback);
            }
        }

        return applied;
    }

    internal bool TryCapturePlayerMonsterTarget(
        ClientSession session,
        byte mapId,
        uint objectId,
        out MonsterRuntimeSnapshot target,
        out PlayerMonsterCombatAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (TryResolvePlayerMonsterAuthorityLocked(
                    session,
                    mapId,
                    out _,
                    out var runtime,
                    out authority))
            {
                var captured = InvokeWorldOwner(
                    runtime,
                    map =>
                    {
                        var found = map.TryGetMonsterSnapshot(
                            objectId,
                            out var snapshot);
                        return (Found: found, Snapshot: snapshot);
                    });
                target = captured.Snapshot;
                return captured.Found;
            }

            target = default!;
            authority = default;
            return false;
        }
    }

    internal bool TryCapturePlayerMonsterTargets(
        ClientSession session,
        byte mapId,
        out IReadOnlyList<MonsterRuntimeSnapshot> targets,
        out PlayerMonsterCombatAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (TryResolvePlayerMonsterAuthorityLocked(
                    session,
                    mapId,
                    out _,
                    out var runtime,
                    out authority))
            {
                targets = InvokeWorldOwner(
                    runtime,
                    static map => map.SnapshotMonsters());
                return true;
            }

            targets = [];
            authority = default;
            return false;
        }
    }

    private bool TryResolvePlayerMonsterAuthorityLocked(
        ClientSession session,
        byte mapId,
        out GameSessionContext context,
        out WorldInstanceRuntime runtime,
        out PlayerMonsterCombatAuthority authority)
    {
        if (_sessions.TryGetValue(session, out context!) &&
            context.MapId == mapId &&
            context.Character.CurrentMap == mapId &&
            context.WorldReady &&
            (!context.Ownership.IsValid ||
             IsCurrentAccountSession(
                 context.AccountId,
                 session,
                 context.Ownership)) &&
            TryGetWorldInstance(context, out runtime!) &&
            _playerLifeRevisions.TryGetValue(
                session,
                out var lifeRevision))
        {
            authority = new(
                context.WorldInstanceId,
                context.WorldRevision,
                context.Ownership,
                lifeRevision,
                context.WorldMembershipEpoch);
            return authority.IsValid;
        }

        context = default!;
        runtime = default!;
        authority = default;
        return false;
    }
}
