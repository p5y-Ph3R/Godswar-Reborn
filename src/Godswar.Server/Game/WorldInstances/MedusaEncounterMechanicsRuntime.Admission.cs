namespace Godswar.Server.Game.WorldInstances;

internal sealed partial class MedusaEncounterMechanicsRuntime
{
    internal bool CanAdmitCharacter(int characterId) =>
        characterId > 0 &&
        !_charactersById.ContainsKey(characterId);

    internal void AdmitCharacter(int characterId)
    {
        if (!CanAdmitCharacter(characterId))
        {
            throw new InvalidOperationException(
                "The Medusa mechanics character is already admitted.");
        }

        var character = new CharacterState(characterId);
        _charactersById.Add(characterId, character);
        _orderedCharacters.Add(character);
        _orderedCharacters.Sort(static (left, right) =>
            left.CharacterId.CompareTo(right.CharacterId));
    }

    internal bool CanRemoveAdmittedCharacter(int characterId) =>
        _charactersById.TryGetValue(characterId, out var character) &&
        character.Effects.Count == 0 &&
        _orderedCharacters.Contains(character) &&
        _pendingPeriodicDamage?.Character.CharacterId != characterId;

    internal bool RemoveAdmittedCharacter(int characterId)
    {
        if (!CanRemoveAdmittedCharacter(characterId) ||
            !_charactersById.TryGetValue(characterId, out var character))
        {
            return false;
        }

        _orderedCharacters.Remove(character);
        _charactersById.Remove(characterId);
        return true;
    }
}
