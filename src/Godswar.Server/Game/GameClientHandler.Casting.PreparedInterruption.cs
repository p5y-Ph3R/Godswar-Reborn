namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
#if DEBUG
    private Action? _protocolCheckAfterPreparedInterruptionReservation =
        null;
    private Func<Exception?>?
        _protocolCheckPreparedInterruptionStartError = null;
#endif

    private sealed class HandlerPreparedSkillCastInterruption(
        GameClientHandler handler,
        PendingSkillCast pending)
        : PreparedSkillCastInterruption
    {
        private PreparedSkillCastInterruptionClaimOutcome _outcome;
        private bool _reservationOwned;
        private int _released;

        private protected override
            PreparedSkillCastInterruptionClaimOutcome ClaimCore()
        {
            lock (handler._skillCastSync)
            {
                if (!ReferenceEquals(handler._pendingSkillCast, pending))
                {
                    _outcome = PreparedSkillCastInterruptionClaimOutcome
                        .NoLongerCurrent;
                }
                else if (pending.CompletionClaimed)
                {
                    _outcome = PreparedSkillCastInterruptionClaimOutcome
                        .CompletionWon;
                }
                else if (pending.InterruptionClaimed)
                {
                    _outcome = pending.PreparedInterruptionClaimed
                        ? PreparedSkillCastInterruptionClaimOutcome
                            .AlreadyInterrupted
                        : PreparedSkillCastInterruptionClaimOutcome
                            .NoLongerCurrent;
                    if (_outcome ==
                        PreparedSkillCastInterruptionClaimOutcome
                            .AlreadyInterrupted)
                    {
                        pending.PreparedInterruptionReservations++;
                        _reservationOwned = true;
                    }
                }
                else
                {
                    pending.InterruptionClaimed = true;
                    pending.PreparedInterruptionClaimed = true;
                    pending.PreparedInterruptionReservations++;
                    _reservationOwned = true;
#if DEBUG
                    handler._protocolCheckAfterPreparedInterruptionReservation?
                        .Invoke();
#endif
                    _outcome = PreparedSkillCastInterruptionClaimOutcome
                        .InterruptionWon;
                }

                return _outcome;
            }
        }

        private protected override
            PreparedSkillCastNotificationClaimOutcome
            ClaimNotificationCore()
        {
            lock (handler._skillCastSync)
            {
                if (_outcome is not (
                        PreparedSkillCastInterruptionClaimOutcome
                            .InterruptionWon or
                        PreparedSkillCastInterruptionClaimOutcome
                            .AlreadyInterrupted))
                {
                    return PreparedSkillCastNotificationClaimOutcome
                        .NotRequired;
                }
                if (pending.InterruptionNotificationClaimed)
                {
                    return PreparedSkillCastNotificationClaimOutcome
                        .Delegated;
                }

                pending.InterruptionNotificationClaimed = true;
                return PreparedSkillCastNotificationClaimOutcome.Owner;
            }
        }

        internal override Task<bool>
            WaitForNotificationAdmissionAsync() =>
            pending.InterruptionNotificationAdmission;

        private protected override void
            CompleteNotificationAdmissionCore(bool admitted) =>
            pending.CompleteInterruptionNotificationAdmission(admitted);

        private protected override void ReleaseCore()
        {
            if (!_reservationOwned ||
                Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            lock (handler._skillCastSync)
            {
                pending.PreparedInterruptionReservations--;
                if (pending.PreparedInterruptionReservations == 0 &&
                    ReferenceEquals(handler._pendingSkillCast, pending))
                {
                    handler._pendingSkillCast = null;
                }
            }
        }

        internal override async Task
            CompleteAfterStatusPublicationAsync()
        {
            if (_outcome ==
                PreparedSkillCastInterruptionClaimOutcome.CompletionWon)
            {
                await pending.LifecycleTask;
                return;
            }
            if (_outcome !=
                PreparedSkillCastInterruptionClaimOutcome.InterruptionWon &&
                _outcome !=
                    PreparedSkillCastInterruptionClaimOutcome
                        .AlreadyInterrupted)
            {
                return;
            }

            pending.RequestCancellation();
            await Task.Yield();
            // A newer Medusa hit may supersede the first effect identity and
            // inherit its one notification. Both capabilities share this
            // reliable cast-start barrier and reservation.
            var start = await pending.StartPublication;
#if DEBUG
            var injectedStartError = handler
                ._protocolCheckPreparedInterruptionStartError?.Invoke();
            if (injectedStartError is not null)
            {
                start = new StartPublicationResult(injectedStartError);
            }
#endif
            if (start.Error is not null)
            {
                throw new InvalidOperationException(
                    "The interrupted cast start was not published.",
                    start.Error);
            }
        }
    }

    private PreparedSkillCastInterruption?
        PreparePendingSkillCastInterruption(
            SkillCastInterruptionReason _)
    {
        lock (_skillCastSync)
        {
            return _pendingSkillCast is { } pending
                ? new HandlerPreparedSkillCastInterruption(
                    this,
                    pending)
                : null;
        }
    }

    private void RegisterSkillCastInterruption()
    {
        _registry.RegisterSkillCastInterruptionSink(
            _session,
            InterruptPendingSkillCastAsync);
        try
        {
            _registry.RegisterPreparedSkillCastInterruptionSink(
                _session,
                PreparePendingSkillCastInterruption);
            try
            {
                _registry.RegisterInstanceTransitionSink(
                    _session,
                    HandlePartyInstanceTransitionAsync);
            }
            catch
            {
                _registry.UnregisterPreparedSkillCastInterruptionSink(
                    _session);
                throw;
            }
        }
        catch
        {
            _registry.UnregisterSkillCastInterruptionSink(_session);
            throw;
        }
    }

    private void UnregisterSkillCastInterruption()
    {
        _registry.UnregisterInstanceTransitionSink(_session);
        _registry.UnregisterPreparedSkillCastInterruptionSink(_session);
        _registry.UnregisterSkillCastInterruptionSink(_session);
    }
}
