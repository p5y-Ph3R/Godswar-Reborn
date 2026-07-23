namespace Godswar.Server.State;

internal sealed partial class JsonGameStore
{
    public async Task<ZodiacLevelUpgradeResult?> UpgradeZodiacLevelAsync(
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
                candidate.Id == characterId);
            if (character is null)
            {
                return null;
            }

            var result = ZodiacLevelUpgrade.Apply(character);
            if (result.Committed)
            {
                await SaveUnsafeAsync(db, cancellationToken);
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }
}
