using Godswar.Server.Application.WorldInstances;
using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
#if DEBUG
    // ProtocolChecks may inject owner-mailbox failures by reflection. Release
    // builds expose no callable seam or alternate authority result.
    private Exception? _protocolCheckMedusaStatusAuthorityFailure;
#endif

    internal bool IsMedusaActionAllowed(
        ClientSession session,
        MedusaEncounterControlRestriction action,
        DateTimeOffset observedAt,
        out MedusaCharacterEffectAuthorityResult authority)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (action == MedusaEncounterControlRestriction.None ||
            (action & ~MedusaEncounterControlRestriction.AllActions) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        authority = ResolveMedusaCharacterEffectAuthority(
            session,
            observedAt);
        return authority.Allows(action);
    }

    internal MedusaCharacterEffectAuthorityResult
        ResolveMedusaCharacterEffectAuthority(
            ClientSession session,
            DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (!_sessions.TryGetValue(session, out var context) ||
                !IsMedusaContentMap(context.MapId))
            {
                return UnboundCharacterEffects();
            }
            if (!TryGetWorldInstance(context, out var runtime))
            {
                return MissingCharacterEffectMembership();
            }

            var hasLife = _playerLifeRevisions.TryGetValue(
                session,
                out var lifeRevision);
            var registryAuthorityCurrent =
                context.Ownership.IsValid &&
                IsCurrentAccountSession(
                    context.AccountId,
                    session,
                    context.Ownership);
            try
            {
#if DEBUG
                if (Interlocked.Exchange(
                        ref _protocolCheckMedusaStatusAuthorityFailure,
                        null) is { } injectedFailure)
                {
                    throw injectedFailure;
                }
#endif
                return InvokeWorldOwner(
                    runtime,
                    map => map
                        .ResolveMedusaCharacterEffectsForSessionGuarded(
                            context,
                            hasLife ? lifeRevision : -1,
                            registryAuthorityCurrent,
                            observedAt));
            }
            catch (Exception error) when (
                error is InvalidOperationException or
                    ObjectDisposedException or
                    TimeoutException or
                    SingleOwnerMailboxAdmissionException or
                    SingleOwnerMailboxStoppedException or
                    SingleOwnerMailboxWorkerException)
            {
                return new(
                    MedusaCharacterEffectAuthorityOutcome
                        .BoundAuthorityUnavailable,
                    View: null);
            }
        }
    }

    private void ClearBoundMedusaEffectsForExpiredLifeLocked(
        GameSessionContext context,
        DateTimeOffset lifeAdvancedAt)
    {
        // Bounded owner checkpoint only: a due old-life capability remains
        // owner-held and reacquirable here. Live Bleed integration must drain
        // and retry before advancing the life revision, must not discard a
        // Reserved/InvariantFault disposition, and must propagate a TimedOut
        // owner result through the prepared terminal-status roster.
        if (!IsMedusaContentMap(context.MapId) ||
            !_playerLifeRevisions.TryGetValue(
                context.Session,
                out var currentLifeRevision) ||
            currentLifeRevision <= 0 ||
            !TryGetWorldInstance(context, out var runtime))
        {
            return;
        }

        try
        {
            InvokeWorldOwner(
                runtime,
                map => map.ClearMedusaCharacterEffectsForLifeGuarded(
                    context,
                    currentLifeRevision - 1,
                    lifeAdvancedAt));
        }
        catch (Exception error) when (
            error is InvalidOperationException or
                ObjectDisposedException or
                TimeoutException or
                SingleOwnerMailboxAdmissionException or
                SingleOwnerMailboxStoppedException or
                SingleOwnerMailboxWorkerException)
        {
            // Exact-life command views still exclude the expired life. This
            // cleanup is bounded state reclamation after HP is irreversible.
        }
    }

    private static bool IsMedusaContentMap(byte mapId) =>
        mapId is 200 or 204;

    private static MedusaCharacterEffectAuthorityResult
        UnboundCharacterEffects() => new(
            MedusaCharacterEffectAuthorityOutcome.Unbound,
            View: null);

    private static MedusaCharacterEffectAuthorityResult
        MissingCharacterEffectMembership() => new(
            MedusaCharacterEffectAuthorityOutcome
                .CurrentMembershipRequired,
            View: null);
}
