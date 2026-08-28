using System.Collections.Concurrent;
using Godswar.Server.Networking;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    public async Task<MountRideActivationCommit?> TryActivateMountRideAndPublishAsync(
        ClientSession session,
        int expectedCharacterId,
        long expectedLifeRevision,
        MountRideDefinition mount,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!TryGetOrCreatePlayerStatusState(session, out var state))
        {
            return null;
        }

        var admissionClaims = new ExactStatusDisconnectClaims();
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            GameCharacter character;
            int accountId;
            int currentMana;
            lock (_gate)
            {
                if (!_sessions.TryGetValue(session, out var context) ||
                    !context.WorldReady ||
                    context.CharacterId != expectedCharacterId ||
                    !_playerLifeRevisions.TryGetValue(session, out var lifeRevision) ||
                    lifeRevision != expectedLifeRevision)
                {
                    return null;
                }

                character = context.Character;
                accountId = context.AccountId;
                lock (character.VitalsSync)
                {
                    if (character.CurrentHp <= 0 ||
                        character.Level < mount.MountLevel ||
                        !RequireItemContent().Mounts.TryGetEquippedRideDefinition(character, out var currentMount) ||
                        currentMount != mount ||
                        character.CurrentMp < MountCatalog.RideManaCost ||
                        state.RuntimeStatuses.TryGetValue(
                            MountCatalog.RuntimeStatusKind,
                            out var existing) && existing.ExpiresAt > now)
                    {
                        return null;
                    }

                    var statusRevision = checked(state.Revision + 1);
                    var status = new ActiveRuntimeStatus(
                        mount.StatusId,
                        MountCatalog.RuntimeStatusKind,
                        Priority: 1,
                        Beneficial: false,
                        DateTimeOffset.MaxValue,
                        ClientStatusAggregate.Empty,
                        statusRevision,
                        MovementSpeedBonus: mount.SpeedBonus);
                    character.CurrentMp -= MountCatalog.RideManaCost;
                    currentMana = character.CurrentMp;
                    character.MarkVitalsChanged();
                    state.Revision = statusRevision;
                    state.RuntimeStatuses[MountCatalog.RuntimeStatusKind] = status;
                    RefreshSkillCastControlSnapshot(state);
                }
            }

            if (_checkpointCoordinator is not null ||
                _store is not null)
            {
                try
                {
                    // Ride is not externally successful until its MP cost is
                    // durable. Do not let a socket cancellation after status
                    // publication refund the cast on the next login.
                    await FlushVitalsAsync(
                        accountId,
                        character,
                        CancellationToken.None);
                }
                catch
                {
                    state.RuntimeStatuses.Remove(MountCatalog.RuntimeStatusKind);
                    RefreshSkillCastControlSnapshot(state);
                    lock (character.VitalsSync)
                    {
                        character.CurrentMp = Math.Min(
                            character.MaxMp,
                            (int)Math.Min(
                                int.MaxValue,
                                (long)character.CurrentMp + MountCatalog.RideManaCost));
                        character.MarkVitalsChanged();
                    }

                    try
                    {
                        await FlushVitalsAsync(
                            accountId,
                            character,
                            CancellationToken.None);
                    }
                    catch (Exception rollbackError)
                    {
                        Console.WriteLine(
                            $"[mount] Ride MP rollback persistence failed character={character.Name}: {rollbackError.Message}");
                    }

                    throw;
                }
            }

            RefreshSkillCastControlSnapshot(state);
            await PublishStatusSnapshotLockedAsync(
                session,
                state,
                now,
                "mount-ride",
                force: true,
                broadcast: true,
                cancellationToken,
                forceLocalGameDataSynchronization: true,
                claimedDisconnects: admissionClaims);
            return new MountRideActivationCommit(character, currentMana, StatusChanged: true);
        }
        finally
        {
            state.Gate.Release();
            admissionClaims.CompleteAll(this);
        }
    }

    public async Task<bool> RemovePersistentRuntimeStatusForLifeRevisionAndPublishAsync(
        ClientSession session,
        long expectedLifeRevision,
        int kind,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!TryGetOrCreatePlayerStatusState(session, out var state))
        {
            return false;
        }

        var admissionClaims = new ExactStatusDisconnectClaims();
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (!_sessions.ContainsKey(session) ||
                    !_playerLifeRevisions.TryGetValue(session, out var lifeRevision) ||
                    lifeRevision != expectedLifeRevision ||
                    !state.RuntimeStatuses.Remove(kind))
                {
                    return false;
                }
            }

            RefreshSkillCastControlSnapshot(state);
            await PublishStatusSnapshotLockedAsync(
                session,
                state,
                now,
                reason,
                force: true,
                broadcast: true,
                cancellationToken,
                forceLocalGameDataSynchronization:
                    kind == MountCatalog.RuntimeStatusKind,
                claimedDisconnects: admissionClaims);
            return true;
        }
        finally
        {
            state.Gate.Release();
            admissionClaims.CompleteAll(this);
        }
    }

    private bool RemovePersistentRuntimeStatusForLifeRevisionLocked(
        ClientSession session,
        PlayerStatusState state,
        long expectedLifeRevision,
        int kind)
    {
        if (!Monitor.IsEntered(_gate))
        {
            throw new SynchronizationLockException(
                "Runtime-status life cleanup requires registry authority.");
        }
        if (!_sessions.ContainsKey(session) ||
            !_playerLifeRevisions.TryGetValue(
                session,
                out var lifeRevision) ||
            lifeRevision != expectedLifeRevision ||
            !state.RuntimeStatuses.Remove(kind))
        {
            return false;
        }

        if (kind != MountCatalog.RuntimeStatusKind)
        {
            RefreshSkillCastControlSnapshot(state);
        }
        return true;
    }

    private async Task PublishRuntimeStatusRemovalAsync(
        ClientSession session,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!_playerStatusStates.TryGetValue(session, out var state))
        {
            return;
        }

        var admissionClaims = new ExactStatusDisconnectClaims();
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (!_sessions.ContainsKey(session))
                {
                    return;
                }
            }

            await PublishStatusSnapshotLockedAsync(
                session,
                state,
                now,
                reason,
                force: true,
                broadcast: true,
                cancellationToken,
                forceLocalGameDataSynchronization: true,
                claimedDisconnects: admissionClaims);
        }
        finally
        {
            state.Gate.Release();
            admissionClaims.CompleteAll(this);
        }
    }

    internal ClientStatusAggregate GetRuntimeStatusAggregate(
        ClientSession session,
        DateTimeOffset now)
    {
        if (!_playerStatusStates.TryGetValue(session, out var state))
        {
            return ClientStatusAggregate.Empty;
        }

        state.Gate.Wait();
        try
        {
            if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
            {
                lock (_gate)
                {
                    if (_sessions.TryGetValue(
                            session,
                            out var context))
                    {
                        return EvaluatePlayerStatusEcsLocked(
                                session,
                                state,
                                context,
                                now)
                            .Snapshot
                            .Aggregate;
                    }
                }
            }

            var active = state.RuntimeStatuses.Values
                .Where(status => status.ExpiresAt > now)
                .ToArray();
            return PlayerStatusComposer.Compose(
                    ExperienceBoostState.Empty,
                    active,
                    now)
                .Aggregate;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    internal decimal GetRuntimePhysicalDamageReduction(
        ClientSession session,
        DateTimeOffset now)
    {
        TryGetRuntimePhysicalDamageReduction(
            session,
            now,
            out var reduction);
        return reduction;
    }

    internal bool TryGetRuntimePhysicalDamageReduction(
        ClientSession session,
        DateTimeOffset now,
        out decimal reduction) =>
        TryGetRuntimePhysicalDamageReduction(
            session,
            now,
            out reduction,
            out _);

    internal bool TryGetRuntimePhysicalDamageReduction(
        ClientSession session,
        DateTimeOffset now,
        out decimal reduction,
        out int physicalDefenseBonus)
    {
        reduction = 0m;
        physicalDefenseBonus = 0;
        if (!_playerStatusStates.TryGetValue(session, out var state))
        {
            if (_playerRuntimeMode != PlayerRuntimeMode.Ecs)
            {
                return true;
            }

            lock (_gate)
            {
                return _sessions.ContainsKey(session);
            }
        }

        state.Gate.Wait();
        try
        {
            IEnumerable<ActiveRuntimeStatus> statuses;
            if (_playerRuntimeMode == PlayerRuntimeMode.Ecs)
            {
                RuntimeStatusSessionLookupHook?.Invoke();
                lock (_gate)
                {
                    if (!_sessions.TryGetValue(
                            session,
                            out var context))
                    {
                        return false;
                    }

                    statuses = EvaluatePlayerStatusEcsLocked(
                            session,
                            state,
                            context,
                            now)
                        .ActiveRuntimeStatuses;
                }
            }
            else
            {
                statuses = state.RuntimeStatuses.Values
                    .Where(status => status.ExpiresAt > now)
                    .ToArray();
            }

            reduction = Math.Clamp(
                statuses.Sum(
                    static status =>
                        status.PhysicalDamageReduction),
                0m,
                1m);
            physicalDefenseBonus = (int)Math.Clamp(
                statuses.Aggregate(
                    0L,
                    static (sum, status) =>
                        sum + status.Modifiers.PhysicalDefense),
                int.MinValue,
                int.MaxValue);
            return true;
        }
        finally
        {
            state.Gate.Release();
        }
    }

}
