namespace Godswar.Server.Networking.Secure.Realtime;

internal sealed class SecureRealtimeSessionState : IDisposable
{
    public SecureRealtimeSingleSlot<SecureRealtimeMovementIngress>
        MovementIngress { get; } = new();

    public SecureRealtimeSingleSlot<SecureRealtimePositionSnapshot>
        SnapshotEgress { get; } = new();

    public SecureRealtimeTransportState Transport { get; } = new();

    public void Dispose()
    {
        MovementIngress.Dispose();
        SnapshotEgress.Dispose();
    }
}
