using Godswar.Server.Game.WorldInstances;

namespace Godswar.Server.Game;

internal sealed partial class GameSessionRegistry
{
#if DEBUG
    internal Action?
        ProtocolCheckAfterMedusaPeriodicOwnerAcknowledgement { get; set; }
    internal Action?
        ProtocolCheckAfterMedusaPeriodicLedgerPrepared { get; set; }
#endif

    private async Task<bool> DrainMedusaPeriodicDamageAsync(
        WorldInstanceRuntime runtime,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!InvokeWorldOwner(
                runtime,
                static map => map.HasBoundMedusaEncounter()))
        {
            return true;
        }
        if (_playerRuntimeMode != PlayerRuntimeMode.Ecs)
        {
            return true;
        }

        for (var transition = 0; transition < 256; transition++)
        {
            if (!_medusaPeriodicDamageLedger.TryGetRetained(
                    runtime.InstanceId,
                    out var handle,
                    out var snapshot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var observation =
                    InvokeWorldOwnerAuthoritativeMutation(
                        runtime,
                        map =>
                        {
                            var routed = map.TryObserveMedusaTime(
                                now,
                                out var result);
                            return (routed, result);
                        });
                if (!observation.routed)
                {
                    return true;
                }
                if (observation.result.MechanicsResult is not
                    { PeriodicDamage: { } reservation })
                {
                    return observation.result.GateOutcome !=
                        MedusaOwnedOperationGateOutcome.InvariantFault;
                }
                if (!TryPrepareMedusaPeriodicDamage(
                        runtime,
                        reservation))
                {
                    return false;
                }
                continue;
            }

            try
            {
                var progressed = snapshot.Phase switch
                {
                    MedusaPeriodicDamageLedgerPhase.Prepared =>
                        TryProcessPreparedMedusaPeriodicDamage(
                            runtime,
                            handle,
                            snapshot),
                    MedusaPeriodicDamageLedgerPhase.PreHpInvariantFault =>
                        TryAbortMedusaPeriodicDamage(
                            runtime,
                            handle),
                    MedusaPeriodicDamageLedgerPhase.HPCommitted =>
                        TryAcknowledgeMedusaPeriodicDamage(
                            runtime,
                            handle),
                    MedusaPeriodicDamageLedgerPhase.OwnerAcked =>
                        await CompleteAndPublishMedusaPeriodicDamageAsync(
                            runtime,
                            handle,
                            snapshot,
                            now),
                    MedusaPeriodicDamageLedgerPhase.Published =>
                        await PersistAndRemoveMedusaPeriodicDamageAsync(
                            runtime,
                            handle,
                            snapshot,
                            now),
                    MedusaPeriodicDamageLedgerPhase.OwnerInvariantFault =>
                        await SettleMedusaPeriodicInvariantAsync(
                            handle,
                            snapshot),
                    _ => false
                };
                if (!progressed)
                {
                    return false;
                }
            }
            catch (Exception error)
            {
                Console.WriteLine(
                    "[medusa-periodic] retained tick deferred " +
                    $"instance={runtime.InstanceId}: {error.Message}");
                return false;
            }
        }

        return false;
    }

    private bool TryPrepareMedusaPeriodicDamage(
        WorldInstanceRuntime runtime,
        MedusaEncounterMechanicsRuntime.PeriodicDamageReservation
            reservation)
    {
        var members = InvokeWorldOwnerAuthoritativeMutation(
            runtime,
            static map => map.Snapshot());
        if (!TryCaptureMedusaPeriodicDamageTarget(
                runtime,
                reservation.Identity,
                members,
                out var target,
                out var recipients,
                out var playerEventFloor) ||
            !TryAllocateMonsterAttackEventIdAbove(
                playerEventFloor,
                out var eventId))
        {
            return false;
        }

        var intent = reservation.Identity.Damage >= target.CurrentHealth
            ? MedusaPeriodicDamageOwnerIntent.Terminal
            : MedusaPeriodicDamageOwnerIntent.Applied;
        var preparation = InvokeWorldOwnerAuthoritativeMutation(
            runtime,
            map =>
            {
                var routed =
                    map.TryPrepareMedusaPeriodicDamageOwnerReceipt(
                        reservation,
                        eventId,
                        intent,
                        out var result);
                return (routed, result);
            });
        if (!preparation.routed ||
            !preparation.result.IsPrepared ||
            preparation.result.Receipt is not { } receipt)
        {
            return false;
        }

        var outcome = _medusaPeriodicDamageLedger.TryPrepare(
                reservation,
                target,
                eventId,
                receipt,
                recipients,
                out _);
#if DEBUG
        if (outcome == MedusaPeriodicDamageLedgerMutationOutcome.Prepared)
        {
            ProtocolCheckAfterMedusaPeriodicLedgerPrepared?.Invoke();
        }
#endif
        return outcome is MedusaPeriodicDamageLedgerMutationOutcome.Prepared
            or MedusaPeriodicDamageLedgerMutationOutcome.AlreadyPresent;
    }

