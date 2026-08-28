using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    internal Task<bool>
        DeliverMonsterCastStartToViewerAdmissionAsync(
            ClientSession session,
            byte mapId,
            uint monsterId,
            ReadOnlyMemory<byte> packet,
            uint expectedSpawnGeneration,
            CancellationToken cancellationToken,
            string label)
    {
        if (!TryCaptureMonsterCastStartSource(
                session,
                mapId,
                out var runtime,
                out var source,
                out var sourceLife))
        {
            return Task.FromResult(false);
        }

        var outcome = TryAdmitMonsterCastStartExact(
            runtime,
            source,
            sourceLife,
            source,
            sourceLife,
            monsterId,
            expectedSpawnGeneration,
            [packet],
            cancellationToken,
            out var claimedDisconnect,
            out var completion);

        if (claimedDisconnect is not null)
        {
            CompleteClaimedExactStatusDisconnect(claimedDisconnect);
        }
        if (!WasMonsterCastStartOwned(outcome))
        {
            return Task.FromResult(false);
        }

        ObserveExactAdmissionCompletion(session, completion, label);
        return Task.FromResult(true);
    }

    internal Task<int>
        BroadcastMonsterCastStartToViewersAdmissionAsync(
            ClientSession sourceSession,
            byte mapId,
            uint monsterId,
            ReadOnlyMemory<byte> packet,
            uint expectedSpawnGeneration,
            CancellationToken cancellationToken,
            string label)
    {
        if (!TryCaptureMonsterCastStartSource(
                sourceSession,
                mapId,
                out var runtime,
                out var source,
                out var sourceLife))
        {
            return Task.FromResult(0);
        }

        var recipients = CaptureMonsterAttackPublicationRecipients(
            runtime,
            InvokeWorldOwner(
                runtime,
                static map => map.Snapshot(),
                cancellationToken));
        var admitted = 0;
        foreach (var recipient in recipients)
        {
            if (ReferenceEquals(
                    recipient.Context.Session,
                    sourceSession))
            {
                continue;
            }

            try
            {
                var outcome = TryAdmitMonsterCastStartExact(
                    runtime,
                    recipient.Context,
                    recipient.LifeRevision,
                    source,
                    sourceLife,
                    monsterId,
                    expectedSpawnGeneration,
                    [packet],
                    cancellationToken,
                    out var claimedDisconnect,
                    out var completion);

                if (claimedDisconnect is not null)
                {
                    CompleteClaimedExactStatusDisconnect(
                        claimedDisconnect);
                }

                if (WasMonsterCastStartOwned(outcome))
                {
                    ObserveExactAdmissionCompletion(
                        recipient.Context.Session,
                        completion,
                        label);
                    admitted++;
                }
            }
            catch (Exception error) when (
                error is not OperationCanceledException)
            {
                if (TryClaimExactMedusaPublicationPairDisconnect(
                        source,
                        sourceLife,
                        recipient.Context,
                        recipient.LifeRevision,
                        out var claimedRecipient))
                {
                    CompleteClaimedExactStatusDisconnect(
                        claimedRecipient);
                }
            }
        }

        return Task.FromResult(admitted);
    }

    private MonsterCastStartAdmissionOutcome
        TryAdmitMonsterCastStartExact(
            WorldInstanceRuntime runtime,
            GameSessionContext recipient,
            long recipientLifeRevision,
            GameSessionContext source,
            long sourceLifeRevision,
            uint monsterId,
            uint expectedSpawnGeneration,
            IReadOnlyList<ReadOnlyMemory<byte>> packets,
            CancellationToken cancellationToken,
            out ClientSession? claimedDisconnect,
            out Task completion)
    {
        claimedDisconnect = null;
        completion = Task.CompletedTask;
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return MonsterCastStartAdmissionOutcome.Canceled;
            }
            if (!TryResolveExactMedusaPublicationContextLocked(
                    runtime,
                    recipient,
                    recipientLifeRevision,
                    out recipient) ||
                !TryResolveExactMedusaPublicationContextLocked(
                    runtime,
                    source,
                    sourceLifeRevision,
                    out source))
            {
                return MonsterCastStartAdmissionOutcome.MembershipStale;
            }

            var leaseOutcome = runtime.Map
                .TryAcquireMonsterCastStartDeliveryLease(
                    recipient.Session,
                    monsterId,
                    expectedSpawnGeneration,
                    out var lease);
            if (leaseOutcome != MonsterCastStartLeaseOutcome.Acquired ||
                lease is null)
            {
                if (leaseOutcome == MonsterCastStartLeaseOutcome.Busy &&
                    recipient.Session.TryClaimDisconnect())
                {
                    claimedDisconnect = recipient.Session;
                }
                return MonsterCastStartAdmissionOutcome.MembershipStale;
            }

            try
            {
                var outcome = runtime.Map.TryAdmitMonsterCastStart(
                    recipient,
                    source,
                    packets,
                    cancellationToken,
                    out completion);
                // This lease is a visibility-transition fence only; cast
                // start supplies no health/reconciliation mutations. Avoid
                // the general lease Commit allocation after egress owns the
                // packet batch.
                if (outcome ==
                        MonsterCastStartAdmissionOutcome.AdmittedTerminal &&
                    recipient.Session.TryClaimDisconnect())
                {
                    claimedDisconnect = recipient.Session;
                }
                else if (outcome ==
                         MonsterCastStartAdmissionOutcome.AdmissionFailed &&
                         recipient.Session.TryClaimDisconnect())
                {
                    claimedDisconnect = recipient.Session;
                }
                return outcome;
            }
            finally
            {
                lease.Release();
            }
        }
    }

    private static bool WasMonsterCastStartOwned(
        MonsterCastStartAdmissionOutcome outcome) => outcome is
        MonsterCastStartAdmissionOutcome.Admitted or
        MonsterCastStartAdmissionOutcome.AdmittedTerminal;

    private bool TryCaptureMonsterCastStartSource(
        ClientSession session,
        byte mapId,
        out WorldInstanceRuntime runtime,
        out GameSessionContext source,
        out long lifeRevision)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(session, out source!) &&
                !session.IsDisconnected &&
                source.WorldReady &&
                source.MapId == mapId &&
                TryGetWorldInstance(source, out runtime!) &&
                _playerLifeRevisions.TryGetValue(
                    session,
                    out lifeRevision))
            {
                return true;
            }
        }

        runtime = null!;
        source = null!;
        lifeRevision = -1;
        return false;
    }

}
