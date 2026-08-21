namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private uint CurrentPlayerObjectId =>
        _registry.GetRequiredPlayerObjectId(_session);
}
