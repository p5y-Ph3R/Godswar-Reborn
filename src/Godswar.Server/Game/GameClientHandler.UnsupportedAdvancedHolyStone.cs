using Godswar.Server.Application.Inventory;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task RejectUnsupportedAdvancedHolyStoneAsync(
        GamePacket packet,
        uint npcId,
        int dialogIndex,
        CancellationToken cancellationToken)
    {
        // Action 701 is named "Equipment Advance Drilling" by the original
        // client, but no captured client-to-server commit establishes its
        // argument roles. Never infer an item-consuming mutation here.
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                HolyStoneNativeResults.WrongSelectionSubId),
            cancellationToken,
            "NpcFunctionActionResponse");

        var shape = HolyStoneProtocol.IsExactAdvancedDrillNavigation(
            packet)
            ? "page_transition"
            : "unknown_value_shape";
        Console.WriteLine(
            "[holy-stone] advanced drill rejected " +
            $"npc={npcId} dialog={dialogIndex} shape={shape} " +
            "reason=missing_c2s_wire_capture");
    }
}
