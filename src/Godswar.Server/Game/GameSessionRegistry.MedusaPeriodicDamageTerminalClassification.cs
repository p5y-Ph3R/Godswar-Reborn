using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private sealed class PeriodicTerminalClassification
        : MedusaPeriodicDamageTerminalClassification
    {
        internal PeriodicTerminalClassification(
            in MedusaPeriodicDamageIdentity identity,
            in MedusaPeriodicDamageTargetCapture target,
            MedusaPeriodicDamageTerminalWithoutHpReason reason)
        {
            Identity = identity;
            Target = target;
            Reason = reason;
        }

        private MedusaPeriodicDamageIdentity Identity { get; }

        private MedusaPeriodicDamageTargetCapture Target { get; }

        internal override MedusaPeriodicDamageTerminalWithoutHpReason Reason
            { get; }

        internal override bool Matches(
            in MedusaPeriodicDamageIdentity identity,
            in MedusaPeriodicDamageTargetCapture target) =>
            Identity == identity && Target == target;
    }

    /// <summary>
    /// Classifies and mints terminal authority without releasing the registry
    /// target gate between current-state proof and ledger installation.
    /// A raw reason enum is never accepted as authority.
    /// </summary>
    internal MedusaPeriodicDamageLedgerMutationOutcome
        TryCreateClassifiedMedusaPeriodicDamageTerminalWithoutHpAuthority(
            MedusaPeriodicDamageLedgerHandle? handle,
            out MedusaPeriodicDamageTerminalWithoutHpAuthority? authority)
    {
        authority = null;
        if (handle is null)
        {
            return MedusaPeriodicDamageLedgerMutationOutcome.Invalid;
        }

        lock (_gate)
        {
            if (!_medusaPeriodicDamageLedger.TryGetRetained(
                    handle.Identity.WorldInstanceId,
                    out var retained,
                    out var snapshot) ||
                !ReferenceEquals(retained, handle) ||
                snapshot.Phase != MedusaPeriodicDamageLedgerPhase.Prepared ||
                !TryClassifyPeriodicTerminalLocked(
                    snapshot.Target,
                    out var reason))
            {
                return MedusaPeriodicDamageLedgerMutationOutcome.Invalid;
            }

            var classification = new PeriodicTerminalClassification(
                snapshot.Identity,
                snapshot.Target,
                reason);
            return _medusaPeriodicDamageLedger
                .TryCreateTerminalWithoutHpAuthority(
                    handle,
                    classification,
                    out authority);
        }
    }

    private bool TryClassifyPeriodicTerminalLocked(
        in MedusaPeriodicDamageTargetCapture target,
        out MedusaPeriodicDamageTerminalWithoutHpReason reason)
    {
        GameSessionContext? current = null;
        foreach (var candidate in _sessions.Values)
        {
            if (candidate.CharacterId == target.Authority.CharacterId &&
                candidate.Ownership == target.Authority.Ownership)
            {
                current = candidate;
                break;
            }
        }

        if (current is null ||
            current.Session.IsDisconnected ||
            !current.WorldReady ||
            current.WorldInstanceId != target.Authority.WorldInstanceId ||
            current.WorldRevision != target.Authority.WorldRevision ||
            current.ObjectId != target.Authority.ObjectId ||
            current.WorldMembershipEpoch !=
                target.Authority.WorldMembershipEpoch)
        {
            reason = MedusaPeriodicDamageTerminalWithoutHpReason
                .TargetTransferred;
            return true;
        }

        if (!IsCurrentAccountSession(
                current.AccountId,
                current.Session,
                current.Ownership))
        {
            reason = MedusaPeriodicDamageTerminalWithoutHpReason.TargetStale;
            return true;
        }

        int currentHealth;
        long currentVitalsRevision;
        lock (current.Character.VitalsSync)
        {
            currentHealth = current.Character.CurrentHp;
            currentVitalsRevision = current.Character.VitalsRevision;
        }
        if (!_playerLifeRevisions.TryGetValue(
                current.Session,
                out var lifeRevision) ||
            lifeRevision != target.Authority.LifeRevision ||
            currentHealth <= 0)
        {
            reason = MedusaPeriodicDamageTerminalWithoutHpReason.TargetDead;
            return true;
        }

        if (currentVitalsRevision != target.Authority.VitalsRevision ||
            currentHealth != target.CurrentHealth)
        {
            // Same-route, same-life alive vitals drift is the routine pre-HP
            // ECS race. It retains the reservation and refreshes target,
            // recipients, and a strictly higher event ID; it is not terminal.
            reason = default;
            return false;
        }

        reason = default;
        return false;
    }
}
