using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private MedusaClientStatusOverlay CaptureMedusaClientStatusOverlay(
        GameSessionContext expected,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(
                    expected.Session,
                    out var current) ||
                !ReferenceEquals(current, expected) ||
                !current.WorldReady ||
                current.WorldInstanceId != expected.WorldInstanceId ||
                current.WorldRevision != expected.WorldRevision ||
                current.Ownership != expected.Ownership ||
                current.ObjectId != expected.ObjectId ||
                current.CharacterId != expected.CharacterId ||
                !ReferenceEquals(
                    current.Character,
                    expected.Character))
            {
                return UnavailableMedusaClientStatus(
                    MedusaCharacterEffectAuthorityOutcome
                        .CurrentMembershipRequired);
            }
            if (!IsMedusaContentMap(current.MapId))
            {
                return MedusaClientStatusOverlay.Unbound;
            }
            if (!_playerLifeRevisions.TryGetValue(
                    current.Session,
                    out var lifeRevision))
            {
                return UnavailableMedusaClientStatus(
                    MedusaCharacterEffectAuthorityOutcome
                        .CurrentMembershipRequired);
            }

            var target = new MedusaClientStatusTargetFence(
                current.WorldInstanceId,
                current.WorldRevision,
                current.Ownership,
                current.CharacterId,
                current.ObjectId,
                lifeRevision,
                current.WorldMembershipEpoch);
            var authority = ResolveMedusaCharacterEffectAuthority(
                current.Session,
                now);
            return MedusaClientStatusProjection.Create(
                target,
                authority,
                now);
        }
    }

    private bool MatchesMedusaClientStatusOverlay(
        GameSessionContext expected,
        MedusaClientStatusOverlay expectedOverlay,
        DateTimeOffset now) =>
        MatchesMedusaClientStatusOverlay(
            expected,
            expectedOverlay,
            now,
            out _);

    private bool MatchesMedusaClientStatusOverlay(
        GameSessionContext expected,
        MedusaClientStatusOverlay expectedOverlay,
        DateTimeOffset now,
        out MedusaCharacterEffectAuthorityOutcome currentOutcome)
    {
        var current = CaptureMedusaClientStatusOverlay(expected, now);
        currentOutcome = current.AuthorityOutcome;
        if (!expectedOverlay.IsBound)
        {
            return current.AuthorityOutcome ==
                MedusaCharacterEffectAuthorityOutcome.Unbound;
        }

        return current.CanPublish &&
            string.Equals(
                current.Fingerprint,
                expectedOverlay.Fingerprint,
                StringComparison.Ordinal);
    }

    private static MedusaClientStatusOverlay
        UnavailableMedusaClientStatus(
            MedusaCharacterEffectAuthorityOutcome outcome) => new(
                outcome,
                Target: null,
                RunDeadline: null,
                Presentations: [],
                $"medusa-client:{(byte)outcome}:unavailable");
}
