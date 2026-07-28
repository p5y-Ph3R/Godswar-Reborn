using System.Net;
using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureRealtimeSessionAuthorityChecks
{
    private static void
        CheckQueuedTransitionPreservesUnappliedInput()
    {
        var time = new ManualTimeProvider();
        time.Advance(TimeSpan.FromSeconds(1));
        using var authority = CreateAuthority(
            gameplayMovementEnabled: true,
            capacity: 1,
            time);
        using var registration = Register(
            authority,
            connectionSeed: 97);
        var grant = CopyGrant(registration.Lease);
        try
        {
            var revision = Bind(
                authority,
                grant,
                new IPEndPoint(IPAddress.Loopback, 45_097));
            time.Advance(TimeSpan.FromMilliseconds(100));
            var udpInput =
                SecureRealtimeMovementProtocolChecks.CreateInput(
                    epoch: 1,
                    inputId: 700);
            Check.True(
                authority.OfferUdpMovement(
                    SecureUdpConnectionKeyFrom(
                        grant.ConnectionId),
                    revision,
                    udpInput).IsAccepted,
                "unapplied UDP input enters capacity-one ingress");

            time.Advance(TimeSpan.FromMilliseconds(200));
            var changedRetry = udpInput with
            {
                Flags =
                    SecureRealtimeMovementFlags.CurrentWorld,
                TransportEpoch = 2,
                X = udpInput.X + 500f
            };
            var handoff = registration.Lease.OfferTlsMovement(
                EncodeTlsInput(changedRetry));
            Check.True(
                handoff.Status ==
                    SecureRealtimeMovementOfferStatus
                        .TransportChangedDuplicate &&
                registration.Lease.TryTakeRealtimeMovement(
                    out var transition) &&
                transition.Kind ==
                    SecureRealtimeMovementIngressKind
                        .TransportTransition &&
                transition.TransportSource ==
                    SecureRealtimeTransportSource.Tls &&
                transition.Input.TransportEpoch == 2 &&
                transition.Input.InputId == udpInput.InputId &&
                transition.Input.X == udpInput.X &&
                transition.ServerReceiveElapsed ==
                    TimeSpan.FromMilliseconds(100),
                "duplicate handoff preserves the first authenticated intent until simulation consumes it");
        }
        finally
        {
            grant.Clear();
        }
    }

    private static void CheckExpiredUdpOfferRetainsTlsFallback()
    {
        var time = new ManualTimeProvider();
        time.Advance(TimeSpan.FromSeconds(1));
        using var authority = CreateAuthority(
            gameplayMovementEnabled: true,
            capacity: 1,
            time);
        using var registration = Register(
            authority,
            connectionSeed: 113);
        time.Advance(TimeSpan.FromSeconds(31));
        Check.True(
            !registration.Lease.TryCopyGrantMaterial(
                stackalloc byte[16],
                stackalloc byte[32],
                out _),
            "expired UDP binding proof is not exposed");
        var fallback =
            SecureRealtimeMovementProtocolChecks.CreateInput(
                SecureRealtimeMovementFlags.CurrentWorld,
                epoch: 2,
                inputId: 900);
        Check.True(
            registration.Lease.OfferTlsMovement(
                EncodeTlsInput(fallback)).IsAccepted &&
            registration.Lease.TryTakeRealtimeMovement(
                out var ingress) &&
            ingress.Input == fallback,
            "live TLS lease retains bounded fallback after UDP offer expiry");
    }

    private static void CheckTlsFallbackSurvivesUdpIdleCleanup()
    {
        var time = new ManualTimeProvider();
        time.Advance(TimeSpan.FromSeconds(1));
        using var authority = CreateAuthority(
            gameplayMovementEnabled: true,
            capacity: 1,
            time);
        using var registration = Register(
            authority,
            connectionSeed: 127);
        var grant = CopyGrant(registration.Lease);
        try
        {
            var connectionId =
                SecureUdpConnectionKeyFrom(grant.ConnectionId);
            var revision = Bind(
                authority,
                grant,
                new IPEndPoint(IPAddress.Loopback, 45_127));
            var udpInput =
                SecureRealtimeMovementProtocolChecks.CreateInput(
                    epoch: 1,
                    inputId: 1_000);
            Check.True(
                authority.OfferUdpMovement(
                    connectionId,
                    revision,
                    udpInput).IsAccepted &&
                registration.Lease.TryTakeRealtimeMovement(out _),
                "bound UDP input establishes realtime ownership");

            var fallback = udpInput with
            {
                TransportEpoch = 2,
                InputId = 1_001
            };
            Check.True(
                registration.Lease.OfferTlsMovement(
                    EncodeTlsInput(fallback)).IsAccepted &&
                registration.Lease.TryTakeRealtimeMovement(out _),
                "TLS fallback takes one-way realtime ownership");

            time.Advance(TimeSpan.FromSeconds(31));
            var nextTlsInput = fallback with
            {
                InputId = 1_002
            };
            Check.True(
                authority.CleanupExpiredSessions() == 0 &&
                authority.GetSnapshot().TrackedSessions == 1 &&
                registration.Lease.OfferTlsMovement(
                    EncodeTlsInput(nextTlsInput)).IsAccepted &&
                registration.Lease.TryTakeRealtimeMovement(
                    out var ingress) &&
                ingress.Input == nextTlsInput &&
                ingress.TransportSource ==
                    SecureRealtimeTransportSource.Tls,
                "TLS-owned fallback survives the obsolete UDP idle deadline");

            registration.Lease.Dispose();
            Check.True(
                authority.GetSnapshot().TrackedSessions == 0,
                "TLS lease release removes the retained authority entry");
        }
        finally
        {
            grant.Clear();
        }
    }
}
