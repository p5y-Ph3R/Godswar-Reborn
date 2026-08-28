using Godswar.Server.Networking;
using Godswar.Server.Protocol;

namespace Godswar.Server.ProtocolChecks;

internal static partial class MedusaInstanceOwnershipChecks
{
#if DEBUG
    private static async Task CheckRawExactTerminalBoolTruthAsync()
    {
        var transport = new SwitchableMedusaTransport();
        await using var session = new ClientSession(
            transport,
            new NetworkRuntimeOptions
            {
                ReliableEgressQueueItems = 2,
                ReliableEgressQueueBytes =
                    LegacyProtocolLimits.MaxPacketLength * 2,
                ReliableEgressPendingItems = 4,
                ReliableEgressPendingBytes =
                    LegacyProtocolLimits.MaxPacketLength * 2,
                ReliableWriteTimeoutMilliseconds = 10_000
            },
            NetworkEndpointRole.Game);
        session.ProtocolCheckFailNextExactBatchAfterCommit();
        var owned = session.TryAdmitExactBatch(
            [
                MedusaTestPacket(0x7D21),
                MedusaTestPacket(0x7D22)
            ],
            out var completion);
        await ObserveExpectedExactFailureAsync(completion);
        Check.True(
            owned &&
            completion.IsCompleted &&
            completion.IsFaulted &&
            transport.WriteCount == 0 &&
            !session.IsDisconnected,
            "the raw exact-batch bool preserves AdmittedTerminal ownership and a settled aggregate fault without claiming caller-owned teardown or retrying bytes");
        session.Disconnect();
        Check.True(
            session.IsDisconnected && transport.IsDisconnected,
            "the raw AdmittedTerminal owner can finalize the sealed session exactly once");
    }
#endif
}
