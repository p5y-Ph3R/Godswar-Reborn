using Godswar.Server.Game.WorldInstances;
using Godswar.Server.Networking;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
    private sealed class PeriodicInvariantSettlementAuthority(
        MedusaPeriodicDamageIdentity identity)
        : MedusaPeriodicDamageInvariantSettlementAuthority
    {
        internal override bool Matches(
            in MedusaPeriodicDamageIdentity candidate) =>
            candidate == identity;
    }

    private async Task<bool> PersistAndRemoveMedusaPeriodicDamageAsync(
        WorldInstanceRuntime runtime,
        MedusaPeriodicDamageLedgerHandle handle,
        MedusaPeriodicDamageLedgerSnapshot snapshot,
        DateTimeOffset now)
    {
        if (snapshot.HpCommit is { AfterHealth: 0 } &&
            !snapshot.LethalStatusCleanupSettled)
        {
            if (!await CleanupMedusaPeriodicLethalStatusLifeAsync(
                    runtime,
                    handle,
                    snapshot,
                    now))
            {
                return false;
            }
        }

        if (_medusaPeriodicDamageLedger
            .TryGetPersistenceSettlementAuthority(
                handle,
                out var recoveredAuthority))
        {
            return _medusaPeriodicDamageLedger.RemovePublished(
                    handle,
                    recoveredAuthority) ==
                MedusaPeriodicDamageLedgerMutationOutcome.Removed;
        }
        if (!_medusaPeriodicDamageLedger.TryClaimPersistenceAttempt(handle))
        {
            return false;
        }

        var persistenceSucceeded = snapshot.HpCommit is null;
        try
        {
            if (snapshot.HpCommit is not null &&
                TryGetPeriodicSelf(
                    _medusaPeriodicDamageLedger,
                    handle,
                    snapshot.RecipientCount,
                    out var self))
            {
                await PersistRoutineVitalsAsync(
                    self.Context,
                    CancellationToken.None);
                persistenceSucceeded = true;
            }
        }
        catch (Exception error)
        {
            Console.WriteLine(
                "[medusa-periodic] vitals persistence observed failure " +
                $"target={snapshot.Identity.TargetCharacterId}: " +
                error.Message);
        }

        if (!persistenceSucceeded)
        {
            _ = _medusaPeriodicDamageLedger
                .ReleasePersistenceAttempt(handle);
            return false;
        }

        _ = _medusaPeriodicDamageLedger
            .MarkPersistenceAttemptSettled(handle);

        return _medusaPeriodicDamageLedger
                .TryGetPersistenceSettlementAuthority(
                    handle,
                    out var authority) &&
            _medusaPeriodicDamageLedger.RemovePublished(
                handle,
                authority) ==
                MedusaPeriodicDamageLedgerMutationOutcome.Removed;
    }

    private async Task<bool> CleanupMedusaPeriodicLethalStatusLifeAsync(
        WorldInstanceRuntime runtime,
        MedusaPeriodicDamageLedgerHandle handle,
        MedusaPeriodicDamageLedgerSnapshot snapshot,
        DateTimeOffset now)
    {
        _ = now;
        if (!TryGetPeriodicSelf(
                _medusaPeriodicDamageLedger,
                handle,
                snapshot.RecipientCount,
                out var self))
        {
            return false;
        }

        if (_playerStatusStates.TryGetValue(
                self.Session,
                out var statusState))
        {
            await statusState.Gate.WaitAsync(CancellationToken.None);
            try
            {
                lock (_gate)
                {
                    if (TryResolveExactMedusaPublicationContextLocked(
                            runtime,
                            self.Context,
                            snapshot.HpCommit!.Value.AfterLifeRevision,
                            out _))
                    {
                        _ = RemovePersistentRuntimeStatusForLifeRevisionLocked(
                            self.Session,
                            statusState,
                            snapshot.HpCommit.Value.AfterLifeRevision,
                            MountCatalog.RuntimeStatusKind);
                    }
                }
            }
            finally
            {
                statusState.Gate.Release();
            }
        }

        return _medusaPeriodicDamageLedger
                .MarkLethalStatusCleanupSettled(handle) is
            MedusaPeriodicDamageLedgerMutationOutcome.Published or
            MedusaPeriodicDamageLedgerMutationOutcome.AlreadyPresent;
    }

    private async Task<bool> SettleMedusaPeriodicInvariantAsync(
        MedusaPeriodicDamageLedgerHandle handle,
        MedusaPeriodicDamageLedgerSnapshot snapshot)
    {
        var disconnects = new List<ClientSession>();
        lock (_gate)
        {
            for (var index = 0; index < snapshot.RecipientCount; index++)
            {
                if (!_medusaPeriodicDamageLedger.TryGetRecipientIdentity(
                        handle,
                        index,
                        out var identity) ||
                    !TryResolveMedusaPublicationContextLocked(
                        identity.Context,
                        identity.LifeRevision,
                        out var current) ||
                    !current.Session.TryClaimDisconnect())
                {
                    continue;
                }
                disconnects.Add(current.Session);
            }
        }
        foreach (var session in disconnects)
        {
            CompleteClaimedExactStatusDisconnect(session);
        }

        if (snapshot.HpCommit is not null &&
            TryGetPeriodicSelf(
                _medusaPeriodicDamageLedger,
                handle,
                snapshot.RecipientCount,
                out var self))
        {
            try
            {
                await PersistRoutineVitalsAsync(
                    self.Context,
                    CancellationToken.None);
            }
            catch (Exception error)
            {
                Console.WriteLine(
                    "[medusa-periodic] invariant persistence observed " +
                    $"failure target={snapshot.Identity.TargetCharacterId}: " +
                    error.Message);
            }
        }

        var authority = new PeriodicInvariantSettlementAuthority(
            snapshot.Identity);
        return _medusaPeriodicDamageLedger.SettleOwnerInvariantFault(
                handle,
                authority) ==
            MedusaPeriodicDamageLedgerMutationOutcome.InvariantSettled;
    }
}
