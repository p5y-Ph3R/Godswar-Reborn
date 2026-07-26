using Godswar.Server.Networking.Secure.Realtime;
using Godswar.Server.Networking.Secure.Udp;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private readonly SecurePhase4AcceptanceFaults?
        _phase4AcceptanceFaults;

    private bool ShouldForcePhase4AcceptanceCorrection(
        in SecureRealtimeMovementIngress ingress)
    {
        var context = _session.SecureConnectionContext;
        return _phase4AcceptanceFaults is not null &&
            context is not null &&
            SecureUdpConnectionKey.TryCreate(
                context.ConnectionId.Span,
                out var connectionId) &&
            _phase4AcceptanceFaults.ShouldForceCorrection(
                connectionId,
                ingress);
    }

    private void ConfirmPhase4AcceptanceCorrectionWrite(
        ulong inputId)
    {
        var context = _session.SecureConnectionContext;
        if (_phase4AcceptanceFaults is null ||
            context is null ||
            !SecureUdpConnectionKey.TryCreate(
                context.ConnectionId.Span,
                out var connectionId) ||
            !_phase4AcceptanceFaults.ConfirmReliableCorrectionWrite(
                connectionId,
                inputId))
        {
            throw new InvalidOperationException(
                "Acceptance correction write confirmation was rejected.");
        }
    }
}
