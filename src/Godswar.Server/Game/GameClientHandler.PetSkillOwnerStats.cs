using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task<bool> SendPetSkillOwnerStatRefreshAsync(
        string reason,
        CancellationToken cancellationToken)
    {
        if (_character is null)
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        var status = await _registry.GetStatusSnapshotAsync(
            _session,
            now,
            cancellationToken);
        await _session.SendAsync(
            PacketBuilder.PlayerStatusEffects(
                _character,
                status.Effects,
                status.Aggregate),
            cancellationToken,
            $"{reason}ExtendedStatus");
        await _session.SendAsync(
            BuildLocalPlayerStatusUpdateAt(
                status.Aggregate,
                now),
            cancellationToken,
            $"{reason}GameData");
        return true;
    }
}
