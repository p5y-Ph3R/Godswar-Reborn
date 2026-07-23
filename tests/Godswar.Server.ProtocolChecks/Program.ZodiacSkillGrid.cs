using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static async Task CheckZodiacSkillGridActivationAsync()
    {
        int[] expectedActivationCosts =
        [
            0, 2_300, 7_200, 14_400,
            0, 2_300, 7_200, 14_400,
            0, 0, 920, 920,
            0, 0, 920, 920
        ];
        Check.Equal(
            16,
            ZodiacSkillGridCatalog.GridCount,
            "native Zodiac state exposes sixteen training grids");
        for (var gridIndex = 0;
             gridIndex < ZodiacSkillGridCatalog.GridCount;
             gridIndex++)
        {
            Check.Equal(
                expectedActivationCosts[gridIndex],
                ZodiacSkillGridCatalog.GetActivationGoldCost(gridIndex),
                $"grid {gridIndex} uses shipped UnlockG premium-gold cost");
            Check.Equal(
                (gridIndex / 4) << 8,
                ZodiacSkillGridCatalog.PackClientLevel(gridIndex, 0),
                $"grid {gridIndex} uses zero-based native row marker");
        }

        var requestBytes = Convert.FromHexString(
            "18003928000000000000640001000000FFFFFFFF00000000");
        Check.True(
            ZodiacSyncRequest.TryParse(requestBytes, out var request) &&
            request.IsSkillGridActivation,
            "native module-zero SID 100 activation intent parses");
        Check.Equal(1, request.Value1, "SID 100 carries zero-based grid index");
        Check.Equal(-1, request.Value2, "SID 100 preserves client placeholder");

        var paid = new GameCharacter { Gold = 5_000 };
        var paidResult = ZodiacSkillGridActivation.Apply(paid, 1);
        Check.True(paidResult.Committed, "eligible paid grid activation succeeds");
        Check.Equal(2_300, paidResult.GoldCost, "paid activation derives server cost");
        Check.Equal(2_700, paid.Gold, "paid activation deducts premium gold");
        Check.Equal(1, paid.ZodiacSkillGridLevels[1], "activation begins at grid level one");
        Check.Equal(
            -1,
            paid.ZodiacSkillGridSkillIds[1],
            "activation does not invent an assigned combat skill");

        var duplicate = ZodiacSkillGridActivation.Apply(paid, 1);
        Check.Equal(
            (int)ZodiacSkillGridActivationStatus.AlreadyActive,
            (int)duplicate.Status,
            "repeat activation is rejected idempotently");
        Check.Equal(2_700, paid.Gold, "repeat activation cannot double-charge");

        var free = new GameCharacter { Gold = 0 };
        Check.True(
            ZodiacSkillGridActivation.Apply(free, 0).Committed,
            "zero-cost shipped grid activates without premium gold");
        Check.Equal(0, free.Gold, "free activation leaves wallet unchanged");

        var insufficient = new GameCharacter { Gold = 7_199 };
        var insufficientResult = ZodiacSkillGridActivation.Apply(
            insufficient,
            2);
        Check.Equal(
            (int)ZodiacSkillGridActivationStatus.InsufficientGold,
            (int)insufficientResult.Status,
            "server rejects insufficient premium gold");
        Check.Equal(7_199, insufficient.Gold, "rejected activation cannot charge");
        Check.Equal(
            0,
            insufficient.ZodiacSkillGridLevels[2],
            "rejected activation cannot alter grid state");

        var invalid = ZodiacSkillGridActivation.Apply(paid, 16);
        Check.Equal(
            (int)ZodiacSkillGridActivationStatus.InvalidGrid,
            (int)invalid.Status,
            "out-of-range grid is rejected");

        Check.True(
            PacketBuilder.ZodiacSkillGridActivated(1).SequenceEqual(
                Convert.FromHexString(
                    "180039284814000000006400010000000000000000000000")),
            "SID 100 success response uses the native 24-byte form");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.ZodiacSkillGridActivated(-1),
            "response builder rejects an invalid grid");

        await CheckJsonZodiacSkillGridActivationAsync();
    }

    private static async Task CheckJsonZodiacSkillGridActivationAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-zodiac-grid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            int accountId;
            int characterId;
            await using (var store = new JsonGameStore(dataPath))
            {
                await store.EnsureSeedDataAsync();
                var account = await store.LoginOrCreateAccountAsync(
                    "zodiac-grid",
                    "");
                var character = await store.CreateCharacterAsync(
                    account.Id,
                    new GameCharacter
                    {
                        Name = "ZodiacGridHero",
                        Gold = 5_000
                    });
                accountId = account.Id;
                characterId = character.Id;

                var wrongOwner = await store.ActivateZodiacSkillGridAsync(
                    account.Id + 1,
                    character.Id,
                    1);
                Check.True(
                    wrongOwner is null,
                    "JSON grid activation binds character ownership");

                var result = await store.ActivateZodiacSkillGridAsync(
                    account.Id,
                    character.Id,
                    1)
                    ?? throw new InvalidOperationException(
                        "JSON Zodiac grid character disappeared");
                Check.True(
                    result.Committed,
                    "JSON grid and premium-gold deduction commit together");
            }

            await using var reloaded = new JsonGameStore(dataPath);
            var persisted = await reloaded.GetFirstCharacterAsync(accountId)
                ?? throw new InvalidOperationException(
                    "JSON Zodiac grid state did not persist");
            Check.Equal(characterId, persisted.Id, "reloaded grid character identity");
            Check.Equal(2_700, persisted.Gold, "reloaded premium-gold deduction");
            Check.Equal(1, persisted.ZodiacSkillGridLevels[1], "reloaded grid level");
            Check.Equal(-1, persisted.ZodiacSkillGridSkillIds[1], "reloaded selected-skill sentinel");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
