using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetSystemFoundationChecks
{
    public static Task RunAsync()
    {
        CheckSpeciesCatalog();
        CheckItemCatalog();
        CheckSkillFamilyCatalog();
        CheckPersistenceMigration();
        CheckOwnerMergeToggle();
        CheckRebirth();
        CheckPetMerge();
        return Task.CompletedTask;
    }

    private static void CheckItemCatalog()
    {
        Check.True(
            PetItemCatalog.TryGetCore(PetItemCatalog.MergedSpirit, out var mergedSpirit) &&
            mergedSpirit.DisplayName == "Merged Spirit" &&
            mergedSpirit.Purpose == PetItemPurpose.PetMerge,
            "Merged Spirit has its exact client ID and pet-merge purpose");
        Check.True(
            PetItemCatalog.TryGetCore(PetItemCatalog.RebirthSpirit, out var rebirthSpirit) &&
            rebirthSpirit.Purpose == PetItemPurpose.Rebirth,
            "Rebirth Spirit is cataloged");
        Check.True(
            PetItemCatalog.TryGetCore(PetItemCatalog.FairyFeather, out var fairyFeather) &&
            fairyFeather.Purpose == PetItemPurpose.SavvyReset,
            "Fairy's Feather resets the six-savvy distribution");
        Check.True(
            PetItemCatalog.TryGetCore(PetItemCatalog.PhoenixFeather, out var phoenixFeather) &&
            phoenixFeather.Purpose == PetItemPurpose.GrowthReset,
            "Phoenix's Feather resets pet growth");
        Check.True(
            PetItemCatalog.FindRange(10200)?.Purpose == PetItemPurpose.SkillBook &&
            PetItemCatalog.FindRange(10745)?.Purpose == PetItemPurpose.SkillBook &&
            PetItemCatalog.FindRange(11094)?.Purpose == PetItemPurpose.SpeciesChange,
            "skill-book and Magic Jade ranges include their client endpoints");
    }

    private static void CheckSkillFamilyCatalog()
    {
        Check.Equal(
            PetSkillFamilyCatalog.FamilyCount,
            PetSkillFamilyCatalog.All.Count,
            "all 67 installed-client pet skill families are cataloged");
        Check.Equal(
            PetSkillFamilyCatalog.BookBackedFamilyCount,
            PetSkillFamilyCatalog.All.Count(static family => family.HasSkillBooks),
            "57 pet skill families have stock skill books");
        Check.Equal(
            1655,
            PetSkillFamilyCatalog.RuntimeRowCount,
            "raw Pet_Skill.xml runtime-row count is retained");
        Check.True(
            PetSpeciesCatalog.All.All(species =>
                PetSkillFamilyCatalog.TryGetByInitialRuntimeSkillId(
                    species.StarterSkillId,
                    out _)),
            "every species starter skill resolves to a known family");
        Check.True(
            PetSkillFamilyCatalog.TryGetByInitialRuntimeSkillId(3124, out var impTrick) &&
            impTrick.DisplayName == "Imp Trick" &&
            !impTrick.HasSkillBooks,
            "starter-only pet families are explicit rather than assigned invented books");
    }

    private static void CheckSpeciesCatalog()
    {
        Check.Equal(45, PetSpeciesCatalog.All.Count, "all client pet species are cataloged");
        Check.True(
            PetSpeciesCatalog.All.Select(static species => species.Type)
                .SequenceEqual(Enumerable.Range(1, 45)),
            "pet species retain contiguous client type IDs");
        Check.True(
            PetSpeciesCatalog.All.All(static species =>
                species.MagicJadeItemId == 11049u + (uint)species.Type),
            "every species maps to its stock Magic Jade");
        Check.Equal(5, PetSpeciesCatalog.EggInconsistencies.Count, "four wrong eggs and one missing egg are explicit");

        Check.True(PetSpeciesCatalog.TryGet(1, out var rockElf), "Rock Elf resolves");
        Check.True(rockElf.FoodKind == PetFoodKind.Omnivore, "Rock Elf food type");
        Check.Equal(405, rockElf.StarterSkillId, "Rock Elf initial runtime skill");
        Check.Equal("Life Totem I", rockElf.StarterSkillName, "Rock Elf initial skill name");
        Check.True(
            rockElf.ClientLifetimeValues.SequenceEqual(new[] { 600, 800, 1100 }),
            "Rock Elf retains every authored lifetime");

        Check.True(PetSpeciesCatalog.TryGet(38, out var thunderPixie), "Thunder Pixie resolves");
        Check.True(thunderPixie.EggItemId == 10187, "Thunder Pixie named egg item");
        Check.True(thunderPixie.EggDeclaredSpeciesType == 36, "Thunder Pixie egg's actual payload");
        Check.True(
            thunderPixie.EggStatus == PetEggCatalogStatus.PayloadTargetsDifferentSpecies,
            "Thunder Pixie egg inconsistency is not hidden");

        Check.True(PetSpeciesCatalog.TryGet(45, out var cupid), "Cupid resolves");
        Check.True(cupid.EggStatus == PetEggCatalogStatus.Missing, "Cupid has no stock egg item");
        Check.Equal((uint)11094, cupid.MagicJadeItemId, "Cupid still has a Magic Jade");
        Check.Equal(4740, PetSpeciesCatalog.EggHatchRuntimeSkillId, "stock egg hatch runtime skill");
    }

    private static void CheckPersistenceMigration()
    {
        var migration = PostgresSchemaMigrationCatalog.All.Single(candidate =>
            candidate.Id == "20260728_010_pet_foundation");
        var sql = migration.Sql;

        Check.True(
            sql.Contains("CREATE TABLE IF NOT EXISTS public.pet_templates", StringComparison.Ordinal) &&
            sql.Contains("CREATE TABLE IF NOT EXISTS public.character_pets", StringComparison.Ordinal) &&
            sql.Contains("CREATE TABLE IF NOT EXISTS public.character_pet_stat_values", StringComparison.Ordinal) &&
            sql.Contains("public.character_pet_character_bonuses", StringComparison.Ordinal) &&
            sql.Contains("CREATE TABLE IF NOT EXISTS public.character_pet_skills", StringComparison.Ordinal) &&
            sql.Contains("CREATE TABLE IF NOT EXISTS public.pet_operation_audit", StringComparison.Ordinal),
            "pet migration owns normalized templates, instances, savvy, bonuses, skills, and audit state");
        Check.True(
            sql.Contains("CHECK (level BETWEEN 1 AND 120)", StringComparison.Ordinal) &&
            sql.Contains("CHECK (slot_index BETWEEN 0 AND 5)", StringComparison.Ordinal) &&
            sql.Contains("current_energy <= maximum_energy", StringComparison.Ordinal) &&
            sql.Contains("ux_character_pets_one_summoned", StringComparison.Ordinal) &&
            sql.Contains("ux_character_pets_one_contributing", StringComparison.Ordinal),
            "pet persistence enforces client limits and one active pet per character");
        Check.True(
            sql.Contains("(7, 'Wing Race'", StringComparison.Ordinal) &&
            sql.Contains("(22, 'Poison Cactus'", StringComparison.Ordinal) &&
            sql.Contains("(45, 'Cupid'", StringComparison.Ordinal),
            "database species names use installed Message_Pet localization");
        Check.True(
            new[]
            {
                10097, 10098, 10103, 10104, 10105, 10106,
                10107, 10108, 11000, 11003, 11004, 11005
            }.All(itemId => sql.Contains($"({itemId}, 'Pet{itemId}'", StringComparison.Ordinal)),
            "core pet-manager items are reconciled by their exact client IDs");
        Check.True(
            !sql.Contains("opcode", StringComparison.OrdinalIgnoreCase),
            "pet migration cannot guess an uncaptured wire opcode");

        var aptitudeMigration = PostgresSchemaMigrationCatalog.All.Single(candidate =>
            candidate.Id == "20260728_011_pet_aptitude_range");
        Check.True(
            aptitudeMigration.Sql.Contains(
                "CHECK (aptitude BETWEEN 1 AND 16)",
                StringComparison.Ordinal),
            "forward-only correction accepts every aptitude rendered by the client");
    }

    private static void CheckOwnerMergeToggle()
    {
        var pet = CreatePet() with
        {
            HasOwnerMergeTalent = true,
            IsSummoned = true,
            CurrentEnergy = 100,
            MaximumEnergy = 100,
            Amity = 40
        };
        var contribution = new PetOwnerStatContribution(
            100m, 2m, 20m, 3m, 10m, 1m, 4m, 5m,
            50m, 2m, 20m, 3m, 10m, 1m, 2m, 4m);
        var outcome = new AuthoritativePetOwnerMergeOutcome(
            contribution,
            [405, 600],
            EnergyAfterMerge: 80);

        Check.True(
            PetManagerPlanner.TryToggleOwnerMerge(
                pet,
                pet.OwnerCharacterId,
                outcome,
                out var mergePlan,
                out var rejection),
            "eligible pet merges with its owner");
        Check.True(rejection == PetPlanRejection.None, "owner merge accepted");
        Check.True(mergePlan!.PetAfter.IsMergedWithOwner, "owner merge state is retained");
        Check.Equal(80, mergePlan.PetAfter.CurrentEnergy, "server outcome controls post-merge energy");

        Check.True(
            PetManagerPlanner.TryToggleOwnerMerge(
                mergePlan.PetAfter,
                pet.OwnerCharacterId,
                outcome: null,
                out var unmergePlan,
                out rejection),
            "merged pet toggles off without a second outcome");
        Check.True(!unmergePlan!.PetAfter.IsMergedWithOwner, "unmerge clears merge state");
        Check.Equal(contribution, unmergePlan.StatContribution, "unmerge removes the stored contribution");

        var lowAmity = pet with { Amity = 39 };
        Check.True(
            !PetManagerPlanner.TryToggleOwnerMerge(
                lowAmity,
                pet.OwnerCharacterId,
                outcome,
                out _,
                out rejection),
            "amity below the client threshold is rejected");
        Check.True(
            rejection == PetPlanRejection.InsufficientAmity,
            "owner merge amity rejection");

        var tooManySkills = outcome with { GrantedSkillIds = [1, 2, 3, 4, 5, 6, 7] };
        Check.True(
            !PetManagerPlanner.TryToggleOwnerMerge(
                pet,
                pet.OwnerCharacterId,
                tooManySkills,
                out _,
                out rejection),
            "the stock six pet-skill slots remain bounded");
        Check.True(
            rejection == PetPlanRejection.InvalidAuthoritativeOutcome,
            "seventh merged pet skill is rejected");
    }

    private static void CheckRebirth()
    {
        var ladder = new[] { 50, 80, 100, 110, 120, 120, 120 };
        Check.True(
            ladder.Select((_, index) => PetManagerPlanner.RequiredLevelForRebirth(index))
                .SequenceEqual(ladder),
            "rebirth level ladder follows Pet_Alter.xml");

        var initial = new PetSavvy(10m, 11m, 12m, 13m, 14m, 15m);
        var pet = CreatePet() with
        {
            Level = 80,
            CompletedRebirths = 1,
            RebirthsRemaining = 3,
            HasSoulContract = true,
            IsSummoned = true,
            Rank = 20m,
            InitialSavvy = initial,
            AddedSavvy =
                new PetSavvy(42m, 43m, 44m, 45m, 46m, 47m),
            RarityAddedSavvy =
                new PetSavvy(40m, 41m, 42m, 43m, 44m, 45m)
        };
        var acceleration =
            new PetSavvy(0.15m, 0.15m, 0.15m, 0.15m, 0.15m, 0.15m);
        var outcome = new AuthoritativePetRebirthOutcome(
            CarriedExperience: 1234,
            RankAfter: 22m,
            acceleration);

        Check.True(
            PetManagerPlanner.TryPlanRebirth(
                pet,
                pet.OwnerCharacterId,
                new PetRebirthMaterials(
                    RebirthSpiritCount: 5,
                    RebornHarpyiaCount: 0),
                outcome,
                out var plan,
                out var rejection),
            "eligible pet rebirth is planned");
        Check.True(rejection == PetPlanRejection.None, "rebirth accepted");
        Check.Equal(1, plan!.PetAfter.Level, "rebirth returns pet to level one");
        Check.Equal(1234L, plan.PetAfter.Experience, "server-authored carried EXP is applied");
        Check.Equal(initial, plan.PetAfter.InitialSavvy, "rebirth preserves initial savvy");
        Check.Equal(
            pet.RarityAddedSavvy,
            plan.PetAfter.AddedSavvy,
            "rebirth clears only additions above the rarity floor");
        Check.Equal(acceleration, plan.PetAfter.GrowthAcceleration, "server-authored growth is applied");
        Check.Equal(2, plan.PetAfter.CompletedRebirths, "rebirth count advances");
        Check.Equal(2, plan.PetAfter.RebirthsRemaining, "one rebirth chance is consumed");

        Check.True(
            !PetManagerPlanner.TryPlanRebirth(
                pet with { HasSoulContract = false },
                pet.OwnerCharacterId,
                new PetRebirthMaterials(5, 0),
                outcome,
                out _,
                out rejection),
            "rebirth requires the stock Soul Contract prerequisite");
        Check.True(
            rejection == PetPlanRejection.SoulContractRequired,
            "missing Soul Contract has a specific rejection");

        Check.True(
            !PetManagerPlanner.TryPlanRebirth(
                pet,
                pet.OwnerCharacterId,
                new PetRebirthMaterials(0, 1),
                outcome,
                out _,
                out rejection),
            "restricted Reborn Harpyia cannot be used on an unbound pet");
        Check.True(
            rejection == PetPlanRejection.RestrictedMaterialRequiresBoundPet,
            "restricted rebirth material has a specific rejection");

        Check.True(
            PetManagerPlanner.TryPlanRebirth(
                pet with { IsBound = true },
                pet.OwnerCharacterId,
                new PetRebirthMaterials(0, 5),
                outcome,
                out var boundPlan,
                out rejection),
            "a bound pet may use the restricted Reborn Harpyia variant");
        Check.Equal(
            5,
            boundPlan!.Materials.RebornHarpyiaCount,
            "restricted rebirth materials remain in the transaction plan");
    }

    private static void CheckPetMerge()
    {
        var initial = new PetSavvy(10m, 20m, 30m, 40m, 50m, 60m);
        var primary = CreatePet() with
        {
            Level = 30,
            Rank = 15m,
            InitialSavvy = initial,
            HasSoulContract = false,
            IsSummoned = true
        };
        var deputy = CreatePet(petId: 2) with
        {
            Level = 30,
            Rank = 18m,
            HasSoulContract = false,
            IsSummoned = false
        };
        var improvedSavvy = new PetSavvy(11m, 21m, 31m, 41m, 51m, 61m);
        var outcome = new AuthoritativePetMergeOutcome(16m, improvedSavvy);

        Check.True(
            PetManagerPlanner.TryPlanPetMerge(
                primary,
                deputy,
                primary.OwnerCharacterId,
                new PetMergeMaterials(
                    MergedSpiritCount: 5,
                    FusedHarpyiaCount: 0),
                outcome,
                out var plan,
                out var rejection),
            "eligible primary and deputy pet merge");
        Check.True(rejection == PetPlanRejection.None, "pet merge accepted");
        Check.Equal(improvedSavvy, plan!.PrimaryPetAfter.InitialSavvy, "only server-authored savvy is applied");
        Check.Equal(1, plan.PrimaryPetAfter.CompletedPetMerges, "pet merge count advances");
        Check.Equal(deputy.PetId, plan.ConsumedDeputyPetId, "deputy pet is explicitly consumed");

        var contractedDeputy = deputy with { HasSoulContract = true };
        Check.True(
            PetManagerPlanner.TryPlanPetMerge(
                primary,
                contractedDeputy,
                primary.OwnerCharacterId,
                new PetMergeMaterials(0, 0),
                outcome,
                out _,
                out rejection),
            "Soul Contract status has no effect on pet merge");
        Check.True(
            rejection == PetPlanRejection.None,
            "contracted deputy remains eligible");

        var clientInventedRegression = new AuthoritativePetMergeOutcome(
            14m,
            new PetSavvy(9m, 19m, 29m, 39m, 49m, 59m));
        Check.True(
            !PetManagerPlanner.TryPlanPetMerge(
                primary,
                deputy,
                primary.OwnerCharacterId,
                new PetMergeMaterials(0, 0),
                clientInventedRegression,
                out _,
                out rejection),
            "a regressive untrusted-looking outcome is rejected");
        Check.True(
            rejection == PetPlanRejection.InvalidAuthoritativeOutcome,
            "pet merge outcome rejection");

        Check.True(
            !PetManagerPlanner.TryPlanPetMerge(
                primary,
                deputy,
                primary.OwnerCharacterId,
                new PetMergeMaterials(0, 1),
                outcome,
                out _,
                out rejection),
            "restricted Fused Harpyia cannot be used on an unbound primary pet");
        Check.True(
            rejection == PetPlanRejection.RestrictedMaterialRequiresBoundPet,
            "restricted pet-merge material has a specific rejection");

        Check.True(
            PetManagerPlanner.TryPlanPetMerge(
                primary with { IsBound = true },
                deputy,
                primary.OwnerCharacterId,
                new PetMergeMaterials(0, 5),
                outcome,
                out var boundPlan,
                out rejection),
            "a bound primary pet may use the restricted Fused Harpyia variant");
        Check.Equal(
            5,
            boundPlan!.Materials.FusedHarpyiaCount,
            "restricted pet-merge materials remain in the transaction plan");
    }

    private static OwnedPet CreatePet(long petId = 1) =>
        new(
            petId,
            OwnerCharacterId: 7,
            SpeciesType: 1,
            Name: "Test Pet",
            Level: 1,
            Experience: 0,
            Rank: 1m,
            Aptitude: PetAptitude.Weak,
            InitialSavvy: PetSavvy.Zero,
            AddedSavvy: PetSavvy.Zero,
            BaseGrowthRates: PetSavvy.Zero,
            GrowthAcceleration: PetSavvy.Zero,
            CompletedPetMerges: 0,
            CompletedRebirths: 0,
            RebirthsRemaining: 1,
            HasSoulContract: false,
            HasOwnerMergeTalent: false,
            IsBound: false,
            IsSummoned: false,
            IsAway: false,
            CurrentEnergy: 0,
            MaximumEnergy: 100,
            Amity: 0,
            OwnerMerge: null);
}
