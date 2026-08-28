using System.Buffers.Binary;
using Godswar.Server.Domain.World.Content;
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
            _warehouseAccessContext = null;
            Console.WriteLine("[npc] dialog open ignored: payload too short");
            return;
        }

        ClearGearEnhancerSelection();
        ClearInstanceCallerPageContext();
        var npcId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload[..sizeof(uint)]);
        if (!TryResolveMapNpc(npcId, out var npc))
        {
            _warehouseAccessContext = null;
            Console.WriteLine(
                $"[npc] dialog open ignored: unknown npc={npcId} " +
                $"map={_character?.CurrentMap.ToString() ?? "<none>"}");
            return;
        }

        // The stock client can leave storage open while the related manager
        // dialogue is used. Every unrelated NPC click invalidates the lease.
        if (!WarehouseNpcProtocol.IsManagerEndpoint(
                npc.NpcKey,
                npc.InteractionId))
        {
            _warehouseAccessContext = null;
        }

        if (WarehouseNpcProtocol.IsWarehouseEndpoint(
                npc.NpcKey,
                npc.InteractionId))
        {
            if (packet.Length == 48 && packet.Buffer.Length == 48)
            {
                await _session.SendAsync(
                    PacketBuilder.WarehouseDialogOpenAck(
                        npc.InteractionId,
                        npc.NpcKey),
                    cancellationToken,
                    "WarehouseDialogOpenAck");
            }
            else
            {
                Console.Error.WriteLine(
                    "[warehouse] rejected non-canonical NPC click " +
                    $"npc={npc.InteractionId} length={packet.Length}");
            }
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

    private async Task HandleNpcDialogPageRequestAsync(
        GamePacket packet,
        CancellationToken cancellationToken)
    {
        if (packet.Payload.Length < sizeof(uint))
        {
            Console.WriteLine("[npc] page request ignored: payload too short");
            return;
        }

        var npcId = BinaryPrimitives.ReadUInt32LittleEndian(
            packet.Payload[..sizeof(uint)]);
        if (!TryResolveMapNpc(npcId, out var npc))
        {
            Console.WriteLine(
                $"[npc] page request ignored: unknown npc={npcId}");
            return;
        }

        if (WarehouseNpcProtocol.IsWarehouseEndpoint(
                npc.NpcKey,
                npc.InteractionId))
        {
            if (packet.Length == 8 && packet.Buffer.Length == 8)
            {
                await HandleWarehouseOpenAsync(npc, cancellationToken);
            }
            else if (packet.Length == 12 &&
                     packet.Buffer.Length == 12 &&
                     TryAuthorizeWarehouseTransfer(out var authorizedNpc) &&
                     authorizedNpc.InteractionId == npc.InteractionId)
            {
                var page = BinaryPrimitives.ReadInt32LittleEndian(
                    packet.Payload.Slice(sizeof(uint), sizeof(int)));
                await HandleWarehouseOpenAsync(
                    npc,
                    cancellationToken,
                    page,
                    issueAccess: false);
            }
            else
            {
                Console.Error.WriteLine(
                    "[warehouse] rejected non-canonical page request " +
                    $"npc={npc.InteractionId} length={packet.Length}");
            }
            return;
        }

        Console.WriteLine(
            $"[npc] page request npc={npcId} key={npc.NpcKey}");
    }
}
