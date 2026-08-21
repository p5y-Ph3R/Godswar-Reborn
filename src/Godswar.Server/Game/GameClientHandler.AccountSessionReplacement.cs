using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task PublishReplacedWorldRemovalAsync(
        DetachedPlayerWorldSession? detached)
    {
        if (detached is null)
        {
            return;
        }

        try
        {
            var context = detached.Context;
            var recipients =
                await _registry.BroadcastToWorldInstanceAsync(
                    context.WorldInstanceId,
                    PacketBuilder.RemoveWorldObjects(
                        context.ObjectId),
                    CancellationToken.None,
                    context.Session,
                    "ReplacedWorldObjectRemove");
            if (recipients > 0)
            {
                Console.WriteLine(
                    $"[world] broadcast replaced-session leave " +
                    $"instance={context.WorldInstanceId} " +
                    $"map={context.MapId} " +
                    $"character={context.DisplayName} " +
                    $"object={context.ObjectId} " +
                    $"recipients={recipients}");
            }
        }
        catch (Exception error)
        {
            Console.WriteLine(
                _session.AllowsPayloadDiagnostics
                    ? $"[world] replaced-session leave failed: " +
                        error.Message
                    : "[world] replaced-session leave failed");
        }
        finally
        {
            _registry.ReleaseDetachedPlayerWorld(detached);
        }
    }
}
