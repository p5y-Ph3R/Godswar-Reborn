using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private byte[] BuildLocalPlayerStatusUpdate()
    {
        if (_character is null)
        {
            throw new InvalidOperationException(
                "A local player status update requires an active character.");
        }

        // A mounted player must keep the same locomotion multiplier on every
        // later 10166 refresh. Sending the packet builder's walking default
        // after forging, progression, equipment, or inspection silently
        // cancels the client's mount-speed change.
        var status = _registry.GetRuntimeStatusAggregate(
            _session,
            DateTimeOffset.UtcNow);
        return PacketBuilder.PlayerStatusUpdate(_character, status);
    }
}
