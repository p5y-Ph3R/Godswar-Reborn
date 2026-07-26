using System.Net.Sockets;
using Godswar.Server.Networking.Secure.Realtime;

namespace Godswar.Server.Networking.Secure.Udp;

internal sealed partial class SecureUdpEndpointServer
{
    private async Task RunRealtimeSnapshotEgressAsync(
        Socket socket,
        CancellationToken cancellationToken)
    {
        var payload = new byte[
            SecureRealtimeMovementProtocol.PositionSnapshotBytes];
        var datagram = new byte[
            SecureUdpProtectedConstants.MaximumDatagramBytes];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var dispatch =
                    await _sessions!.WaitForRealtimeSnapshotAsync(
                        cancellationToken);
                if (dispatch is null)
                {
                    return;
                }

                payload.AsSpan().Clear();
                datagram.AsSpan().Clear();
                if (!SecureRealtimeMovementProtocol
                        .TryEncodePositionSnapshot(
                            dispatch.Value.Snapshot,
                            payload,
                            out var payloadBytes) ||
                    payloadBytes != payload.Length ||
                    !_sessions.TryProtect(
                        dispatch.Value.ConnectionId,
                        dispatch.Value.RemoteEndpoint,
                        dispatch.Value.BindingRevision,
                        SecureUdpProtectedMessageType.PositionSnapshot,
                        payload,
                        datagram,
                        out var datagramBytes,
                        out _,
                        out _) ||
                    datagramBytes <= 0 ||
                    datagramBytes > _maximumDatagramBytes)
                {
                    SecureUdpMetrics.RecordOutcome(
                        SecureUdpDatagramOutcome
                            .RealtimeMovementRejected);
                    continue;
                }

                try
                {
                    var sent = await socket.SendToAsync(
                        datagram.AsMemory(0, datagramBytes),
                        SocketFlags.None,
                        dispatch.Value.RemoteEndpoint,
                        cancellationToken);
                    if (sent != datagramBytes)
                    {
                        SecureUdpMetrics.RecordOutcome(
                            SecureUdpDatagramOutcome.TransportError);
                        continue;
                    }

                    SecureUdpMetrics.DatagramSent(sent);
                    SecureUdpMetrics.RecordOutcome(
                        SecureUdpDatagramOutcome
                            .RealtimeSnapshotSent);
                }
                catch (SocketException)
                {
                    SecureUdpMetrics.RecordOutcome(
                        SecureUdpDatagramOutcome.TransportError);
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            payload.AsSpan().Clear();
            datagram.AsSpan().Clear();
        }
    }

    private static async Task AwaitRealtimeSnapshotEgressAsync(
        Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
