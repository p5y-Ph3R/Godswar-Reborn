using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Gateway;
using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.State;

internal sealed partial class JsonGameStore
{
    private readonly Dictionary<(int AccountId, int CharacterId),
        PlayerOwnershipFence> _localPlayerOwnership = [];
    private readonly Dictionary<(int AccountId, int CharacterId), long>
        _localPlayerOwnershipGenerations = [];

    public async Task<IReadOnlyList<GameCharacter>> GetCharactersAsync(
        int accountId,
        CancellationToken cancellationToken = default) =>
        await GetCharactersAsync(
            accountId,
            RealmId.Tempest,
            cancellationToken);

    public async Task<IReadOnlyList<GameCharacter>> GetCharactersAsync(
        int accountId,
        RealmId realmId,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            return db.Characters
                .Where(character =>
                    character.AccountId == accountId &&
                    character.RealmId == realmId &&
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
        (await GetCharactersAsync(
            accountId,
            RealmId.Tempest,
            cancellationToken))
            .FirstOrDefault();

    public async Task<GameCharacter?> GetFirstCharacterAsync(
        int accountId,
        RealmId realmId,
        CancellationToken cancellationToken = default) =>
        (await GetCharactersAsync(accountId, realmId, cancellationToken))
            .FirstOrDefault();

    public async Task<SemanticGatewayCharacterRoute?>
        FindCharacterRouteAsync(
            int accountId,
            RealmId realmId,
            CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        if (!realmId.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(realmId));
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var routes = (await LoadUnsafeAsync(cancellationToken))
                .Characters
                .Where(character =>
                    character.AccountId == accountId &&
                    character.RealmId == realmId &&
                    character.LifecycleState ==
                        CharacterLifecycleState.Active)
                .OrderBy(character => character.Id)
                .Take(2)
                .Select(character => new SemanticGatewayCharacterRoute(
                    character.Id,
                    character.RealmId,
                    MapId.FromLegacy(character.CurrentMap)))
                .ToArray();
            return routes.Length switch
            {
                0 => null,
                1 => routes[0],
                _ => throw new InvalidDataException(
                    "The account has more than one active character route.")
            };
        }
        finally
        {
            _lock.Release();
        }
    }

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

    public async Task<CharacterCheckpointOwnership?> AcquireAsync(
        int accountId,
        int characterId,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        CharacterCheckpointValidation.ValidateIdentity(
            accountId,
            characterId,
            new PlayerOwnershipFence(ownerId, 1));
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var character = (await LoadUnsafeAsync(cancellationToken))
                .Characters
                .FirstOrDefault(candidate =>
                    candidate.AccountId == accountId &&
                    candidate.Id == characterId &&
                    candidate.LifecycleState ==
                        CharacterLifecycleState.Active);
            if (character is null)
            {
                return null;
            }

            var key = (accountId, characterId);
            var generation =
                _localPlayerOwnership.TryGetValue(key, out var current)
                    && current.OwnerId == ownerId
                    ? current.Generation
                    : _localPlayerOwnershipGenerations.TryGetValue(
                        key,
                        out var previousGeneration)
                        ? checked(previousGeneration + 1)
                        : 1;
            var ownership = new PlayerOwnershipFence(
                ownerId,
                generation);
            var acquired = new CharacterCheckpointOwnership(
                ownership,
                character.PositionRevision,
                character.VitalsRevision);
            acquired.Validate();
            _localPlayerOwnership[key] = ownership;
            _localPlayerOwnershipGenerations[key] = generation;
            return acquired;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<CharacterCheckpointWriteResult>
        WritePositionAsync(
            CharacterPositionCheckpoint checkpoint,
            CancellationToken cancellationToken = default)
    {
        checkpoint.Validate();
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(candidate =>
                candidate.AccountId == checkpoint.AccountId &&
                candidate.Id == checkpoint.CharacterId);
            if (character is null)
            {
                return CheckpointResult(
                    CharacterCheckpointWriteStatus.CharacterNotFound,
                    storedRevision: null);
            }

            if (!HasCurrentLocalOwnership(
                    character,
                    checkpoint.Owner))
            {
                return CheckpointResult(
                    CharacterCheckpointWriteStatus.OwnershipLost,
                    character.PositionRevision);
            }

            var precondition = ClassifyCheckpointRevision(
                character.PositionRevision,
                checkpoint.Revision,
                character.CurrentMap == checkpoint.CurrentMap &&
                character.PositionX.Equals(checkpoint.PositionX) &&
                character.PositionZ.Equals(checkpoint.PositionZ));
            if (precondition.HasValue)
            {
                return CheckpointResult(
                    precondition.Value,
                    character.PositionRevision);
            }

            character.CurrentMap = checkpoint.CurrentMap;
            character.PositionX = checkpoint.PositionX;
            character.PositionZ = checkpoint.PositionZ;
            character.PositionRevision = checkpoint.Revision;
            await SaveUnsafeAsync(db, cancellationToken);
            return CheckpointResult(
                CharacterCheckpointWriteStatus.Applied,
                checkpoint.Revision);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<CharacterCheckpointWriteResult> WriteVitalsAsync(
        CharacterVitalsCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        checkpoint.Validate();
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(candidate =>
                candidate.AccountId == checkpoint.AccountId &&
                candidate.Id == checkpoint.CharacterId);
            if (character is null)
            {
                return CheckpointResult(
                    CharacterCheckpointWriteStatus.CharacterNotFound,
                    storedRevision: null);
            }

            if (!HasCurrentLocalOwnership(
                    character,
                    checkpoint.Owner))
            {
                return CheckpointResult(
                    CharacterCheckpointWriteStatus.OwnershipLost,
                    character.VitalsRevision);
            }

            var precondition = ClassifyCheckpointRevision(
                character.VitalsRevision,
                checkpoint.Revision,
                character.CurrentHp == checkpoint.CurrentHp &&
                character.CurrentMp == checkpoint.CurrentMp);
            if (precondition.HasValue)
            {
                return CheckpointResult(
                    precondition.Value,
                    character.VitalsRevision);
            }

            character.CurrentHp = checkpoint.CurrentHp;
            character.CurrentMp = checkpoint.CurrentMp;
            character.VitalsRevision = checkpoint.Revision;
            await SaveUnsafeAsync(db, cancellationToken);
            return CheckpointResult(
                CharacterCheckpointWriteStatus.Applied,
                checkpoint.Revision);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<CharacterCheckpointReleaseStatus> ReleaseAsync(
        int accountId,
        int characterId,
        PlayerOwnershipFence owner,
        CancellationToken cancellationToken = default)
    {
        CharacterCheckpointValidation.ValidateIdentity(
            accountId,
            characterId,
            owner);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var key = (accountId, characterId);
            if (!_localPlayerOwnership.TryGetValue(
                    key,
                    out var current))
            {
                return CharacterCheckpointReleaseStatus.AlreadyReleased;
            }
            if (current != owner)
            {
                return CharacterCheckpointReleaseStatus.OwnershipLost;
            }

            _localPlayerOwnership.Remove(key);
            return CharacterCheckpointReleaseStatus.Released;
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool HasCurrentLocalOwnership(
        GameCharacter character,
        PlayerOwnershipFence owner) =>
        character.LifecycleState == CharacterLifecycleState.Active &&
        _localPlayerOwnership.TryGetValue(
            (character.AccountId, character.Id),
            out var current) &&
        current == owner;

    private static CharacterCheckpointWriteStatus?
        ClassifyCheckpointRevision(
            long storedRevision,
            long requestedRevision,
            bool payloadMatches)
    {
        if (storedRevision > requestedRevision)
        {
            return CharacterCheckpointWriteStatus.Superseded;
        }
        if (storedRevision == requestedRevision)
        {
            return payloadMatches
                ? CharacterCheckpointWriteStatus.AlreadyApplied
                : CharacterCheckpointWriteStatus.RevisionConflict;
        }

        return null;
    }

    private static CharacterCheckpointWriteResult CheckpointResult(
        CharacterCheckpointWriteStatus status,
        long? storedRevision) =>
        new(status, storedRevision);

    public async Task<GameCharacter> CreateCharacterAsync(
        int accountId,
        GameCharacter character,
        CancellationToken cancellationToken = default) =>
        await CreateCharacterAsync(
            accountId,
            RealmId.Tempest,
            character,
            cancellationToken);

    public async Task<GameCharacter> CreateCharacterAsync(
        int accountId,
        RealmId realmId,
        GameCharacter character,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            if (db.Characters.Any(existing =>
                    existing.AccountId == accountId &&
                    existing.RealmId == realmId &&
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
            character.RealmId = realmId;
            character.CharacterSlot =
                CharacterLifecyclePolicy.SingleCharacterSlot;
            character.LifecycleState =
                CharacterLifecycleState.Active;
            character.LifecycleVersion =
                NextLifecycleVersion(db, accountId, realmId);
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
        CancellationToken cancellationToken = default) =>
        await DeleteCharacterAsync(
            accountId,
            RealmId.Tempest,
            characterName,
            cancellationToken);

    public async Task<bool> DeleteCharacterAsync(
        int accountId,
        RealmId realmId,
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
                candidate.RealmId == realmId &&
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
                NextLifecycleVersion(db, accountId, realmId);
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
        int accountId,
        RealmId realmId)
    {
        var current = database.Characters
            .Where(character =>
                character.AccountId == accountId &&
                character.RealmId == realmId)
            .Select(character => character.LifecycleVersion)
            .DefaultIfEmpty(0)
            .Max();
        return checked(current + 1);
    }
}
