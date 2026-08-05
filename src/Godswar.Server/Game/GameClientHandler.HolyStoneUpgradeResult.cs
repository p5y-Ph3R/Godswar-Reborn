using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task SendHolyStoneUpgradeResultPanelAsync(
        uint npcId,
        int dialogIndex,
        int nativeResultSubId,
        CancellationToken cancellationToken)
    {
        // The stock client expects one ordered response. Sub-ID 3100 first
        // rebuilds the three upgrade inputs, while the following result sub-ID
        // writes the outcome into the panel's reserved result text control.
        PrepareHolyStoneSelectionContext(
            npcId,
            dialogIndex,
            HolyStoneProtocol.UpgradeSubId);
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                HolyStoneProtocol.UpgradeResultPanelSubId,
                nativeResultSubId),
            cancellationToken,
            "HolyStoneUpgradeResultPanel");
        // NpcFunEment sub-ID 3100 exposes A3 instead of the initial page's A1.
        // Stock A3 sends action 401 before its subsequent 10193 clear burst,
        // so only this successfully-sent result page may commit from current
        // staged selections.
        _gearEnhancerSelectionContext?.AllowPostResultRawUpgradeCommit();
    }
}
