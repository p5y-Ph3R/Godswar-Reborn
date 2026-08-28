using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;
using Godswar.Server.World.Systems.Combat;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    internal MedusaMonsterPlayerHitCapture
        CaptureMedusaMonsterPlayerHitForSessionGuarded(
            ClientSession session,
            GameCharacter expectedCharacter,
            MonsterRuntimeSnapshot eventSource,
            ulong attackEventId,
            in PlayerMonsterCombatAuthority route,
            in MedusaMonsterPlayerTargetAuthority target,
            DateTimeOffset committedAt,
            in MonsterCombatProfile baseProfile)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(expectedCharacter);
        ArgumentNullException.ThrowIfNull(eventSource);

        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is null &&
                _medusaMonsterAttachment is null)
            {
                return new(
                    MedusaMonsterPlayerHitCaptureOutcome.Unbound,
                    baseProfile,
                    SourceAuthority: null,
                    target,
                    AuthoredEffectKind: null);
            }

            lock (_descriptorGate)
            {
                lock (_membershipGate)
                {
                    lock (_monsterRuntimeGate)
                    {
                        return CaptureBoundMonsterPlayerHitLocked(
                            session,
                            expectedCharacter,
                            eventSource,
                            attackEventId,
                            route,
                            target,
                            committedAt,
                            baseProfile);
                    }
                }
            }
        }
    }

    internal MedusaMonsterPlayerHitCommit
        CommitMedusaMonsterPlayerHitForSessionGuarded(
            ClientSession session,
            GameCharacter expectedCharacter,
            in MedusaMonsterPlayerSourceAuthority source,
            in MedusaMonsterPlayerTargetAuthority target,
            MedusaCapturedPlayerVitalsCommit commitVitals,
            MedusaCapturedEffectInterruption? effectInterruption)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(expectedCharacter);
        ArgumentNullException.ThrowIfNull(commitVitals);

        lock (_medusaOwnershipGate)
        {
            lock (_descriptorGate)
            {
                lock (_membershipGate)
                {
                    lock (_monsterRuntimeGate)
                    {
                        lock (expectedCharacter.VitalsSync)
                        {
                            return CommitBoundMonsterPlayerHitLocked(
                                session,
                                expectedCharacter,
                                source,
                                target,
                                commitVitals,
                                effectInterruption);
                        }
                    }
                }
            }
        }
    }

    internal bool HasBoundMedusaEncounter()
    {
        lock (_medusaOwnershipGate)
        {
            return _medusaInstanceOwner is not null ||
                   _medusaMonsterAttachment is not null;
        }
    }
}
