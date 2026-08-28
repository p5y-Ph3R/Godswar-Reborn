namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private Task PublishPartyPositionRefreshAsync(
        CancellationToken cancellationToken) =>
        PublishPartyDeliveriesAsync(
            _registry.GetPartyRefreshDeliveries(_session),
            cancellationToken);
}
