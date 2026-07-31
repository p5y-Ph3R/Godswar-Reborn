namespace Godswar.Server.Game;

internal sealed partial class GameClientHandler
{
    private void JoinCurrentWorld(
        bool worldReady)
    {
        if (_character is null)
        {
            throw new InvalidOperationException(
                "A character is required before joining a world.");
        }

        var accountId = _account?.Id ?? _character.AccountId;
        var admission = _session.GatewayWorldAdmission;
        if (admission is null)
        {
            _registry.JoinMap(
                _session,
                accountId,
                _character,
                WorldObjectIds.ForPlayer(_character.Id),
                worldReady);
            return;
        }

        _registry.JoinGatewayWorld(
            _session,
            accountId,
            _character,
            WorldObjectIds.ForPlayer(_character.Id),
            admission,
            worldReady);
    }

    private bool ValidateGatewayAdmission()
    {
        var admission = _session.GatewayWorldAdmission;
        if (admission is null)
        {
            return true;
        }

        if (_account is null ||
            admission.AccountId != _account.Id ||
            !_registry.AcceptsGatewayAdmission(admission))
        {
            return false;
        }

        if (_character is null)
        {
            return admission.CharacterId == 0;
        }

        return admission.CharacterId == _character.Id &&
            admission.MapId.TryGetLegacyValue(out var mapId) &&
            mapId == _character.CurrentMap;
    }
}
