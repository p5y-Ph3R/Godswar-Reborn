using Godswar.Server.Domain.World.Content;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private WarehouseAccessContext? _warehouseAccessContext;
    private int _warehouseSelectedPage;

    private void IssueWarehouseAccess(NpcSpawnDefinition npc)
    {
        if (_account is null || _character is null)
        {
            _warehouseAccessContext = null;
            _warehouseSelectedPage = 0;
            return;
        }

        _warehouseAccessContext = new WarehouseAccessContext(
            _account.Id,
            _character.Id,
            _processRealmId.Value,
            _character.CurrentMap,
            npc.InteractionId,
            DateTimeOffset.UtcNow + WarehouseAccessContext.Lifetime);
        _warehouseSelectedPage = 0;
    }

    private bool TryAuthorizeWarehouseTransfer(
        out NpcSpawnDefinition npc)
    {
        npc = default!;
        var context = _warehouseAccessContext;
        var now = DateTimeOffset.UtcNow;
        if (context is null ||
            _account is null ||
            _character is null ||
            !context.Matches(
                _account.Id,
                _character.Id,
                _processRealmId.Value,
                _character.CurrentMap,
                now) ||
            !TryResolveMapNpc(context.NpcInteractionId, out npc) ||
            !WarehouseNpcProtocol.IsWarehouseEndpoint(
                npc.NpcKey,
                npc.InteractionId))
        {
            _warehouseAccessContext = null;
            _warehouseSelectedPage = 0;
            return false;
        }

        _warehouseAccessContext = context with
        {
            ExpiresAt = now + WarehouseAccessContext.Lifetime
        };
        return true;
    }
}
