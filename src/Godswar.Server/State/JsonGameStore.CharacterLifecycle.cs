namespace Godswar.Server.State;

internal sealed partial class JsonGameStore
{
    public async Task<IReadOnlyList<GameCharacter>> GetCharactersAsync(
        int accountId,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            return db.Characters
                .Where(character =>
                    character.AccountId == accountId &&
                    character.LifecycleState ==
                        CharacterLifecycleState.Active)
                .OrderBy(character => character.Id)
                .Select(Clone)
                .ToArray();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameCharacter?> GetFirstCharacterAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        (await GetCharactersAsync(accountId, cancellationToken))
            .FirstOrDefault();

    public async Task<CharacterStats?> GetCharacterStatsAsync(
        int accountId,
        int characterId,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(candidate =>
                candidate.AccountId == accountId &&
                candidate.Id == characterId &&
                candidate.LifecycleState ==
                    CharacterLifecycleState.Active);
            return character is null
                ? null
                : CharacterStats.FromCharacter(character);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<GameCharacter> CreateCharacterAsync(
        int accountId,
        GameCharacter character,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            if (db.Characters.Any(existing =>
                    existing.AccountId == accountId &&
                    existing.LifecycleState ==
                        CharacterLifecycleState.Active))
            {
                throw new CharacterSlotOccupiedException();
            }

            character.Name = EnsureUniqueCharacterName(
                db,
                CleanCharacterName(character.Name));
            character.Id = db.NextCharacterId++;
            character.AccountId = accountId;
            character.CharacterSlot =
                CharacterLifecyclePolicy.SingleCharacterSlot;
            character.LifecycleState =
                CharacterLifecycleState.Active;
            character.LifecycleVersion =
                NextLifecycleVersion(db, accountId);
            character.DeletedAt = null;
            character.RestoreUntil = null;
            character.PurgeAfter = null;
            GameDefaults.InitializeStartingLocation(character);
            character.Equipment =
                string.IsNullOrWhiteSpace(character.Equipment)
                    ? GameDefaults.DefaultEquipment(
                        character.Profession)
                    : character.Equipment;
            character.KitBag =
                string.IsNullOrWhiteSpace(character.KitBag)
                    ? GameDefaults.StarterKitBag
                    : character.KitBag;
            character.CreatedUtc = DateTime.UtcNow;
            db.Characters.Add(character);

            await SaveUnsafeAsync(db, cancellationToken);
            return Clone(character);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeleteCharacterAsync(
        int accountId,
        string characterName,
        CancellationToken cancellationToken = default)
    {
        characterName = CleanCharacterName(characterName);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(candidate =>
                candidate.AccountId == accountId &&
                candidate.LifecycleState ==
                    CharacterLifecycleState.Active &&
                string.Equals(
                    candidate.Name,
                    characterName,
                    StringComparison.OrdinalIgnoreCase));
            if (character is null)
            {
                return false;
            }

            var deletedAt = DateTimeOffset.UtcNow;
            character.LifecycleState =
                CharacterLifecycleState.Deleted;
            character.LifecycleVersion =
                NextLifecycleVersion(db, accountId);
            character.DeletedAt = deletedAt;
            character.RestoreUntil =
                deletedAt +
                CharacterLifecyclePolicy.DefaultRestoreWindow;
            character.PurgeAfter =
                character.RestoreUntil.Value +
                CharacterLifecyclePolicy.DefaultPurgeDelay;
            character.CheckpointOwnerId = Guid.Empty;
            await SaveUnsafeAsync(db, cancellationToken);
            return true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static long NextLifecycleVersion(
        GameDatabase database,
        int accountId)
    {
        var current = database.Characters
            .Where(character => character.AccountId == accountId)
            .Select(character => character.LifecycleVersion)
            .DefaultIfEmpty(0)
            .Max();
        return checked(current + 1);
    }
}
