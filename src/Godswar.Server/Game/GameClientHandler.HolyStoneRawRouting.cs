using Godswar.Server.Application.Inventory;
using Godswar.Server.Packets;
using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<RawHolyStoneClassification>
        ClassifyRawHolyStoneBoundaryAsync(
            GamePacket packet,
            uint npcId,
            int dialogIndex,
            int subId,
            CancellationToken cancellationToken)
    {
        var rawUpgradeIntent = HolyStoneProtocol.PendingUpgradeIntent();
        var rawUpgradeCommit =
            !packet.ClientOperationId.HasValue &&
            subId == HolyStoneProtocol.UpgradeSubId &&
            HolyStoneProtocol.IsExactUpgradeBoundary(packet) &&
            TryResolveRawUpgradeSelections(
                npcId,
                dialogIndex,
                rawUpgradeIntent,
                out rawUpgradeIntent);
        var rawCombinationIntent =
            HolyStoneProtocol.PendingCombinationIntent();
        var rawCombinationCommit =
            !packet.ClientOperationId.HasValue &&
            subId == HolyStoneProtocol.CombineSubId &&
            !HolyStoneProtocol.IsExactPageNavigation(packet) &&
            HolyStoneProtocol.IsExactCombinationBoundary(packet) &&
            TryResolveRawCombinationSelections(
                npcId,
                dialogIndex,
                rawCombinationIntent,
                out rawCombinationIntent);
        var rawImplementIntent =
            HolyStoneProtocol.PendingImplementSpiritIntent();
        var rawImplementCommit =
            !packet.ClientOperationId.HasValue &&
            subId == HolyStoneProtocol.ImplementSpiritSubId &&
            HolyStoneProtocol.IsExactImplementSpiritBoundary(packet) &&
            TryResolveRawImplementSpiritSelections(
                npcId,
                dialogIndex,
                rawImplementIntent,
                out rawImplementIntent);
        var exactNavigation =
            !packet.ClientOperationId.HasValue &&
            !rawUpgradeCommit &&
            !rawCombinationCommit &&
            !rawImplementCommit &&
            HolyStoneProtocol.IsExactPageNavigation(packet);
        var exactNpcId = 0u;
        var exactDialogIndex = 0;
        var exactIntent = default(HolyStoneWireIntent);
        var exactMutation =
            rawUpgradeCommit ||
            rawCombinationCommit ||
            rawImplementCommit ||
            !packet.ClientOperationId.HasValue &&
            subId is not (
                HolyStoneProtocol.UpgradeSubId or
                HolyStoneProtocol.CombineSubId or
                HolyStoneProtocol.ImplementSpiritSubId) &&
            HolyStoneProtocol.TryReadMutation(
                packet,
                out exactNpcId,
                out exactDialogIndex,
                out exactIntent) &&
            exactNpcId == npcId &&
            exactDialogIndex == dialogIndex;

        if (rawUpgradeCommit)
        {
            // Capture and consume the exact clear snapshot before any
            // asynchronous NPC-content lookup. A slow content read cannot
            // expire the one-second native clear correlation after we have
            // already accepted this one-shot boundary.
            exactIntent = rawUpgradeIntent;
        }
        else if (rawCombinationCommit)
        {
            // The exact ordered four-slot snapshot is consumed before the
            // asynchronous dialogue lookup and carried as the resolved intent.
            exactIntent = rawCombinationIntent;
        }
        else if (rawImplementCommit)
        {
            exactIntent = rawImplementIntent;
        }
        if (exactNavigation)
        {
            return new RawHolyStoneClassification(true, null);
        }
        if (exactMutation)
        {
            return new RawHolyStoneClassification(true, exactIntent);
        }

        await _session.SendAsync(
            PacketBuilder.NpcFunctionActionResponse(
                npcId,
                dialogIndex,
                HolyStoneNativeResults.WrongSelectionSubId),
            cancellationToken,
            "NpcFunctionActionResponse");
        return new RawHolyStoneClassification(false, null);
    }

    private readonly record struct RawHolyStoneClassification(
        bool Accepted,
        HolyStoneWireIntent? Intent);
}
