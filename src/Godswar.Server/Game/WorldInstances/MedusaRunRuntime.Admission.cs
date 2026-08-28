namespace Godswar.Server.Game.WorldInstances;

internal sealed partial class MedusaRunRuntime
{
    internal bool TryAdmitCharacter(
        int characterId,
        int playerCapacity,
        out bool added)
    {
        added = false;
        if (characterId <= 0 || playerCapacity <= 0)
        {
            return false;
        }
        if (_admittedCharacters.Contains(characterId))
        {
            return true;
        }
        if (_state != MedusaRunState.Active ||
            _orderedAdmittedCharacters.Count >= playerCapacity)
        {
            return false;
        }

        _admittedCharacters.Add(characterId);
        _orderedAdmittedCharacters.Add(characterId);
        _orderedAdmittedCharacters.Sort();
        added = true;
        return true;
    }

    internal bool CanRemoveAdmittedCharacter(int characterId) =>
        _admittedCharacters.Contains(characterId) &&
        _orderedAdmittedCharacters.Contains(characterId);

    internal bool RemoveAdmittedCharacter(int characterId)
    {
        if (!CanRemoveAdmittedCharacter(characterId))
        {
            return false;
        }

        _orderedAdmittedCharacters.Remove(characterId);
        _admittedCharacters.Remove(characterId);
        return true;
    }
}
