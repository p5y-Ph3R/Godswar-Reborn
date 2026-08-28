namespace Godswar.Server.Game;

internal sealed partial class MapInstance
{
    private sealed partial class MedusaInstanceOwnerBoundAggregate
    {
        public bool TryAdmitCharacter(
            int characterId,
            out bool added)
        {
            added = false;
            if (_run.IsCharacterAdmitted(characterId))
            {
                return true;
            }
            if (!_mechanics.CanAdmitCharacter(characterId) ||
                !_run.TryAdmitCharacter(
                    characterId,
                    _playerCapacity,
                    out added))
            {
                return false;
            }

            _mechanics.AdmitCharacter(characterId);
            _lateAdmittedCharacters.Add(characterId);
            return true;
        }

        public bool RollBackLateAdmission(int characterId)
        {
            if (!_lateAdmittedCharacters.Contains(characterId) ||
                !_mechanics.CanRemoveAdmittedCharacter(characterId) ||
                !_run.CanRemoveAdmittedCharacter(characterId))
            {
                return false;
            }

            _mechanics.RemoveAdmittedCharacter(characterId);
            _run.RemoveAdmittedCharacter(characterId);
            _lateAdmittedCharacters.Remove(characterId);
            return true;
        }
    }

    internal bool TryAdmitMedusaCharacter(
        int characterId,
        out bool added)
    {
        lock (_medusaOwnershipGate)
        {
            if (_medusaInstanceOwner is null)
            {
                added = false;
                return false;
            }

            return _medusaInstanceOwner.TryAdmitCharacter(
                characterId,
                out added);
        }
    }

    internal bool RollBackLateMedusaCharacterAdmission(int characterId)
    {
        lock (_medusaOwnershipGate)
        {
            return _medusaInstanceOwner?.RollBackLateAdmission(
                characterId) == true;
        }
    }
}
