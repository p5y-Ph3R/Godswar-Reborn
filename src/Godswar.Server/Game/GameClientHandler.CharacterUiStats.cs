using Godswar.Server.Packets;

namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task HandleCharacterUiStatsV1ProbeAsync(
        ZodiacSyncRequest request,
        CancellationToken cancellationToken)
    {
        if (_character is null ||
            !request.IsCanonicalCharacterUiStatsV1Probe ||
            !TryCaptureCurrentPlayerOwnership(out var ownership))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!_registry.TryAcceptCharacterUiStatsV1CapabilityProbe(
                _session,
                now))
        {
            return;
        }

        var aggregate = ApplyElementalMovementStatus(
            _registry.GetRuntimeStatusAggregate(_session, now),
            now);
        var projection = GameSessionRegistry.ProjectCharacterUiStatsV1(
            _character,
            aggregate);
        if (!RevalidateCurrentPlayerOwnership(ownership))
        {
            return;
        }

        await _session.SendAsync(
            PacketBuilder.CharacterUiStatsV1(projection),
            cancellationToken,
            "CharacterUiStatsV1");
    }
}
