namespace Godswar.Server.Networking;

internal sealed partial class ClientSession
{
    private Action<ClientSession>? _egressTerminalObserver;

    internal void RegisterEgressTerminalObserver(
        Action<ClientSession> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var existing = Interlocked.CompareExchange(
            ref _egressTerminalObserver,
            observer,
            comparand: null);
        if (existing is not null && existing != observer)
        {
            throw new InvalidOperationException(
                "A session can have only one egress terminal owner.");
        }
    }

    /// <summary>
    /// Synchronous, non-waiting reliable admission for packets whose routing
    /// authority is held by the caller. Completion may be observed after
    /// releasing that authority fence.
    /// </summary>
    internal bool TryAdmitExact(
        ReadOnlyMemory<byte> clearPacket,
        out Task completion) =>
        WasExactEgressOwned(
            TryAdmitExactOutcome(clearPacket, out completion));

    internal bool TryAdmitExactBatch(
        IReadOnlyList<ReadOnlyMemory<byte>> clearPackets,
        out Task completion) =>
        WasExactEgressOwned(
            TryAdmitExactBatchOutcome(clearPackets, out completion));

    private static bool WasExactEgressOwned(
        ExactEgressAdmissionOutcome outcome) => outcome is
        ExactEgressAdmissionOutcome.Admitted or
        ExactEgressAdmissionOutcome.AdmittedTerminal;

    internal ExactEgressAdmissionOutcome TryAdmitExactOutcome(
        ReadOnlyMemory<byte> clearPacket,
        out Task completion) =>
        _egress.TryWriteBatch([clearPacket], out completion);

    internal ExactEgressAdmissionOutcome TryAdmitExactBatchOutcome(
        IReadOnlyList<ReadOnlyMemory<byte>> clearPackets,
        out Task completion) =>
        _egress.TryWriteBatch(clearPackets, out completion);

#if DEBUG
    internal void ProtocolCheckFailNextExactBatchAfterCommit() =>
        _egress.ProtocolCheckFailNextExactBatchAfterCommit();
#endif

    private void TerminalizeFromEgress(Exception error)
    {
        _ = error;
        _ = Interlocked.CompareExchange(ref _disconnected, 1, 0);
        try
        {
            CancelLifetime();
        }
        catch
        {
        }
        try
        {
            DisconnectTransport();
        }
        catch
        {
        }
        try
        {
            Volatile.Read(ref _egressTerminalObserver)?.Invoke(this);
        }
        catch
        {
            // Logical and transport teardown are already complete. The
            // owner also observes IsDisconnected on every routing path.
        }
    }
}
