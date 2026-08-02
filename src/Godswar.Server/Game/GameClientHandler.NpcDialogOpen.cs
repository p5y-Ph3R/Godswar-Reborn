using System.Buffers.Binary;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleNpcDialogOpenAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Payload.Length < sizeof(uint))
        {
            Console.WriteLine("[npc] dialog open ignored: payload too short");
            return;
        }

        ClearGearEnhancerSelection();
        var npcId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload[..sizeof(uint)]);
        if (!TryResolveMapNpc(npcId, out var npc))
        {
            Console.WriteLine(
                $"[npc] dialog open ignored: unknown npc={npcId} " +
                $"map={_character?.CurrentMap.ToString() ?? "<none>"}");
            return;
        }

        var routes = await ResolveNpcDialogueRoutesAsync(
            npc,
            cancellationToken);
        if (routes.Count == 0)
        {
            return;
        }

        var clientScriptKey = routes[0].ClientScriptKey;
        if (routes.Count > 3 || routes.Any(route =>
                !string.Equals(
                    route.ClientScriptKey,
                    clientScriptKey,
                    StringComparison.Ordinal)))
        {
            Console.Error.WriteLine(
                "[npc] dialog open rejected: routes cannot be represented " +
                $"npc={npc.InteractionId} routes={routes.Count}");
            return;
        }

        // One native advertisement carries up to three ordered top-level
        // functions in a base-1000 field. For Gear Mentor, [4, 37] becomes
        // 37004, so Gear Enhancement and Class Suit are sibling choices.
        var dialogIndices = routes
            .Select(static route => route.DialogIndex)
            .ToArray();
        await _session.SendAsync(
            PacketBuilder.NpcDialogOpenAck(
                npc.InteractionId,
                dialogIndices,
                clientScriptKey),
            cancellationToken,
            "NpcDialogOpenAck");

        foreach (var route in routes)
        {
            Console.WriteLine(
                $"[npc] dialog open npc={npc.InteractionId} " +
                $"script={route.ClientScriptKey} " +
                $"behavior={route.Behavior} dialog={route.DialogIndex} " +
                $"order={route.RouteOrder}");
        }
    }

    private Task HandleNpcDialogPageRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (packet.Payload.Length < sizeof(uint))
        {
            Console.WriteLine("[npc] page request ignored: payload too short");
            return Task.CompletedTask;
        }

        var npcId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload[..sizeof(uint)]);
        Console.WriteLine(
            TryResolveMapNpc(npcId, out var npc)
                ? $"[npc] page request npc={npcId} key={npc.NpcKey}"
                : $"[npc] page request ignored: unknown npc={npcId}");
        return Task.CompletedTask;
    }
}