    private bool TryProcessPreparedMedusaPeriodicDamage(
        WorldInstanceRuntime runtime,
        MedusaPeriodicDamageLedgerHandle handle,
        in MedusaPeriodicDamageLedgerSnapshot snapshot)
    {
        if (TryConsumeMedusaPeriodicTerminalWithoutHp(runtime, handle))
        {
            return true;
        }
        if (!_medusaPeriodicDamageLedger.TryGetPreparedAttempt(
                handle,
                out var target,
                out var eventId,
                out var hpObserver))
        {
            return false;
        }

        try
        {
            if (TryApplyMedusaPeriodicDamage(
                    runtime,
                    handle,
                    snapshot.RecipientCount,
                    target,
                    eventId,
                    hpObserver,
                    out var decision) &&
                decision.Applied)
            {
                return true;
            }
        }
        catch
        {
            if (_medusaPeriodicDamageLedger.TryGetSnapshot(
                    runtime.InstanceId,
                    out var afterFault) &&
                afterFault.Phase is
                    MedusaPeriodicDamageLedgerPhase.HPCommitted or
                    MedusaPeriodicDamageLedgerPhase.PostHpQuarantined)
            {
                return afterFault.Phase ==
                    MedusaPeriodicDamageLedgerPhase.HPCommitted;
            }
        }

        return TryConsumeMedusaPeriodicTerminalWithoutHp(runtime, handle) ||
            TryRefreshMedusaPeriodicDamage(runtime, handle, snapshot);
    }

    private bool TryConsumeMedusaPeriodicTerminalWithoutHp(
        WorldInstanceRuntime runtime,
        MedusaPeriodicDamageLedgerHandle handle)
    {
        var classified =
            TryCreateClassifiedMedusaPeriodicDamageTerminalWithoutHpAuthority(
                handle,
                out var authority);
        if (classified is not (
                MedusaPeriodicDamageLedgerMutationOutcome.Prepared or
                MedusaPeriodicDamageLedgerMutationOutcome.AlreadyPresent) ||
            authority is null ||
            !_medusaPeriodicDamageLedger.TryGetCurrentOwnerReceipt(
                handle,
                out var receipt))
        {
            return false;
        }

        var completion = InvokeWorldOwnerAuthoritativeMutation(
            runtime,
            map =>
            {
                var routed =
                    map.TryCompleteMedusaPeriodicDamageTerminalWithoutHp(
                        receipt,
                        authority,
                        out var result);
                return (routed, result);
            });
        if (!completion.routed)
        {
            return false;
        }
        return _medusaPeriodicDamageLedger.MarkTerminalWithoutHp(
                handle,
                completion.result) is
            MedusaPeriodicDamageLedgerMutationOutcome.OwnerAcked or
            MedusaPeriodicDamageLedgerMutationOutcome.AlreadyPresent;
    }

