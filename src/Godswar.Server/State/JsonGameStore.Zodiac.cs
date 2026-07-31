using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Zodiac;

namespace Godswar.Server.State;

internal sealed partial class JsonGameStore :
    IZodiacLevelStore,
    ILocalPlayerOwnershipRegistry
{
    private readonly Dictionary<(int AccountId, int CharacterId),
        PlayerOwnershipFence> _localPlayerOwnership = [];

    public async Task<ZodiacLevelUpgradeResult?> UpgradeZodiacLevelAsync(
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken = default)
    {
        ownership.Validate();
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var db = await LoadUnsafeAsync(cancellationToken);
            var character = db.Characters.FirstOrDefault(candidate =>
                candidate.AccountId == accountId &&
                candidate.Id == characterId);
            if (character is null)
            {
                return null;
            }

            RequireCurrentLocalOwnership(character, ownership);
            var result = ZodiacLevelUpgrade.Apply(character);
            if (result.Committed)
            {
                await SaveUnsafeAsync(db, cancellationToken);
            }

            RequireCurrentLocalOwnership(character, ownership);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    async Task<ZodiacLevelUpgradeStoreResult?> IZodiacLevelStore.UpgradeAsync(
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        var result = await UpgradeZodiacLevelAsync(
            accountId,
            characterId,
            ownership,
            cancellationToken);
        return result is null
            ? null
            : FocusedGameplayProjectionCompatibility.ToApplication(result);
    }

    async Task ILocalPlayerOwnershipRegistry.BindAsync(
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        ownership.Validate();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(accountId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(characterId);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            _localPlayerOwnership[(accountId, characterId)] = ownership;
        }
        finally
        {
            _lock.Release();
        }
    }

    async Task<bool> ILocalPlayerOwnershipRegistry.ReleaseAsync(
        int accountId,
        int characterId,
        PlayerOwnershipFence ownership,
        CancellationToken cancellationToken)
    {
        ownership.Validate();
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var key = (accountId, characterId);
            return _localPlayerOwnership.TryGetValue(key, out var current) &&
                   current == ownership &&
                   _localPlayerOwnership.Remove(key);
        }
        finally
        {
            _lock.Release();
        }
    }

    private void RequireCurrentLocalOwnership(
        GameCharacter character,
        PlayerOwnershipFence ownership)
    {
        var key = (character.AccountId, character.Id);
        var hasOwner = _localPlayerOwnership.TryGetValue(
            key,
            out var current);
        new PlayerOwnershipValidationResult(
                character.LifecycleState == CharacterLifecycleState.Active &&
                hasOwner &&
                current == ownership
                    ? PlayerOwnershipValidationStatus.Current
                    : PlayerOwnershipValidationStatus.OwnershipLost,
                hasOwner ? current.Generation : null)
            .RequireCurrent();
    }
}
