using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Networking.Secure.Udp;
using Godswar.Server.World.Systems.Players;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecurePhase4NetworkEmulationChecks
{
    private const uint WorldGeneration = 23;
    private const byte MapId = 2;
    private const uint SourceObjectId = 0x1520;

    public static Task RunAsync()
    {
        CheckSeededImpairmentsAndReordering();
        CheckUdpToTlsFallbackAndGlobalDedupe();
        CheckSingleSlotBoundedOverload();
        CheckProtocolMtuBudget();
        CheckPeriodicKeyframeRecoveryAndNoRollback();
        return Task.CompletedTask;
    }

    private static void CheckSeededImpairmentsAndReordering()
    {
        var first = CreateImpairedNetwork();
        var firstDuplicate = PopulateImpairedNetwork(first);
        var firstDeliveries = first.Drain();

        var replay = CreateImpairedNetwork();
        var replayDuplicate = PopulateImpairedNetwork(replay);
        var replayDeliveries = replay.Drain();

        Check.Equal(
            Signature(firstDeliveries),
            Signature(replayDeliveries),
            "seeded latency and jitter schedule is deterministic");
        Check.Equal(
            firstDuplicate,
            replayDuplicate,
            "seeded duplicate packet identities are deterministic");
        Check.Equal(
            1,
            first.UdpBlockedDrops,
            "initial UDP-blocked packet is dropped");
        Check.True(
            firstDeliveries.Any(static delivery =>
                delivery.LogicalInputId == 2),
            "UDP passes immediately when the modeled block expires");
        Check.Equal(
            3,
            first.BurstLossDrops,
            "configured consecutive UDP burst is lost");
        Check.Equal(
            7,
            first.MaximumPending,
            "bounded impairment fixture pending-packet peak");

        Check.True(
            firstDuplicate.DuplicatePacketIdentity >
            firstDuplicate.PacketIdentity,
            "duplicate has a later physical packet identity");
        var duplicateDeliveries = firstDeliveries
            .Where(static delivery =>
                delivery.LogicalInputId == 9)
            .ToArray();
        Check.True(
            duplicateDeliveries.Length == 2 &&
            duplicateDeliveries[0].PacketIdentity !=
                duplicateDeliveries[1].PacketIdentity &&
            duplicateDeliveries[0].Input.InputId ==
                duplicateDeliveries[1].Input.InputId,
            "later physical packet retains the same logical input ID");

        foreach (var delivery in firstDeliveries)
        {
            Check.True(
                delivery.JitterMilliseconds is >= -20 and <= 20,
                "seeded jitter remains inside configured bounds");
            Check.Equal(
                SecureRealtimeMovementProtocol.MovementInputBytes,
                delivery.PayloadBytes,
                "emulated movement uses exact protocol size");
        }

        var inputFiveIndex = IndexOfLogicalInput(
            firstDeliveries,
            logicalInputId: 5);
        var inputFourIndex = IndexOfLogicalInput(
            firstDeliveries,
            logicalInputId: 4);
        Check.True(
            inputFiveIndex >= 0 &&
            inputFourIndex > inputFiveIndex,
            "forced one-input reordering window is observed");

        var transport = new SecureRealtimeTransportState();
        var statuses = new List<(
            ulong PacketIdentity,
            ulong LogicalInputId,
            SecureRealtimeReconciliationStatus Status,
            ulong HighestInputId)>();
        foreach (var delivery in firstDeliveries)
        {
            var result = transport.Reconcile(
                delivery.Source,
                delivery.Input.TransportEpoch,
                delivery.Input.InputId);
            statuses.Add(
                (
                    delivery.PacketIdentity,
                    delivery.LogicalInputId,
                    result.Status,
                    result.HighestInputId));
        }

        var reordered = statuses.Single(
            static status => status.LogicalInputId == 4);
        Check.True(
            reordered.Status ==
                SecureRealtimeReconciliationStatus.StaleInput &&
            reordered.HighestInputId == 5,
            "out-of-order input inside fixture window cannot roll back");
        var duplicateStatuses = statuses
            .Where(static status => status.LogicalInputId == 9)
            .OrderBy(static status => status.PacketIdentity)
            .ToArray();
        Check.True(
            duplicateStatuses.Length == 2 &&
            duplicateStatuses[0].Status ==
                SecureRealtimeReconciliationStatus.Accepted &&
            duplicateStatuses[1].Status ==
                SecureRealtimeReconciliationStatus.Duplicate,
            "logical duplicate is suppressed independently of packet ID");
        var finalTransport = transport.GetSnapshot();
        Check.True(
            finalTransport.HasTransport &&
            finalTransport.TransportSource ==
                SecureRealtimeTransportSource.Udp &&
            finalTransport.HighestInputId == 10,
            "post-impairment transport converges to newest UDP input");
    }

    private static void CheckUdpToTlsFallbackAndGlobalDedupe()
    {
        var network = new DeterministicMovementNetwork(
            seed: 9,
            baseLatencyMilliseconds: 10,
            jitterMilliseconds: 0);
        var udpSend = network.Send(
            WireInput(
                SecureRealtimeTransportSource.Udp,
                epoch: 1,
                inputId: 41,
                x: 0.5f),
            SecureRealtimeTransportSource.Udp,
            TimeSpan.Zero);
        var tlsRetry = network.Send(
            WireInput(
                SecureRealtimeTransportSource.Tls,
                epoch: 2,
                inputId: 41,
                x: 0.5f),
            SecureRealtimeTransportSource.Tls,
            TimeSpan.FromMilliseconds(100));
        network.Send(
            WireInput(
                SecureRealtimeTransportSource.Tls,
                epoch: 2,
                inputId: 42,
                x: 1f),
            SecureRealtimeTransportSource.Tls,
            TimeSpan.FromMilliseconds(150));

        Check.True(
            tlsRetry.PacketIdentity > udpSend.PacketIdentity,
            "TLS retry has a later packet identity than UDP original");

        var transport = new SecureRealtimeTransportState();
        var authority = CreateAuthority();
        var deliveries = network.Drain();

        var udp = deliveries[0];
        var udpResult = transport.Reconcile(
            udp.Source,
            udp.Input.TransportEpoch,
            udp.Input.InputId);
        Check.True(
            udpResult.Status ==
                SecureRealtimeReconciliationStatus.Accepted,
            "initial UDP input establishes transport");
        var udpDecision = authority.ProcessLatest(
            AuthorityInput(udp),
            World(
                epoch: 1,
                AuthoritativePlayerMovementSource.Udp),
            udp.DeliveredAt);
        Check.True(
            udpDecision.Accepted &&
            udpDecision.AcknowledgedInputId == 41,
            "initial UDP input reaches authority");

        var retry = deliveries[1];
        var retryResult = transport.Reconcile(
            retry.Source,
            retry.Input.TransportEpoch,
            retry.Input.InputId);
        Check.True(
            retryResult.Status ==
                SecureRealtimeReconciliationStatus
                    .TransportChangedDuplicate &&
            retryResult.ShouldEnqueue,
            "same logical input emits only an epoch-transition marker");
        var transportAfterRetry = transport.GetSnapshot();
        Check.True(
            transportAfterRetry.TransportEpoch == 2 &&
            transportAfterRetry.TransportSource ==
                SecureRealtimeTransportSource.Tls &&
            transportAfterRetry.HighestInputId == 41,
            "duplicate retry safely commits exact-next TLS fallback");

        var beforeHandoff = authority.Snapshot;
        Check.True(
            authority.TryAdvanceTransportEpoch(2),
            "authority accepts authenticated exact-next TLS epoch");
        var afterHandoff = authority.Snapshot;
        Check.True(
            afterHandoff.AcknowledgedInputId ==
                beforeHandoff.AcknowledgedInputId &&
            afterHandoff.Revision == beforeHandoff.Revision &&
            afterHandoff.AuthoritativeX ==
                beforeHandoff.AuthoritativeX,
            "fallback cannot roll ACK, revision, or position back");

        var next = deliveries[2];
        var nextResult = transport.Reconcile(
            next.Source,
            next.Input.TransportEpoch,
            next.Input.InputId);
        Check.True(
            nextResult.Status ==
                SecureRealtimeReconciliationStatus.Accepted,
            "greater global input continues on TLS");
        var nextDecision = authority.ProcessLatest(
            AuthorityInput(next),
            World(
                epoch: 2,
                AuthoritativePlayerMovementSource.Tls),
            next.DeliveredAt);
        Check.True(
            nextDecision.Accepted &&
            nextDecision.AcknowledgedInputId == 42 &&
            nextDecision.AuthoritativeX == 1f &&
            nextDecision.Revision > beforeHandoff.Revision,
            "TLS continuation converges without rollback");
    }

    private static void CheckSingleSlotBoundedOverload()
    {
        using var slot =
            new SecureRealtimeSingleSlot<
                SecureRealtimeMovementIngress>();
        for (ulong inputId = 1; inputId <= 10_000; inputId++)
        {
            var input = WireInput(
                SecureRealtimeTransportSource.Udp,
                epoch: 1,
                inputId,
                x: inputId / 10_000f);
            var ingress = new SecureRealtimeMovementIngress(
                input,
                SecureRealtimeTransportSource.Udp,
                TimeSpan.FromMilliseconds(
                    checked((long)inputId)),
                SecureRealtimeMovementIngressKind.Input);
            var status = slot.Offer(ingress);
            Check.True(
                status ==
                    (inputId == 1
                        ? SecureRealtimeMailboxOfferStatus.Accepted
                        : SecureRealtimeMailboxOfferStatus.Replaced),
                "one-slot overload uses latest-value replacement");
        }

        var saturated = slot.GetSnapshot();
        Check.True(
            saturated.HasItem &&
            saturated.Accepted == 10_000 &&
            saturated.Replaced == 9_999 &&
            saturated.Taken == 0,
            "10,000 offers retain one item with exact replacement count");
        Check.True(
            slot.TryTake(out var latest) &&
            latest.Input.InputId == 10_000,
            "constant-memory mailbox retains only newest input");
        var drained = slot.GetSnapshot();
        Check.True(
            !drained.HasItem &&
            drained.Accepted == 10_000 &&
            drained.Replaced == 9_999 &&
            drained.Taken == 1,
            "bounded mailbox drains without hidden backlog");
    }

    private static void CheckProtocolMtuBudget()
    {
        Check.True(
            SecureRealtimeMovementProtocol.MovementInputBytes <=
                SecureUdpProtectedConstants.MaximumPayloadBytes &&
            SecureRealtimeMovementProtocol.PositionSnapshotBytes <=
                SecureUdpProtectedConstants.MaximumPayloadBytes,
            "realtime payloads fit protected UDP payload budget");
        Check.True(
            SecureUdpProtectedConstants.HeaderBytes +
            SecureRealtimeMovementProtocol.MovementInputBytes +
            SecureUdpProtectedConstants.TagBytes <=
                SecureUdpProtectedConstants.MaximumDatagramBytes &&
            SecureUdpProtectedConstants.HeaderBytes +
            SecureRealtimeMovementProtocol.PositionSnapshotBytes +
            SecureUdpProtectedConstants.TagBytes <=
                SecureUdpProtectedConstants.MaximumDatagramBytes &&
            SecureUdpProtectedConstants.MaximumDatagramBytes == 1_200,
            "protected realtime datagrams stay within 1200-byte MTU");
    }

    private static DeterministicMovementNetwork CreateImpairedNetwork() =>
        new(
            seed: 0x00C0FFEE,
            baseLatencyMilliseconds: 60,
            jitterMilliseconds: 20,
            udpBlockedUntil: TimeSpan.FromMilliseconds(100),
            burstLossFirstInputId: 6,
            burstLossLastInputId: 8);

    private static EmulatedMovementSend PopulateImpairedNetwork(
        DeterministicMovementNetwork network)
    {
        network.Send(
            WireInput(
                SecureRealtimeTransportSource.Udp,
                epoch: 1,
                inputId: 1),
            SecureRealtimeTransportSource.Udp,
            TimeSpan.Zero);
        network.Send(
            WireInput(
                SecureRealtimeTransportSource.Udp,
                epoch: 1,
                inputId: 2),
            SecureRealtimeTransportSource.Udp,
            TimeSpan.FromMilliseconds(100));
        network.Send(
            WireInput(
                SecureRealtimeTransportSource.Udp,
                epoch: 1,
                inputId: 3),
            SecureRealtimeTransportSource.Udp,
            TimeSpan.FromMilliseconds(150));
        network.Send(
            WireInput(
                SecureRealtimeTransportSource.Udp,
                epoch: 1,
                inputId: 4),
            SecureRealtimeTransportSource.Udp,
            TimeSpan.FromMilliseconds(200),
            forcedAdditionalDelayMilliseconds: 120);
        network.Send(
            WireInput(
                SecureRealtimeTransportSource.Udp,
                epoch: 1,
                inputId: 5),
            SecureRealtimeTransportSource.Udp,
            TimeSpan.FromMilliseconds(250));
        for (ulong inputId = 6; inputId <= 8; inputId++)
        {
            network.Send(
                WireInput(
                    SecureRealtimeTransportSource.Udp,
                    epoch: 1,
                    inputId),
                SecureRealtimeTransportSource.Udp,
                TimeSpan.FromMilliseconds(
                    checked((long)inputId * 50)));
        }
        var duplicate = network.Send(
            WireInput(
                SecureRealtimeTransportSource.Udp,
                epoch: 1,
                inputId: 9),
            SecureRealtimeTransportSource.Udp,
            TimeSpan.FromMilliseconds(450),
            duplicateAdditionalDelayMilliseconds: 100);
        network.Send(
            WireInput(
                SecureRealtimeTransportSource.Udp,
                epoch: 1,
                inputId: 10),
            SecureRealtimeTransportSource.Udp,
            TimeSpan.FromMilliseconds(700));
        return duplicate;
    }

    private static string Signature(
        IReadOnlyList<EmulatedMovementDelivery> deliveries) =>
        string.Join(
            "|",
            deliveries.Select(static delivery =>
                $"{delivery.PacketIdentity}:{delivery.LogicalInputId}:" +
                $"{delivery.DeliveredAt.Ticks}"));

    private static int IndexOfLogicalInput(
        IReadOnlyList<EmulatedMovementDelivery> deliveries,
        ulong logicalInputId)
    {
        for (var index = 0; index < deliveries.Count; index++)
        {
            if (deliveries[index].LogicalInputId == logicalInputId)
            {
                return index;
            }
        }

        return -1;
    }
}
