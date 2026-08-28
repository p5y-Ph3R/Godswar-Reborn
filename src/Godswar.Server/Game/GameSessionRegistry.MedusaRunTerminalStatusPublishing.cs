using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
#if DEBUG
    private Action? _protocolCheckBeforeMedusaTerminalSchedule = null;
    private Action<int>?
        _protocolCheckBeforeMedusaTerminalMemberPublication = null;
#endif

    private bool TryPrepareMedusaRunTerminalClearLocked(
        WorldInstanceId instanceId,
        out MedusaRunTerminalClearWorkItem workItem)
    {
        const int maximumMembers = 5;
        var members = new MonsterAttackPublicationRecipient[maximumMembers];
        var count = 0;
        foreach (var context in _sessions.Values)
        {
            if (!context.WorldReady ||
                context.WorldInstanceId != instanceId)
            {
                continue;
            }
            if (count == members.Length)
            {
                workItem = null!;
                return false;
            }
            if (!_playerLifeRevisions.TryGetValue(
                    context.Session,
                    out var lifeRevision))
            {
                workItem = null!;
                return false;
            }
            members[count++] = new(context, lifeRevision);
        }
        workItem = new(this, instanceId, members, count);
        return true;
    }

    private sealed class MedusaRunTerminalClearWorkItem :
        IThreadPoolWorkItem
    {
        private readonly GameSessionRegistry _registry;
        private readonly MonsterAttackPublicationRecipient[] _members;
        private readonly int _memberCount;
        private DateTimeOffset _completedAt;

        internal MedusaRunTerminalClearWorkItem(
            GameSessionRegistry registry,
            WorldInstanceId instanceId,
            MonsterAttackPublicationRecipient[] members,
            int memberCount)
        {
            _registry = registry;
            InstanceId = instanceId;
            _members = members;
            _memberCount = memberCount;
        }

        internal WorldInstanceId InstanceId { get; }

        internal void ScheduleNonThrowing(DateTimeOffset completedAt)
        {
            _completedAt = completedAt;
            try
            {
#if DEBUG
                _registry._protocolCheckBeforeMedusaTerminalSchedule?
                    .Invoke();
#endif
                if (ThreadPool.UnsafeQueueUserWorkItem(
                        this,
                        preferLocal: false))
                {
                    return;
                }
            }
            catch
            {
            }
            FailClosedPreparedMembersNonThrowing();
        }

        public void Execute()
        {
            for (var index = 0; index < _memberCount; index++)
            {
                var member = _members[index];
                try
                {
#if DEBUG
                    _registry
                        ._protocolCheckBeforeMedusaTerminalMemberPublication?
                        .Invoke(index);
#endif
                    _registry.PublishMedusaRunTerminalStatusClearAsync(
                            member.Context,
                            member.LifeRevision,
                            _completedAt)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception error)
                {
                    _registry
                        .FailClosedPreparedMedusaRunTerminalMember(
                            member);
                    try
                    {
                        Console.WriteLine(
                            "[medusa-status] run-terminal clear failed " +
                            $"target={member.Context.DisplayName}: " +
                            error.Message);
                    }
                    catch
                    {
                    }
                }
            }
        }

        internal void FailClosedPreparedMembersNonThrowing()
        {
            for (var index = 0; index < _memberCount; index++)
            {
                _registry.FailClosedPreparedMedusaRunTerminalMember(
                    _members[index]);
            }
        }
    }

    private void FailClosedPreparedMedusaRunTerminalMember(
        in MonsterAttackPublicationRecipient member)
    {
        ClientSession? claimed = null;
        try
        {
            _ = TryClaimExactMedusaMembershipDisconnect(
                member.Context,
                member.LifeRevision,
                out claimed!);
        }
        catch
        {
        }
        if (claimed is not null)
        {
            try
            {
                CompleteClaimedExactStatusDisconnect(claimed);
            }
            catch
            {
            }
        }
    }

    private async Task PublishMedusaRunTerminalStatusClearAsync(
        GameSessionContext target,
        long targetLifeRevision,
        DateTimeOffset completedAt)
    {
        if (!TryGetOrCreatePlayerStatusState(
                target.Session,
                out var state))
        {
            // A removed/transferred target is benign; the exact recapture in
            // this helper suppresses it. A still-current target without a
            // status state is an invariant fault and is failed closed.
            FailClosedClaimedMedusaStatusPublication(
                target,
                targetLifeRevision);
            return;
        }

        var failCloseAfterGate = false;
        var admissionClaims = new ExactStatusDisconnectClaims();
        await state.Gate.WaitAsync(CancellationToken.None);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!TryRebaseMedusaPublicationContext(
                        target,
                        targetLifeRevision,
                        out target))
                {
                    return;
                }
                var projectionAt = CurrentMedusaProjectionTime(
                    completedAt);
                var overlay = CaptureMedusaClientStatusOverlay(
                    target,
                    projectionAt);
                if (!overlay.CanPublish)
                {
                    if (overlay.AuthorityOutcome ==
                            MedusaCharacterEffectAuthorityOutcome
                                .CurrentMembershipRequired &&
                        TryRebaseMedusaPublicationContext(
                            target,
                            targetLifeRevision,
                            out target))
                    {
                        continue;
                    }
                    failCloseAfterGate =
                        RequiresFailClosedMedusaProjection(
                            overlay,
                            CancellationToken.None);
                    return;
                }

                var publicationOutcome = overlay.AuthorityOutcome;
                if (await PublishStatusSnapshotLockedAsync(
                        target.Session,
                        state,
                        projectionAt,
                        "medusa-status-run-terminal",
                        force: true,
                        broadcast: true,
                        CancellationToken.None,
                        medusaAuthorityObserved: outcome =>
                            publicationOutcome = outcome,
                        claimedDisconnects: admissionClaims))
                {
                    return;
                }
                if (publicationOutcome is
                    MedusaCharacterEffectAuthorityOutcome
                        .BoundAuthorityUnavailable or
                    MedusaCharacterEffectAuthorityOutcome
                        .CurrentMembershipRequired)
                {
                    failCloseAfterGate =
                        RequiresFailClosedMedusaProjection(
                            publicationOutcome,
                            CancellationToken.None);
                    return;
                }

                // A concurrent baseline mutation may supersede the complete
                // snapshot. Recompose exactly once from current state.
            }

            var current = CaptureMedusaClientStatusOverlay(
                target,
                CurrentMedusaProjectionTime(completedAt));
            failCloseAfterGate =
                current.AuthorityOutcome ==
                    MedusaCharacterEffectAuthorityOutcome
                        .CurrentMembershipRequired ||
                RequiresFailClosedMedusaProjection(
                    current,
                    CancellationToken.None,
                    disconnectIfPublishable: true);
        }
        finally
        {
            state.Gate.Release();
            admissionClaims.CompleteAll(this);
            if (failCloseAfterGate)
            {
                FailClosedClaimedMedusaStatusPublication(
                    target,
                    targetLifeRevision);
            }
        }
    }
}
