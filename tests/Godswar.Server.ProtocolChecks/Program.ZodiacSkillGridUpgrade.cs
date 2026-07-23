using Godswar.Server.Game;
using Godswar.Server.Packets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class Program
{
    private static async Task CheckZodiacSkillGridUpgradeAsync()
    {
        int[] expectedEnergyCosts =
        [
            5, 12, 17, 25, 30, 60, 119, 179, 238, 298,
            595, 893, 1_191, 1_489, 1_786, 2_382, 2_977, 3_575,
            4_170, 5_366, 5_996, 6_666, 7_386, 8_166, 9_016,
            9_946, 10_966, 12_086, 13_316, 14_546, 15_876,
            17_316, 18_876, 20_566, 22_496, 24_476, 26_616,
            28_926, 31_416, 34_096, 36_976, 40_066, 43_376,
            46_916, 50_696, 54_726, 59_016, 63_576, 68_416
        ];
        int[] expectedTalentPointCosts =
        [
            7, 15, 25, 32, 40, 263, 362, 523, 682, 920,
            955, 1_196, 1_434, 1_672, 1_786, 2_186, 2_583,
            2_982, 3_381, 4_040, 4_470, 4_950, 5_510, 6_190,
            7_040, 8_120, 9_500, 11_300, 13_060, 14_820,
            16_580, 18_340, 20_100, 21_860, 23_620, 25_380,
            27_140, 28_900, 30_660, 32_420, 34_180, 35_940,
            37_700, 39_460, 41_220, 42_980, 44_740, 46_500,
            48_260
        ];
        byte[] expectedRequiredZodiacLevels =
        [
            1, 2, 2, 3, 3, 4, 4, 5, 5, 6,
            6, 7, 7, 8, 8, 9, 9, 10, 10, 11,
            12, 13, 14, 15, 16, 17, 18, 19, 20, 20,
            21, 21, 22, 22, 23, 23, 24, 24, 25, 25,
            26, 26, 27, 27, 28, 28, 29, 29, 30
        ];

        var energyTotal = 0;
        var talentPointTotal = 0;
        for (var index = 0; index < expectedEnergyCosts.Length; index++)
        {
            Check.True(
                ZodiacSkillGridUpgradeCatalog.TryGetRequirement(
                    index + 1,
                    out var requirement),
                $"grid level {index + 1} has a shipped next-level requirement");
            Check.Equal(
                index + 1,
                (int)requirement.CurrentLevel,
                $"requirement {index} current level");
            Check.Equal(
                index + 2,
                (int)requirement.NextLevel,
                $"requirement {index} next level");
            Check.Equal(
                expectedRequiredZodiacLevels[index],
                requirement.RequiredZodiacLevel,
                $"requirement {index} Zodiac-level gate");
            Check.Equal(
                expectedEnergyCosts[index],
                requirement.EnergyCost,
                $"requirement {index} UpdateE cost");
            Check.Equal(
                expectedTalentPointCosts[index],
                requirement.TalentPointCost,
                $"requirement {index} UpdateS cost");
            energyTotal += requirement.EnergyCost;
            talentPointTotal += requirement.TalentPointCost;
        }

        Check.Equal(827_921, energyTotal, "one grid's level 1-to-50 energy total");
        Check.Equal(
            726_024,
            talentPointTotal,
            "one grid's level 1-to-50 Talent Point total");
        Check.True(
            !ZodiacSkillGridUpgradeCatalog.TryGetRequirement(50, out _),
            "grid level 50 is the shipped maximum");
        Check.Throws<ArgumentOutOfRangeException>(
            () => ZodiacSkillGridUpgradeCatalog.TryGetRequirement(0, out _),
            "inactive level zero has no upgrade-table entry");

        var nativeRequest = Convert.FromHexString(
            "1800392800000000FF00650001000000FFFFFFFF00000000");
        Check.True(
            ZodiacSyncRequest.TryParse(nativeRequest, out var request) &&
            request.IsSkillGridUpgrade,
            "native module-255 SID 101 grid-upgrade intent parses");
        Check.Equal(1, request.Value1, "SID 101 carries zero-based grid index");
        Check.Equal(-1, request.Value2, "SID 101 preserves native placeholder");

        var moduleZeroRequest = nativeRequest.ToArray();
        moduleZeroRequest[8] = 0;
        Check.True(
            ZodiacSyncRequest.TryParse(
                moduleZeroRequest,
                out var compatibleRequest) &&
            compatibleRequest.IsSkillGridUpgrade,
            "module-zero SID 101 remains compatible with Lua-style requests");

        var eligible = CreateGridUpgradeCharacter(
            gridLevel: 1,
            zodiacLevel: 2,
            energy: 5,
            energyRemainderX100: 37,
            talentPoints: 7,
            selectedSkillId: 10_057);
        var upgraded = ZodiacSkillGridUpgrade.Apply(eligible, 1);
        Check.True(upgraded.Committed, "eligible SID 101 upgrade commits");
        Check.Equal(1, (int)upgraded.PreviousLevel, "committed previous level");
        Check.Equal(2, (int)upgraded.CurrentLevel, "committed next level");
        Check.Equal(0, eligible.ZodiacEnergy, "energy cost is server-derived");
        Check.Equal(
            37,
            eligible.ZodiacEnergyRemainderX100,
            "centi-energy remainder survives integer cost");
        Check.Equal(0, eligible.TalentPoints, "Talent Point cost is server-derived");
        Check.Equal(
            10_057,
            eligible.ZodiacSkillGridSkillIds[1],
            "grid upgrade preserves selected skill");

        var inactive = CreateGridUpgradeCharacter(
            gridLevel: 0,
            zodiacLevel: 30,
            energy: 10_000_000,
            talentPoints: 10_000_000);
        AssertRejectedGridUpgrade(
            inactive,
            ZodiacSkillGridUpgradeStatus.InactiveGrid,
            "inactive grid");

        var zodiacGated = CreateGridUpgradeCharacter(
            gridLevel: 5,
            zodiacLevel: 2,
            energy: 10_000,
            talentPoints: 10_000);
        AssertRejectedGridUpgrade(
            zodiacGated,
            ZodiacSkillGridUpgradeStatus.ZodiacLevelTooLow,
            "Zodiac-level-gated grid");

        var energyLimited = CreateGridUpgradeCharacter(
            gridLevel: 1,
            zodiacLevel: 1,
            energy: 4,
            talentPoints: 7);
        AssertRejectedGridUpgrade(
            energyLimited,
            ZodiacSkillGridUpgradeStatus.InsufficientEnergy,
            "energy-limited grid");

        var talentLimited = CreateGridUpgradeCharacter(
            gridLevel: 1,
            zodiacLevel: 1,
            energy: 5,
            talentPoints: 6);
        AssertRejectedGridUpgrade(
            talentLimited,
            ZodiacSkillGridUpgradeStatus.InsufficientTalentPoints,
            "Talent-Point-limited grid");

        var maximum = CreateGridUpgradeCharacter(
            gridLevel: 50,
            zodiacLevel: 30,
            energy: 10_000_000,
            talentPoints: 10_000_000);
        AssertRejectedGridUpgrade(
            maximum,
            ZodiacSkillGridUpgradeStatus.MaximumLevelReached,
            "maximum-level grid");

        var invalid = ZodiacSkillGridUpgrade.Apply(eligible, 16);
        Check.Equal(
            (int)ZodiacSkillGridUpgradeStatus.InvalidGrid,
            (int)invalid.Status,
            "out-of-range SID 101 grid is rejected");

        Check.True(
            PacketBuilder.ZodiacSkillGridUpgraded(1).SequenceEqual(
                Convert.FromHexString(
                    "180039284814000000006500010000000000000000000000")),
            "SID 101 response uses the native 24-byte form");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PacketBuilder.ZodiacSkillGridUpgraded(16),
            "SID 101 response rejects an invalid grid");

        await CheckJsonZodiacSkillGridUpgradeAsync();
    }

    private static GameCharacter CreateGridUpgradeCharacter(
        int gridLevel,
        byte zodiacLevel,
        int energy,
        int talentPoints,
        int energyRemainderX100 = 0,
        int selectedSkillId = -1)
    {
        var character = new GameCharacter
        {
            ZodiacLevel = zodiacLevel,
            ZodiacEnergy = energy,
            ZodiacEnergyRemainderX100 = energyRemainderX100,
            TalentPoints = talentPoints
        };
        character.ZodiacSkillGridLevels[1] = gridLevel;
        character.ZodiacSkillGridSkillIds[1] = selectedSkillId;
        return character;
    }

    private static void AssertRejectedGridUpgrade(
        GameCharacter character,
        ZodiacSkillGridUpgradeStatus expectedStatus,
        string description)
    {
        var previousLevel = character.ZodiacSkillGridLevels[1];
        var previousEnergy = character.ZodiacEnergy;
        var previousRemainder = character.ZodiacEnergyRemainderX100;
        var previousTalentPoints = character.TalentPoints;
        var result = ZodiacSkillGridUpgrade.Apply(character, 1);
        Check.Equal(
            (int)expectedStatus,
            (int)result.Status,
            $"{description} status");
        Check.Equal(
            previousLevel,
            character.ZodiacSkillGridLevels[1],
            $"{description} cannot alter level");
        Check.Equal(
            previousEnergy,
            character.ZodiacEnergy,
            $"{description} cannot spend energy");
        Check.Equal(
            previousRemainder,
            character.ZodiacEnergyRemainderX100,
            $"{description} cannot alter energy remainder");
        Check.Equal(
            previousTalentPoints,
            character.TalentPoints,
            $"{description} cannot spend Talent Points");
    }

    private static async Task CheckJsonZodiacSkillGridUpgradeAsync()
    {
        var dataPath = Path.Combine(
            Path.GetTempPath(),
            $"godswar-zodiac-grid-upgrade-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataPath);

        try
        {
            int accountId;
            int characterId;
            await using (var store = new JsonGameStore(dataPath))
            {
                await store.EnsureSeedDataAsync();
                var account = await store.LoginOrCreateAccountAsync(
                    "zodiac-grid-upgrade",
                    "");
                var character = await store.CreateCharacterAsync(
                    account.Id,
                    new GameCharacter
                    {
                        Name = "ZodiacGridUpgradeHero",
                        Gold = 5_000,
                        ZodiacLevel = 2,
                        ZodiacEnergy = 17,
                        TalentPoints = 22
                    });
                accountId = account.Id;
                characterId = character.Id;

                var activation =
                    await store.ActivateZodiacSkillGridAsync(
                        account.Id,
                        character.Id,
                        1)
                    ?? throw new InvalidOperationException(
                        "JSON Zodiac grid character disappeared");
                Check.True(
                    activation.Committed,
                    "JSON setup activation commits");

                var wrongOwner = await store.UpgradeZodiacSkillGridAsync(
                    account.Id + 1,
                    character.Id,
                    1);
                Check.True(
                    wrongOwner is null,
                    "JSON SID 101 binds character ownership");

                var first = await store.UpgradeZodiacSkillGridAsync(
                    account.Id,
                    character.Id,
                    1)
                    ?? throw new InvalidOperationException(
                        "JSON first grid upgrade disappeared");
                var second = await store.UpgradeZodiacSkillGridAsync(
                    account.Id,
                    character.Id,
                    1)
                    ?? throw new InvalidOperationException(
                        "JSON second grid upgrade disappeared");
                Check.True(
                    first.Committed && second.Committed,
                    "JSON atomically commits sequential upgrade costs");
            }

            await using var reloaded = new JsonGameStore(dataPath);
            var persisted = await reloaded.GetFirstCharacterAsync(accountId)
                ?? throw new InvalidOperationException(
                    "JSON upgraded Zodiac grid did not persist");
            Check.Equal(
                characterId,
                persisted.Id,
                "reloaded upgraded-grid character identity");
            Check.Equal(
                3,
                persisted.ZodiacSkillGridLevels[1],
                "two SID 101 upgrades persist");
            Check.Equal(
                -1,
                persisted.ZodiacSkillGridSkillIds[1],
                "SID 101 preserves unassigned-skill sentinel");
            Check.Equal(0, persisted.ZodiacEnergy, "both energy costs persist");
            Check.Equal(
                0,
                persisted.TalentPoints,
                "both Talent Point costs persist");
        }
        finally
        {
            Directory.Delete(dataPath, recursive: true);
        }
    }
}