    private bool TryRefreshMedusaPeriodicDamage(
        WorldInstanceRuntime runtime,
        MedusaPeriodicDamageLedgerHandle handle,
        in MedusaPeriodicDamageLedgerSnapshot prior)
    {
        if (!_medusaPeriodicDamageLedger.TryGetPreparedReservation(
                handle,
                out var reservation) ||
            !_medusaPeriodicDamageLedger.TryGetCurrentOwnerReceipt(
                handle,
                out var previousReceipt))
        {
            return false;
        }
        var members = InvokeWorldOwnerAuthoritativeMutation(
            runtime,
            static map => map.Snapshot());
        if (!TryCaptureMedusaPeriodicDamageTarget(
                runtime,
                reservation.Identity,
                members,
                out var target,
                out var recipients,
                out var playerEventFloor) ||
            !TryAllocateMonsterAttackEventIdAbove(
                Math.Max(prior.AttackEventId, playerEventFloor),
                out var replacementEventId))
        {
            return false;
        }

        var created = _medusaPeriodicDamageLedger
            .TryCreateReceiptRefreshAuthority(
                handle,
                target,
                replacementEventId,
                recipients,
                out var refreshAuthority);
        if (created ==
            MedusaPeriodicDamageLedgerMutationOutcome.AttemptsExhausted)
        {
            return true;
        }
        if (created is not (
                MedusaPeriodicDamageLedgerMutationOutcome.Prepared or
                MedusaPeriodicDamageLedgerMutationOutcome.AlreadyPresent) ||
            refreshAuthority is null)
        {
            return false;
        }

        var intent = reservation.Identity.Damage >= target.CurrentHealth
            ? MedusaPeriodicDamageOwnerIntent.Terminal
            : MedusaPeriodicDamageOwnerIntent.Applied;
        try
        {
            var preparation = InvokeWorldOwnerAuthoritativeMutation(
                runtime,
                map =>
                {
                    var routed =
                        map.TryPrepareMedusaPeriodicDamageOwnerReceipt(
                            reservation,
                            replacementEventId,
                            intent,
                            refreshAuthority,
                            out var result);
                    return (routed, result);
                });
            return preparation.routed && preparation.result.IsPrepared;
        }
        catch
        {
            return _medusaPeriodicDamageLedger.TryGetSnapshot(
                    runtime.InstanceId,
                    out var recovered) &&
                recovered.AttackEventId == replacementEventId &&
                recovered.Phase ==
                    MedusaPeriodicDamageLedgerPhase.Prepared;
        }
    }

    private bool TryAcknowledgeMedusaPeriodicDamage(
        WorldInstanceRuntime runtime,
        MedusaPeriodicDamageLedgerHandle handle)
    {
        if (!_medusaPeriodicDamageLedger
                .TryGetOwnerAcknowledgementAuthority(
                    handle,
                    out var authority) ||
            !_medusaPeriodicDamageLedger.TryGetCurrentOwnerReceipt(
                handle,
                out var receipt))
        {
            return false;
        }

        var acknowledgement = InvokeWorldOwnerAuthoritativeMutation(
            runtime,
            map =>
            {
                var routed =
                    map.TryReconcileMedusaPeriodicDamageOwnerReceipt(
                        receipt,
                        authority,
                        out var result);
                return (routed, result);
            });
        if (!acknowledgement.routed)
        {
            return false;
        }
#if DEBUG
        ProtocolCheckAfterMedusaPeriodicOwnerAcknowledgement?.Invoke();
#endif
        return _medusaPeriodicDamageLedger.MarkOwnerAcked(
                handle,
                authority,
                acknowledgement.result) is
            MedusaPeriodicDamageLedgerMutationOutcome.OwnerAcked or
            MedusaPeriodicDamageLedgerMutationOutcome.AlreadyPresent or
            MedusaPeriodicDamageLedgerMutationOutcome.OwnerInvariantFault;
    }

    private bool TryAbortMedusaPeriodicDamage(
        WorldInstanceRuntime runtime,
        MedusaPeriodicDamageLedgerHandle handle)
    {
        if (!_medusaPeriodicDamageLedger.TryGetCurrentOwnerReceipt(
                handle,
                out var receipt) ||
            _medusaPeriodicDamageLedger.TryCreatePreparedAbortAuthority(
                handle,
                receipt,
                out var authority) is not (
                    MedusaPeriodicDamageLedgerMutationOutcome.Prepared or
                    MedusaPeriodicDamageLedgerMutationOutcome
                        .AlreadyPresent) ||
            authority is null)
        {
            return false;
        }

        var aborted = InvokeWorldOwnerAuthoritativeMutation(
            runtime,
            map =>
            {
                var routed =
                    map.TryAbortPreparedMedusaPeriodicDamageOwnerReceipt(
                        receipt,
                        authority,
                        out var result);
                return (routed, result);
            });
        return aborted.routed &&
            _medusaPeriodicDamageLedger.MarkPreparedOwnerAborted(
                handle,
                authority,
                aborted.result) is
                MedusaPeriodicDamageLedgerMutationOutcome
                    .OwnerInvariantFault or
                MedusaPeriodicDamageLedgerMutationOutcome.AlreadyPresent;
    }
}
