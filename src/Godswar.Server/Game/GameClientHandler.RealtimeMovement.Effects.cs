namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private async Task PublishRealtimeMovementEffectsAsync(
        RealtimeMovementEffects effects,
        CancellationToken cancellationToken)
    {
        if (effects.ReliableCorrection is not null)
        {
            if (!RevalidateCurrentWorldEffectOwnership(
                    "realtime_correction_publish"))
            {
                return;
            }
            await _session.SendAsync(
                effects.ReliableCorrection,
                cancellationToken,
                "RealtimeMovementCorrection");
            if (effects.AcceptanceCorrectionInputId is { } inputId)
            {
                ConfirmPhase4AcceptanceCorrectionWrite(inputId);
            }
        }

        if (effects.ViewerMovement is not null)
        {
            if (!RevalidateCurrentWorldEffectOwnership(
                    "realtime_movement_broadcast"))
            {
                return;
            }
            await _registry.BroadcastToMapAsync(
                effects.MapId,
                effects.ViewerMovement,
                cancellationToken,
                _session,
                "RealtimeMovementWorld");
            await PublishPartyPositionRefreshAsync(cancellationToken);
        }

        if (effects.PositionSave is not { } save)
        {
            return;
        }
        if (!RevalidateCurrentWorldEffectOwnership(
                "realtime_position_save"))
        {
            return;
        }
        if (_account is not null &&
            _account.Id == save.AccountId &&
            _character is not null &&
            _character.Id == save.CharacterId)
        {
            await PersistPositionCheckpointAsync(
                _character,
                save.MapId,
                save.X,
                save.Z,
                save.Revision,
                force: false,
                cancellationToken);
        }
    }
}
