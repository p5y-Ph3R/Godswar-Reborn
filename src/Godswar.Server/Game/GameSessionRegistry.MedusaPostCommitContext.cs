using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    /// <summary>
    /// Rebase a captured Medusa publication onto a routine character revision
    /// without accepting a join, rejoin, transfer, ownership, object, life,
    /// or character change. Exact packet admission still requires the newly
    /// captured context reference and its current WorldRevision.
    /// </summary>
    private bool TryRebaseMedusaPublicationContext(
        GameSessionContext expected,
        long expectedLifeRevision,
        out GameSessionContext current)
    {
        lock (_gate)
        {
            return TryResolveMedusaPublicationContextLocked(
                expected,
                expectedLifeRevision,
                out current);
        }
    }

    private bool TryResolveMedusaPublicationContextLocked(
        GameSessionContext expected,
        long expectedLifeRevision,
        out GameSessionContext current)
    {
        current = null!;
        return expected.WorldMembershipEpoch > 0 &&
            _sessions.TryGetValue(expected.Session, out current!) &&
            !current.Session.IsDisconnected &&
            current.WorldMembershipEpoch == expected.WorldMembershipEpoch &&
            ReferenceEquals(current.Session, expected.Session) &&
            current.AccountId == expected.AccountId &&
            current.CharacterId == expected.CharacterId &&
            current.RealmId == expected.RealmId &&
            current.WorldInstanceId == expected.WorldInstanceId &&
            current.MapId == expected.MapId &&
            current.ObjectId == expected.ObjectId &&
            current.WorldReady && expected.WorldReady &&
            current.WorldRevision >= expected.WorldRevision &&
            current.Ownership == expected.Ownership &&
            ReferenceEquals(current.Character, expected.Character) &&
            _playerLifeRevisions.TryGetValue(
                expected.Session,
                out var lifeRevision) &&
            lifeRevision == expectedLifeRevision;
    }

    private bool TryResolveExactMedusaPublicationContextLocked(
        WorldInstanceRuntime runtime,
        GameSessionContext expected,
        long expectedLifeRevision,
        out GameSessionContext current)
    {
        if (IsExactMonsterAttackPublicationContextLocked(
                runtime,
                expected,
                expectedLifeRevision))
        {
            current = expected;
            return true;
        }

        return TryResolveMedusaPublicationContextLocked(
                expected,
                expectedLifeRevision,
                out current) &&
            IsExactMonsterAttackPublicationContextLocked(
                runtime,
                current,
                expectedLifeRevision);
    }

    private bool TryCaptureCurrentMedusaPublicationContext(
        GameSessionContext expected,
        out GameSessionContext current,
        out long lifeRevision)
    {
        lock (_gate)
        {
            if (_playerLifeRevisions.TryGetValue(
                    expected.Session,
                    out lifeRevision) &&
                TryResolveMedusaPublicationContextLocked(
                    expected,
                    lifeRevision,
                    out current))
            {
                return true;
            }
        }

        current = null!;
        lifeRevision = -1;
        return false;
    }

    private bool TryCaptureCurrentMedusaPublicationTarget(
        GameSessionContext expected,
        out GameSessionContext current,
        out WorldInstanceRuntime runtime,
        out long lifeRevision)
    {
        lock (_gate)
        {
            if (_playerLifeRevisions.TryGetValue(
                    expected.Session,
                    out lifeRevision) &&
                TryResolveMedusaPublicationContextLocked(
                    expected,
                    lifeRevision,
                    out current) &&
                TryGetWorldInstance(current, out runtime!))
            {
                return true;
            }
        }

        current = null!;
        runtime = null!;
        lifeRevision = -1;
        return false;
    }

    private bool TryClaimExactMedusaMembershipDisconnect(
        GameSessionContext expected,
        long expectedLifeRevision,
        out ClientSession session)
    {
        lock (_gate)
        {
            if (TryResolveMedusaPublicationContextLocked(
                    expected,
                    expectedLifeRevision,
                    out var current) &&
                current.Session.TryClaimDisconnect())
            {
                session = current.Session;
                return true;
            }
        }

        session = null!;
        return false;
    }

    private bool TryClaimExactMedusaPublicationPairDisconnect(
        GameSessionContext expectedTarget,
        long expectedTargetLifeRevision,
        GameSessionContext expectedRecipient,
        long expectedRecipientLifeRevision,
        out ClientSession session)
    {
        lock (_gate)
        {
            if (TryResolveMedusaPublicationContextLocked(
                    expectedTarget,
                    expectedTargetLifeRevision,
                    out _) &&
                TryResolveMedusaPublicationContextLocked(
                    expectedRecipient,
                    expectedRecipientLifeRevision,
                    out var currentRecipient) &&
                currentRecipient.Session.TryClaimDisconnect())
            {
                session = currentRecipient.Session;
                return true;
            }
        }

        session = null!;
        return false;
    }

    private GameSessionContext? RebaseMedusaPostCommitContext(
        MedusaMonsterPlayerHitCommitOutcome? medusaOutcome,
        GameSessionContext? captured,
        long expectedLifeRevision)
    {
        if (medusaOutcome is null || captured is null)
        {
            return captured;
        }

        return TryRebaseMedusaPublicationContext(
            captured,
            expectedLifeRevision,
            out var current)
                ? current
                : captured;
    }
}
