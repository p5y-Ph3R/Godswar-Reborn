using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    internal MonsterCastStartLeaseOutcome
        TryAcquireMonsterCastStartDeliveryLease(
            ClientSession session,
            uint objectId,
            uint expectedSpawnGeneration,
            out MonsterViewerDeliveryLease? lease)
    {
        lease = null;
        if (!_monsterViewers.TryGetValue(session, out var viewer))
        {
            return MonsterCastStartLeaseOutcome.Stale;
        }
        if (!viewer.TransitionGate.Wait(0))
        {
            return MonsterCastStartLeaseOutcome.Busy;
        }

        if (!_monsterViewers.TryGetValue(session, out var currentViewer) ||
            !ReferenceEquals(currentViewer, viewer) ||
            !ContainsPlayer(session) ||
            !viewer.VisibleMonsterVersions.TryGetValue(
                objectId,
                out var visibleVersion) ||
            visibleVersion.SpawnGeneration != expectedSpawnGeneration)
        {
            viewer.TransitionGate.Release();
            return MonsterCastStartLeaseOutcome.Stale;
        }

        lease = new MonsterViewerDeliveryLease(viewer, [], [], [], []);
        return MonsterCastStartLeaseOutcome.Acquired;
    }

    internal MonsterCastStartAdmissionOutcome
        TryAdmitMonsterCastStart(
            GameSessionContext expectedRecipient,
            GameSessionContext expectedSource,
            IReadOnlyList<ReadOnlyMemory<byte>> packets,
            CancellationToken cancellationToken,
            out Task completion)
    {
        completion = Task.CompletedTask;
        lock (_membershipGate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return MonsterCastStartAdmissionOutcome.Canceled;
            }
            if (!MatchesMonsterCastStartMembership(
                    expectedRecipient) ||
                !MatchesMonsterCastStartMembership(expectedSource))
            {
                return MonsterCastStartAdmissionOutcome
                    .MembershipStale;
            }

            return expectedRecipient.Session.TryAdmitExactBatchOutcome(
                    packets,
                    out completion) switch
                {
                    ExactEgressAdmissionOutcome.Admitted =>
                        MonsterCastStartAdmissionOutcome.Admitted,
                    ExactEgressAdmissionOutcome.AdmittedTerminal =>
                        MonsterCastStartAdmissionOutcome
                            .AdmittedTerminal,
                    _ => MonsterCastStartAdmissionOutcome.AdmissionFailed
                };
        }
    }

    private bool MatchesMonsterCastStartMembership(
        GameSessionContext expected) =>
        expected.WorldMembershipEpoch > 0 &&
        _sessions.TryGetValue(expected.Session, out var current) &&
        !current.Session.IsDisconnected &&
        current.WorldReady &&
        current.WorldMembershipEpoch == expected.WorldMembershipEpoch &&
        current.WorldRevision >= expected.WorldRevision &&
        current.WorldInstanceId == expected.WorldInstanceId &&
        current.MapId == expected.MapId &&
        current.ObjectId == expected.ObjectId &&
        current.Ownership == expected.Ownership &&
        current.CharacterId == expected.CharacterId &&
        ReferenceEquals(current.Character, expected.Character);
}

internal enum MonsterCastStartAdmissionOutcome
{
    Admitted,
    AdmittedTerminal,
    Canceled,
    MembershipStale,
    AdmissionFailed
}

internal enum MonsterCastStartLeaseOutcome
{
    Acquired,
    Busy,
    Stale
}
