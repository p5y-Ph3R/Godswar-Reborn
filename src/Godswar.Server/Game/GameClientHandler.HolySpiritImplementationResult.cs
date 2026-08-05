using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task SendHolySpiritImplementationResultPanelAsync(
        uint npcId,
        int dialogIndex,
        int nativeResultSubId,
        CancellationToken cancellationToken)
    {
        PrepareHolyStoneSelectionContext(
            npcId,
            dialogIndex,
            HolyStoneProtocol.ImplementSpiritSubId);
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                HolyStoneProtocol.ImplementSpiritResultPanelSubId,
                nativeResultSubId),
            cancellationToken,
            "HolySpiritImplementationResultPanel");

        // Native sub-ID 3200 exposes A3, whose action precedes its clear
        // burst. Arm exactly one result-page retry using the current slots.
        _gearEnhancerSelectionContext?
            .AllowPostResultRawUpgradeCommit();
    }
}
