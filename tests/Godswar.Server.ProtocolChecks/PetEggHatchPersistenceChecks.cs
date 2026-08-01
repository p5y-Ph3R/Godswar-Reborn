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
                new PostgresGameStore(connectionString);
            await using var storeB =
                new PostgresGameStore(connectionString);
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
            await InsertCapacityPetsAsync(
                connectionString,
                character.Id,
                PetManagerPlanner.MaximumOwnedPetCount - 2);
            var capacity = await storeA.HatchPetEggAsync(
                account.Id,
                character.Id,
                EggSlot);
            Check.Equal(
                (int)PetEggHatchStatus.PetCapacityReached,
                (int)capacity.Status,
                "native eight-pet capacity rejects another hatch");
            Check.Equal(
                1,
                await ReadEggStackAsync(
                    connectionString,
                    character.Id),
                "capacity rejection preserves the egg");
            Check.Equal(
                PetManagerPlanner.MaximumOwnedPetCount,
                (await storeA.GetOwnedPetsAsync(
                    account.Id,
                    character.Id)).Count,
                "capacity rejection does not create a ninth pet");

            await AssertAuditAsync(
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
        var addedSavvy = result.AddedSavvy
            ?? throw new InvalidOperationException(
                "A successful hatch requires added savvy.");
        Check.Equal(
            ExpectedSpeciesType,
            result.SpeciesType,
            "displayed egg species overrides broken stock Values");
        Check.Equal(
            (short)EggAptitude,
            (short)result.Aptitude,
            "pet aptitude comes directly from egg rarity");
        Check.True(
            PetGrowthPolicy.TryGet(
                result.Aptitude,
                out var bracket) &&
            growth.TotalGrowth >=
                bracket.MinimumTotalGrowth &&
            growth.TotalGrowth <=
                bracket.MaximumTotalGrowth,
            "hatch growth remains in its aptitude bracket");
        Check.Equal(
            growth.TotalGrowth,
            SavvyValues(growth.BaseGrowthRates).Sum(),
            "hatched growth distribution preserves its total");
        Check.True(
            PetAddedSavvyPolicy.TryGet(
                result.Aptitude,
                out var savvyBracket) &&
            addedSavvy.TotalSavvy >=
                savvyBracket.MinimumTotalSavvy &&
            addedSavvy.TotalSavvy <=
                savvyBracket.MaximumTotalSavvy,
            "hatch added savvy remains in its aptitude bracket");
        Check.Equal(
            (decimal)addedSavvy.TotalSavvy,
            SavvyValues(addedSavvy.AddedSavvy).Sum(),
            "hatched added-savvy distribution preserves its total");
        Check.Equal(
            growth.BaseGrowthRates,
            initialSavvy,
            "basic savvy is one times the matching base-growth rate");
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
            "all six growth-derived initial-savvy values survive reload");
        var persistedAddedSavvy = pet.StatValues
            .OrderBy(static stat => stat.StatCode)
            .Select(static stat => stat.AddedSavvy)
            .ToArray();
        Check.True(
            persistedAddedSavvy.SequenceEqual(
                SavvyValues(result.AddedSavvy!.AddedSavvy)),
            "all six rarity-added-savvy values survive reload");
        Check.True(
            pet.StatValues.All(static stat =>
                stat.InitialSavvy > 0m &&
                stat.AddedSavvy > 0m &&
                stat.BirthInitialSavvy == stat.InitialSavvy &&
                stat.RarityAddedSavvy == stat.AddedSavvy &&
                stat.GrowthAcceleration == 0m),
            "basic, rarity-added, growth, and acceleration values retain distinct baselines");
        Check.True(
            pet.Skills.Count == 1 &&
            pet.Skills[0].SkillId == ExpectedStarterSkillId &&
            pet.Skills[0].SlotIndex == 0 &&
            pet.Skills[0].SkillRank == 1,
            "species starter skill persists in slot zero");
    }

}
