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

        var status = await _registry.GetStatusSnapshotAsync(
            _session,
            DateTimeOffset.UtcNow,
            cancellationToken);
        await _session.SendAsync(
            PacketBuilder.PlayerStatusEffects(
                _character,
                status.Effects,
                status.Aggregate),
            cancellationToken,
            $"{reason}ExtendedStatus");
        await _session.SendAsync(
            PacketBuilder.PlayerStatusUpdate(
                _character,
                status.Aggregate),
            cancellationToken,
            $"{reason}GameData");
        return true;
    }
}
