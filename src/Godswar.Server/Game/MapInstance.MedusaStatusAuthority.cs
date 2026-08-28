using Godswar.Server.Application.Characters;
using Godswar.Server.Domain.World.Instances;
using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private sealed partial class MedusaInstanceOwnerBoundAggregate
    {
        public MedusaCharacterEffectAuthorityResult
            ResolveCharacterEffects(
                int characterId,
                PlayerOwnershipFence ownership,
                long lifeRevision,
                long worldMembershipEpoch,
                DateTimeOffset observedAt)
        {
            if (!_run.IsCharacterAdmitted(characterId) ||
                !_mechanics.TryGetActiveCharacterEffectView(
                    characterId,
                    ownership,
                    lifeRevision,
                    worldMembershipEpoch,
                    observedAt,
                    out var view))
            {
                return UnavailableCharacterEffects();
            }

            return _run.PreviewTime(view.EvaluatedAt) switch
            {
                MedusaRunClockOutcome.Active => new(
                    MedusaCharacterEffectAuthorityOutcome.ResolvedActive,
                    view),
                MedusaRunClockOutcome.RunNotActive or
                MedusaRunClockOutcome.TimedOut => new(
                    MedusaCharacterEffectAuthorityOutcome.RunNotActive,
                    View: null),
                _ => UnavailableCharacterEffects()
            };
        }

        private static MedusaCharacterEffectAuthorityResult
            UnavailableCharacterEffects() => new(
                MedusaCharacterEffectAuthorityOutcome
                    .BoundAuthorityUnavailable,
                View: null);
    }

    internal MedusaCharacterEffectAuthorityResult
        ResolveMedusaCharacterEffectsForSessionGuarded(
            GameSessionContext expectedContext,
            long expectedLifeRevision,
            bool registryAuthorityCurrent,
            DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(expectedContext);

        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is null &&
                _medusaMonsterAttachment is null)
            {
                return new(
                    MedusaCharacterEffectAuthorityOutcome.Unbound,
                    View: null);
            }

            lock (_descriptorGate)
            {
                lock (_membershipGate)
                {
                    lock (_monsterRuntimeGate)
                    {
                        return ResolveBoundCharacterEffectsLocked(
                            expectedContext,
                            expectedLifeRevision,
                            registryAuthorityCurrent,
                            observedAt);
                    }
                }
            }
        }
    }

    internal bool ClearMedusaCharacterEffectsForLifeGuarded(
        GameSessionContext expectedContext,
        long expectedLifeRevision,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(expectedContext);
        if (expectedLifeRevision < 0)
        {
            return false;
        }

        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is not { } owner)
            {
                return false;
            }

            lock (_membershipGate)
            {
                if (!IsExactCurrentMedusaMembership(expectedContext))
                {
                    return false;
                }

                var periodic = owner.ClearMonsterPlayerEffectsForLife(
                    expectedContext.CharacterId,
                    expectedContext.Ownership,
                    expectedLifeRevision,
                    expectedContext.WorldMembershipEpoch,
                    observedAt);
                return periodic.Outcome ==
                    MedusaPeriodicDamageReserveOutcome.NoneDue;
            }
        }
    }

    private MedusaCharacterEffectAuthorityResult
        ResolveBoundCharacterEffectsLocked(
            GameSessionContext expectedContext,
            long expectedLifeRevision,
            bool registryAuthorityCurrent,
            DateTimeOffset observedAt)
    {
        var owner = _medusaInstanceOwner;
        var attachment = _medusaMonsterAttachment;
        if (owner is null ||
            attachment is null ||
            !HasCompleteMedusaDamageState(owner) ||
            _monsterRuntimeMode != MonsterRuntimeMode.Ecs ||
            _playerRuntimeMode != PlayerRuntimeMode.Ecs)
        {
            return new(
                MedusaCharacterEffectAuthorityOutcome
                    .BoundAuthorityUnavailable,
                View: null);
        }
        if (expectedLifeRevision < 0 ||
            !registryAuthorityCurrent ||
            !IsExactCurrentMedusaMembership(expectedContext))
        {
            return new(
                MedusaCharacterEffectAuthorityOutcome
                    .CurrentMembershipRequired,
                View: null);
        }
        if (_descriptor.LifecycleState !=
            WorldInstanceLifecycleState.Active)
        {
            return new(
                MedusaCharacterEffectAuthorityOutcome.RunNotActive,
                View: null);
        }

        return owner.ResolveCharacterEffects(
            expectedContext.CharacterId,
            expectedContext.Ownership,
            expectedLifeRevision,
            expectedContext.WorldMembershipEpoch,
            observedAt);
    }

    private bool IsExactCurrentMedusaMembership(
        GameSessionContext expectedContext) =>
        expectedContext.Session is not null &&
        expectedContext.Ownership.IsValid &&
        expectedContext.WorldInstanceId == WorldInstanceId &&
        expectedContext.RealmId == _descriptor.RealmId &&
        expectedContext.MapId == MapId &&
        expectedContext.CharacterId == expectedContext.Character.Id &&
        expectedContext.Character.CurrentMap == MapId &&
        expectedContext.WorldReady &&
        _sessions.TryGetValue(
            expectedContext.Session,
            out var current) &&
        ReferenceEquals(current, expectedContext) &&
        ReferenceEquals(current.Character, expectedContext.Character) &&
        current.ObjectId == expectedContext.ObjectId &&
        _ecsShadow.ContainsPlayer(expectedContext.Session);
}
