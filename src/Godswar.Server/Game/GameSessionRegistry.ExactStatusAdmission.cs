namespace Godswar.Server.Game;

using Godswar.Server.Networking;

internal sealed partial class GameSessionRegistry
{
    private static readonly Exception ExactStatusFailClosedReason =
        new OperationCanceledException(
            "Exact status publication failed closed.");

#if DEBUG
    private Action<ClientSession>?
        _protocolCheckBeforeExactStatusDisconnect = null;
    private Action?
        _protocolCheckBeforeExactAdmissionObservation = null;
#endif

    private enum ExactStatusAdmissionOutcome
    {
        Admitted,
        AdmittedTerminal,
        AdmittedTerminalClaimed,
        Canceled,
        RecipientOrTargetStale,
        ProjectionChanged,
        AuthorityUnavailable,
        AdmissionFailed,
        AdmissionFailedClaimed
    }

    private static bool WasAdmitted(
        ExactStatusAdmissionOutcome outcome) => outcome is
        ExactStatusAdmissionOutcome.Admitted or
        ExactStatusAdmissionOutcome.AdmittedTerminal or
        ExactStatusAdmissionOutcome.AdmittedTerminalClaimed;

    private static bool RequiresAdmissionFailureDisconnect(
        ExactStatusAdmissionOutcome outcome) =>
        outcome is ExactStatusAdmissionOutcome.AdmissionFailed or
            ExactStatusAdmissionOutcome.AdmissionFailedClaimed;

    private sealed class ExactStatusDisconnectClaims
    {
        private ClientSession?[] _sessions = [];
        private int _count;

        internal void EnsureCapacity(int additional)
        {
            if (additional < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(additional));
            }
            var required = checked(_count + additional);
            if (_sessions.Length < required)
            {
                Array.Resize(ref _sessions, required);
            }
        }

        internal void CaptureClaimed(ClientSession session)
        {
            _sessions[_count++] = session;
        }

        internal void CompleteAll(GameSessionRegistry registry)
        {
            for (var index = 0; index < _count; index++)
            {
                registry.CompleteClaimedExactStatusDisconnect(
                    _sessions[index]!);
                _sessions[index] = null;
            }
            _count = 0;
        }
    }

    private void ObserveExactAdmissionCompletionAfterStatusGate(
        PlayerStatusState state,
        ClientSession recipient,
        Task completion,
        string label)
    {
        try
        {
            InvokeProtocolCheckBeforeExactAdmissionObservation();
            _ = ObserveExactAdmissionCompletionAfterStatusGateAsync(
                state,
                recipient,
                completion,
                label);
        }
        catch
        {
            // The exact egress already owns the packets. This observer is
            // diagnostic only and must never turn admission into a replay.
        }
    }

    private async Task
        ObserveExactAdmissionCompletionAfterStatusGateAsync(
            PlayerStatusState state,
            ClientSession recipient,
            Task completion,
            string label)
    {
        try
        {
            await completion;
        }
        catch (Exception error)
        {
            // The egress pump owns logical session terminalization and its
            // pre-registered registry-removal callback. This discarded task
            // is diagnostic only; correctness never depends on scheduling it.
            try
            {
                Console.WriteLine(
                    $"[world] exact egress failed label={label}: " +
                    error.Message);
            }
            catch
            {
            }
        }
    }

    private void DisconnectExactStatusRecipient(
        ClientSession recipient)
    {
        if (!recipient.TryClaimDisconnect())
        {
            return;
        }

        CompleteClaimedExactStatusDisconnect(recipient);
    }

    private void CompleteClaimedExactStatusDisconnect(
        ClientSession recipient)
    {
        try
        {
            InvokeProtocolCheckBeforeExactStatusDisconnect(recipient);
        }
        catch
        {
        }
        finally
        {
            recipient.CompleteClaimedDisconnect(
                ExactStatusFailClosedReason);
            try
            {
                Remove(recipient);
            }
            catch
            {
            }
        }
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void InvokeProtocolCheckBeforeExactAdmissionObservation()
    {
#if DEBUG
        _protocolCheckBeforeExactAdmissionObservation?.Invoke();
#endif
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void InvokeProtocolCheckBeforeExactStatusDisconnect(
        ClientSession recipient)
    {
#if DEBUG
        _protocolCheckBeforeExactStatusDisconnect?.Invoke(recipient);
#endif
    }
}
