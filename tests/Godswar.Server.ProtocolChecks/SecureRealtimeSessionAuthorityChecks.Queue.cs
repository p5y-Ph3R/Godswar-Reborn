using System.Net;

namespace Godswar.Server.ProtocolChecks;

internal static partial class SecureRealtimeSessionAuthorityChecks
{
    private static void CheckSnapshotReadyQueueBound()
    {
        using var authority = CreateAuthority(
            gameplayMovementEnabled: true,
            capacity: 1);
        using var first = Register(authority, connectionSeed: 49);
        var firstGrant = CopyGrant(first.Lease);
        try
        {
            Bind(
                authority,
                firstGrant,
                new IPEndPoint(IPAddress.Loopback, 45_049));
            Check.True(
                first.Lease.TryPublishRealtimeSnapshot(
                    InitialSnapshot()) &&
                authority.RealtimeSnapshotQueueCount == 1,
                "first snapshot-ready entry occupies one bounded slot");
        }
        finally
        {
            firstGrant.Clear();
        }
        first.Dispose();

        using var second = Register(authority, connectionSeed: 65);
        var secondGrant = CopyGrant(second.Lease);
        try
        {
            Bind(
                authority,
                secondGrant,
                new IPEndPoint(IPAddress.Loopback, 45_065));
            Check.True(
                !second.Lease.TryPublishRealtimeSnapshot(
                    InitialSnapshot()) &&
                authority.RealtimeSnapshotQueueCount == 1,
                "session churn cannot grow snapshot-ready storage past capacity");
        }
        finally
        {
            secondGrant.Clear();
        }
    }
}
