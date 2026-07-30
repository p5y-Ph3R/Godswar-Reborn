using Godswar.Server.Protocol;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool>
        TryHandleNonCanonicalSecureGearMentorPacketAsync(
            GamePacket packet,
            uint npcId,
            int dialogIndex,
            int wireSubId,
            CancellationToken cancellationToken)
    {
        if (!_session.IsSecure ||
            !packet.ClientOperationId.HasValue ||
            !ResolveSecureGearMentorCommandFamily(wireSubId).HasValue ||
            GearEnhancerProtocol.IsExactFunctionActionPacket(packet))
        {
            return false;
        }

        // A UUID may already have committed on another connection or server
        // instance. Resolve that durable outcome before settling the retry as
        // malformed. A replay miss may safely receive the finite rejection.
        if (await TryReplayDurableGearMentorBeforeRouteRejectionAsync(
                packet,
                npcId,
                wireSubId,
                cancellationToken))
        {
            return true;
        }

        await TryRejectUnroutedSecureCommandAsync(
            packet,
            npcId,
            "noncanonical_function_action_length",
            cancellationToken,
            ResolveSecureGearMentorCommandFamily(wireSubId),
            responseDialogIndex: dialogIndex);
        return true;
    }
}
