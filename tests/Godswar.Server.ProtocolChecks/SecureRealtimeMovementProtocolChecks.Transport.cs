using Godswar.Server.Networking.Secure.Realtime;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureRealtimeMovementProtocolChecks
{
    private static void CheckTransportReconciliation()
    {
        var state = new SecureRealtimeTransportState();
        Check.True(
            state.Reconcile(
                SecureRealtimeTransportSource.Udp,
                2,
                1).Status ==
                    SecureRealtimeReconciliationStatus
                        .TransportEpochRejected,
            "first transport epoch must be one");
        Check.True(
            state.Reconcile(
                SecureRealtimeTransportSource.Udp,
                1,
                10).ShouldEnqueue &&
            state.Reconcile(
                SecureRealtimeTransportSource.Udp,
                1,
                10).Status ==
                    SecureRealtimeReconciliationStatus.Duplicate &&
            state.Reconcile(
                SecureRealtimeTransportSource.Udp,
                1,
                9).Status ==
                    SecureRealtimeReconciliationStatus.StaleInput &&
            state.Reconcile(
                SecureRealtimeTransportSource.Tls,
                1,
                11).Status ==
                    SecureRealtimeReconciliationStatus
                        .TransportSourceRejected,
            "same-epoch dedupe and source ownership are explicit");

        var fallback = state.Reconcile(
            SecureRealtimeTransportSource.Tls,
            2,
            10);
        Check.True(
            fallback.Status ==
                SecureRealtimeReconciliationStatus
                    .TransportChangedDuplicate &&
            fallback.CurrentTransportEpoch == 2 &&
            fallback.CurrentTransportSource ==
                SecureRealtimeTransportSource.Tls &&
            state.Reconcile(
                SecureRealtimeTransportSource.Tls,
                2,
                12).ShouldEnqueue &&
            state.Reconcile(
                SecureRealtimeTransportSource.Udp,
                3,
                13).Status ==
                    SecureRealtimeReconciliationStatus
                        .TransportEpochRejected,
            "one duplicate-safe UDP-to-TLS fallback is allowed without switchback");

        var lostFirstUdp = new SecureRealtimeTransportState();
        var directFallback = lostFirstUdp.Reconcile(
            SecureRealtimeTransportSource.Tls,
            2,
            50);
        Check.True(
            directFallback.ShouldEnqueue &&
            directFallback.CurrentTransportEpoch == 2 &&
            directFallback.CurrentTransportSource ==
                SecureRealtimeTransportSource.Tls &&
            lostFirstUdp.GetSnapshot().TransportChanges == 1 &&
            lostFirstUdp.Reconcile(
                SecureRealtimeTransportSource.Udp,
                3,
                51).Status ==
                    SecureRealtimeReconciliationStatus
                        .TransportEpochRejected,
            "lost first UDP input may cut directly to TLS epoch two without switchback");

        var tlsFirst = new SecureRealtimeTransportState();
        Check.True(
            tlsFirst.Reconcile(
                SecureRealtimeTransportSource.Tls,
                1,
                70).ShouldEnqueue &&
            tlsFirst.Reconcile(
                SecureRealtimeTransportSource.Udp,
                2,
                71).Status ==
                    SecureRealtimeReconciliationStatus
                        .TransportSourceRejected,
            "Phase 4 transport ownership never switches back from TLS to UDP");
    }
}
