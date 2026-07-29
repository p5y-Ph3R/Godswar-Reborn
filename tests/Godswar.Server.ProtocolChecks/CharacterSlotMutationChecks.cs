using Godswar.Server.Game;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class CharacterSlotMutationChecks
{
    public static async Task RunAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-character-slot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);
        try
        {
            await using var storeA = new JsonGameStore(dataPath);
            await using var storeB = new JsonGameStore(dataPath);
            await storeA.EnsureSeedDataAsync();
            var account = await storeA.LoginOrCreateAccountAsync(
                "single-slot-owner",
                string.Empty);

            var attempts = await Task.WhenAll(
                TryCreateAsync(storeA, account.Id, "SingleSlotA"),
                TryCreateAsync(storeB, account.Id, "SingleSlotB"));
            Check.Equal(
                1,
                attempts.Count(static attempt => attempt.Created),
                "concurrent JSON creation commits exactly one character");
            Check.Equal(
                1,
                attempts.Count(static attempt => attempt.Occupied),
                "concurrent JSON creation rejects the occupied slot");

            var persisted = await storeA.GetCharactersAsync(account.Id);
            Check.Equal(
                1,
                persisted.Count,
                "JSON single-slot guard persists exactly one character");
            var repeated = await TryCreateAsync(
                storeA,
                account.Id,
                "SingleSlotReplay");
            Check.True(
                repeated.Occupied && !repeated.Created,
                "replayed JSON creation rejects an occupied slot");
            Check.Equal(
                1,
                (await storeA.GetCharactersAsync(account.Id)).Count,
                "replayed creation cannot corrupt slot cardinality");
        }
        finally
        {
            if (Directory.Exists(dataPath))
            {
                Directory.Delete(dataPath, recursive: true);
            }
        }
    }

    private static async Task<CreateAttempt> TryCreateAsync(
        IGameStore store,
        int accountId,
        string name)
    {
        try
        {
            _ = await store.CreateCharacterAsync(
                accountId,
                new GameCharacter
                {
                    Name = name,
                    Camp = GameDefaults.SpartaCamp,
                    Profession = 0,
                    Level = 1
                });
            return new CreateAttempt(Created: true, Occupied: false);
        }
        catch (CharacterSlotOccupiedException)
        {
            return new CreateAttempt(Created: false, Occupied: true);
        }
    }

    private sealed record CreateAttempt(bool Created, bool Occupied);
}
