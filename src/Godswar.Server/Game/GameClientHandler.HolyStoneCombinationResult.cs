using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task SendHolyStoneCombinationResultPanelAsync(
        uint npcId,
        int dialogIndex,
        int nativeResultSubId,
        CancellationToken cancellationToken)
    {
        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                HolyStoneProtocol.CombineResultPanelSubId,
                nativeResultSubId),
            cancellationToken,
            "HolyStoneCombinationResultPanel");

        if (_account is null || _character is null)
        {
            return;
        }

        // Sub-ID 3300 exposes A3, which sends action 601 before its four
        // subsequent clear packets. Arm that live-selection order only after
        // the result panel was sent successfully.
        ClearGearEnhancerSelection();
        _holyStoneCombinationSelectionContext =
            new HolyStoneCombinationSelectionContext(
                _account.Id,
                _character.Id,
                npcId,
                dialogIndex,
                DateTimeOffset.UtcNow +
                    GearEnhancerProtocol.SelectionContextLifetime);
        _holyStoneCombinationSelectionContext.AllowPostResultCommit();
    }
}
