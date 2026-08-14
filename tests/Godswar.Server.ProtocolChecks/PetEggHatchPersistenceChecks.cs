using Godswar.Server.Application.Pets;
using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static partial class PetEggHatchPersistenceChecks
{
    private const string ConnectionStringVariable =
        "GODSWAR_TEST_POSTGRES_CONNECTION_STRING";
    private const int EggSlot = 80;
    private const int EggItemId = 10187;
    private const int ExpectedSpeciesType = 38;
    private const int ExpectedStarterSkillId = 5400;
    private const PetAptitude EggAptitude = PetAptitude.Godly;

    public static async Task RunAsync()
    {
        var connectionString =
            Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.WriteLine(
                $"SKIP PostgreSQL pet-egg hatch persistence " +
                $"({ConnectionStringVariable} is not set)");
            return;
        }

        var token = Guid.NewGuid().ToString("N")[..12];
        var username = $"pet_egg_{token}";
        int? accountId = null;
        int? characterId = null;

        try
        {
            await using var storeA =
                new PostgresGameStore(
                    connectionString,
                    petHatchRankRollSource:
                        new FixedPetHatchRankRollSource(89));
            await using var storeB =
                new PostgresGameStore(
                    connectionString,
                    petHatchRankRollSource:
                        new FixedPetHatchRankRollSource(89));
            await storeA.EnsureSeedDataAsync();
            await storeB.EnsureSeedDataAsync();
            CheckEggTemplates(storeA.ItemContent.Templates);

            var account = await storeA.LoginOrCreateAccountAsync(
                username,
                string.Empty);
            accountId = account.Id;
            var character = await storeA.CreateCharacterAsync(
                account.Id,
                new GameCharacter
                {
                    Name = $"Egg{token}",
                    Camp = GameDefaults.SpartaCamp,
                    Profession = 0
                });
            characterId = character.Id;
            await InsertEggAsync(
                connectionString,
                character.Id,
                stack: 1,
                bound: 1);

            var wrongOwner = await storeA.HatchPetEggAsync(
                account.Id + 1,
                character.Id,
                EggSlot);
            Check.Equal(
                (int)PetEggHatchStatus.CharacterNotFound,
                (int)wrongOwner.Status,
                "egg hatch enforces account ownership");
            Check.Equal(
                1,
                await ReadEggStackAsync(
                    connectionString,
                    character.Id),
                "wrong-owner hatch preserves egg");

            await UpdateEggRarityAsync(
                connectionString,
                character.Id,
                rarity: 0);
            var invalidRarity = await storeA.HatchPetEggAsync(
                account.Id,
                character.Id,
                EggSlot);
            Check.Equal(
                (int)PetEggHatchStatus.InvalidEggRarity,
                (int)invalidRarity.Status,
                "undefined egg rarity is rejected");
            Check.Equal(
                1,
                await ReadEggStackAsync(
                    connectionString,
                    character.Id),
                "invalid-rarity rejection preserves egg");

            await UpdateEggRarityAsync(
                connectionString,
                character.Id,
                rarity: (short)PetAptitude.Celestial);
            var unsupportedRarity = await storeA.HatchPetEggAsync(
                account.Id,
                character.Id,
                EggSlot);
            Check.Equal(
                (int)PetEggHatchStatus.UnsupportedEggRarity,
                (int)unsupportedRarity.Status,
                "rarity without a native client profile is rejected");
            Check.Equal(
                1,
                await ReadEggStackAsync(
                    connectionString,
                    character.Id),
                "unsupported-rarity rejection preserves egg");

            await UpdateEggRarityAsync(
                connectionString,
                character.Id,
                rarity: (short)EggAptitude);

            await AssertInvalidRankRollPreservesEggAsync(
                connectionString,
                account.Id,
                character.Id);

            var raced = await Task.WhenAll(
                storeA.HatchPetEggAsync(
                    account.Id,
                    character.Id,
                    EggSlot),
                storeB.HatchPetEggAsync(
                    account.Id,
                    character.Id,
                    EggSlot));
            Check.Equal(
                1,
                raced.Count(static result => result.Succeeded),
                "one concurrent hatch consumes the one-item stack");
            Check.Equal(
                1,
                raced.Count(static result =>
                    result.Status ==
                    PetEggHatchStatus.ItemNotFound),
                "duplicate concurrent hatch sees the committed empty slot");

            var first = raced.Single(static result => result.Succeeded);
            AssertSuccessfulRoll(first);
            Check.Equal(
                0,
                await ReadEggStackAsync(
                    connectionString,
                    character.Id),
                "single egg is consumed exactly once");
            await AssertPersistedPetAsync(
                storeA,
                account.Id,
                character.Id,
                first);

            await InsertEggAsync(
                connectionString,
                character.Id,
                stack: 2,
                bound: 1);
            var invalidStack = await storeA.HatchPetEggAsync(
                account.Id,
                character.Id,
                EggSlot);
            Check.Equal(
                (int)PetEggHatchStatus.InvalidEggStack,
                (int)invalidStack.Status,
                "native non-stackable egg rejects a corrupt stack");
            Check.Equal(
                2,
                await ReadEggStackAsync(
                    connectionString,
                    character.Id),
                "invalid-stack rejection preserves every egg");
            await UpdateEggStackAsync(
                connectionString,
                character.Id,
                stack: 1);
            var second = await storeA.HatchPetEggAsync(
                account.Id,
                character.Id,
                EggSlot);
            AssertSuccessfulRoll(second);
            Check.Equal(
                0,
                await ReadEggStackAsync(
                    connectionString,
                    character.Id),
                "second non-stacked egg is consumed exactly once");

            await InsertEggAsync(
                connectionString,
                character.Id,
                stack: 1,
                bound: 1);
            var capacity = await storeA.HatchPetEggAsync(
                account.Id,
                character.Id,
                EggSlot);
            Check.Equal(
                (int)PetEggHatchStatus.PetCapacityReached,
                (int)capacity.Status,
                "two opened pet sheds reject another hatch");
            Check.Equal(
                1,
                await ReadEggStackAsync(
                    connectionString,
                    character.Id),
                "capacity rejection preserves the egg");
            Check.Equal(
                2,
                (await storeA.GetOwnedPetsAsync(
                    account.Id,
                    character.Id)).Count,
                "shed-capacity rejection creates no extra pet");

            await AssertAuditAsync(
                connectionString,
                character.Id,
                first,
                second);
            await AssertHatchRankEvidenceAsync(
                connectionString,
                character.Id,
                first,
                second);
        }
        finally
        {
            if (accountId.HasValue && characterId.HasValue)
            {
                await DeleteFixtureAsync(
                    connectionString,
                    accountId.Value,
                    username,
                    characterId.Value);
            }
        }
    }

    private static void AssertSuccessfulRoll(
        PetEggHatchResult result)
    {
        Check.True(result.Succeeded, "pet egg hatch commits");
        var growth = result.Growth
            ?? throw new InvalidOperationException(
                "A successful hatch requires growth.");
        var initialSavvy = result.InitialSavvy;
        var initialSavvyRoll = result.InitialSavvyRoll
            ?? throw new InvalidOperationException(
                "A successful hatch requires a Savvy roll.");
        Check.Equal(
            ExpectedSpeciesType,
            result.SpeciesType,
            "displayed egg species overrides broken stock Values");
        Check.Equal(
            (short)EggAptitude,
            (short)result.Aptitude,
            "pet aptitude comes directly from egg rarity");
        var hatchRank = result.HatchRank
            ?? throw new InvalidOperationException(
                "A successful hatch requires rank evidence.");
        Check.True(
            hatchRank == new PetHatchRankRoll(2.70m, 1, 89),
            "injected hatch roll deterministically selects the middle Godly rank");
        Check.True(
            string.Equals(
                PetContentTestCatalog.Instance.Revision.Sha256,
                result.HatchRankContentRevision,
                StringComparison.Ordinal),
            "hatch receipt pins its rank source revision");
        Check.True(
            PetContentTestCatalog.Instance.TryGetAptitude(
                (short)PetAptitude.Weak,
                out var weakAptitude) &&
            growth.TotalGrowth >=
                weakAptitude.MinimumTotalGrowth &&
            growth.TotalGrowth <=
                weakAptitude.MaximumTotalGrowth,
            "unrevealed hatch Growth remains in the Weak content bracket until a Phoenix reset");
        Check.Equal(
            growth.TotalGrowth,
            SavvyValues(growth.BaseGrowthRates).Sum(),
            "hatched growth distribution preserves its total");
        Check.True(
            PetInitialSavvyPolicy.TryGet(
                result.Aptitude,
                out var savvyBracket) &&
            initialSavvyRoll.TotalSavvy >=
                savvyBracket.MinimumTotalSavvy &&
            initialSavvyRoll.TotalSavvy <=
                savvyBracket.MaximumTotalSavvy,
            "hatched Savvy remains in its aptitude bracket");
        Check.Equal(
            (decimal)initialSavvyRoll.TotalSavvy,
            SavvyValues(initialSavvyRoll.InitialSavvy).Sum(),
            "hatched Savvy distribution preserves its total");
        Check.Equal(
            initialSavvyRoll.InitialSavvy,
            initialSavvy,
            "Basic value is the pet-quality Savvy roll");
    }

    private static async Task AssertPersistedPetAsync(
        PostgresGameStore store,
        int accountId,
        int characterId,
        PetEggHatchResult result)
    {
        var pet = (await store.GetOwnedPetsAsync(
                accountId,
                characterId))
            .Single(candidate => candidate.PetId == result.PetId);
        Check.Equal(
            (short)ExpectedSpeciesType,
            pet.SpeciesId,
            "hatched species persists");
        Check.Equal(
            (short)result.Aptitude,
            (short)pet.Aptitude,
            "hatched aptitude persists");
        Check.Equal(
            result.HatchRank!.Rank,
            pet.Rank,
            "hatched rank persists as current rank");
        Check.True(
            PetNativeAptitudeProfileCatalog.TryGet(
                ExpectedSpeciesType,
                result.Aptitude,
                out var nativeProfile),
            "hatched pet resolves its exact native profile");
        Check.Equal(
            nativeProfile.Lifetime,
            pet.RemainingLifetime,
            "hatched lifetime comes from species plus egg rarity");
        Check.True(pet.IsBound, "pet inherits bound egg state");
        Check.True(pet.IsCarried, "a hatched pet is auto-carried");
        Check.True(!pet.IsSummoned, "hatch does not force a new summon");
        Check.Equal(
            (short)PetInnateTalentPolicy.GodlyTalentMask,
            pet.TalentMask,
            "Godly hatch persists all five innate talents");
        Check.True(
            pet.HasOwnerMergeTalent,
            "Godly hatch persists the Merge compatibility projection");
        Check.Equal(6, pet.StatValues.Count, "six growth rows persist");

        var persistedGrowth = pet.StatValues
            .OrderBy(static stat => stat.StatCode)
            .Select(static stat => stat.BaseGrowthRate)
            .ToArray();
        Check.True(
            persistedGrowth.SequenceEqual(
                SavvyValues(result.Growth!.BaseGrowthRates)),
            "all six rolled growth values survive reload");
        var persistedInitialSavvy = pet.StatValues
            .OrderBy(static stat => stat.StatCode)
            .Select(static stat => stat.InitialSavvy)
            .ToArray();
        Check.True(
            persistedInitialSavvy.SequenceEqual(
                SavvyValues(result.InitialSavvy)),
            "all six quality-derived Savvy values survive reload");
        var persistedAddedSavvy = pet.StatValues
            .OrderBy(static stat => stat.StatCode)
            .Select(static stat => stat.AddedSavvy)
            .ToArray();
        Check.True(
            persistedAddedSavvy.SequenceEqual(
                SavvyValues(result.Growth!.BaseGrowthRates)),
            "all six Growth-derived Added-values survive reload");
        Check.True(
            pet.StatValues.All(static stat =>
                stat.InitialSavvy > 0m &&
                stat.AddedSavvy > 0m &&
                stat.BirthInitialSavvy == stat.InitialSavvy &&
                stat.RarityAddedSavvy == stat.InitialSavvy &&
                stat.AddedSavvy == stat.BaseGrowthRate &&
                stat.GrowthAcceleration == 0m),
            "Savvy, Growth, Added-value, and acceleration retain their baselines");
        Check.True(
            pet.Skills.Count == 1 &&
            pet.Skills[0].SkillId == ExpectedStarterSkillId &&
            pet.Skills[0].SlotIndex == 0 &&
            pet.Skills[0].SkillRank == 1,
            "species starter skill persists in slot zero");
    }

    private sealed class FixedPetHatchRankRollSource(int roll) :
        IPetHatchRankRollSource
    {
        public int NextRoll() => roll;
    }

    private static async Task AssertInvalidRankRollPreservesEggAsync(
        string connectionString,
        int accountId,
        int characterId)
    {
        await using var invalidStore = new PostgresGameStore(
            connectionString,
            petHatchRankRollSource:
                new FixedPetHatchRankRollSource(100));
        await invalidStore.EnsureSeedDataAsync();
        try
        {
            _ = await invalidStore.HatchPetEggAsync(
                accountId,
                characterId,
                EggSlot);
        }
        catch (ArgumentOutOfRangeException)
        {
            Check.Equal(
                1,
                await ReadEggStackAsync(connectionString, characterId),
                "invalid injected rank roll rolls back before egg consumption");
            return;
        }

        throw new InvalidOperationException(
            "Invalid injected hatch-rank roll was accepted.");
    }

}
