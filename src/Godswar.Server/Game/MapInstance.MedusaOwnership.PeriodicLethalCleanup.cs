using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private sealed partial class MedusaInstanceOwnerBoundAggregate
    {
        public bool FinalizePeriodicDamageLethalCleanup(
            MedusaPreparedPeriodicDamageOwnerReceipt? receipt,
            IMonsterMapRuntime monsterRuntime)
        {
            if (receipt is not PreparedPeriodicDamageOwnerReceipt exact ||
                !ReferenceEquals(exact.Owner, this) ||
                exact.RequestedIntent !=
                    MedusaPeriodicDamageOwnerIntent.Terminal ||
                exact.ActualDisposition !=
                    MedusaPeriodicDamageDispositionOutcome.Terminal)
            {
                return false;
            }
            if (exact.LethalCleanupCompleted)
            {
                return true;
            }
            if (!HasCoupledClockScalars() ||
                _run.OwnerLastObservedAt != exact.Identity.DueAt)
            {
                return false;
            }

            _mechanics.ClearCharacterLifeAtCurrentClock(
                exact.Identity.TargetCharacterId,
                exact.Identity.TargetOwnership,
                exact.Identity.TargetLifeRevision,
                exact.Identity.TargetWorldMembershipEpoch);
            monsterRuntime.ClearAggroForCharacterStateOnly(
                exact.Identity.TargetCharacterId,
                exact.Identity.DueAt);
            exact.LethalCleanupCompleted = true;
            return true;
        }
    }

    internal bool TryFinalizeMedusaPeriodicDamageLethalCleanup(
        MedusaPreparedPeriodicDamageOwnerReceipt? receipt)
    {
        lock (_medusaOwnershipGate)
        {
            return _medusaInstanceOwner is { } owner &&
                _monsterRuntime is { } monsterRuntime &&
                owner.FinalizePeriodicDamageLethalCleanup(
                    receipt,
                    monsterRuntime);
        }
    }
}
