namespace Godswar.Server.State;

internal enum PetPlanRejection
{
    None,
    MissingPet,
    NotOwned,
    InvalidPetState,
    PetUnavailable,
    MustBeSummoned,
    OwnerMergeTalentRequired,
    EnergyNotFull,
    InsufficientAmity,
    AlreadyMergedWithOwner,
    SoulContractRequired,
    NoRebirthsRemaining,
    MaximumRebirthsReached,
    LevelTooLow,
    InvalidMaterialCount,
    RestrictedMaterialRequiresBoundPet,
    SamePet,
    InvalidAuthoritativeOutcome
}

internal sealed record AuthoritativePetOwnerMergeOutcome(
    PetOwnerStatContribution StatContribution,
    IReadOnlyList<int> GrantedSkillIds,
    int EnergyAfterMerge);

internal sealed record PetOwnerMergePlan(
    OwnedPet PetAfter,
    bool IsMerging,
    PetOwnerStatContribution StatContribution,
    IReadOnlyList<int> GrantedSkillIds);

internal sealed record AuthoritativePetRebirthOutcome(
    long CarriedExperience,
    decimal RankAfter,
    PetSavvy GrowthAcceleration);

internal readonly record struct PetRebirthMaterials(
    int RebirthSpiritCount,
    int RebornHarpyiaCount);

internal sealed record PetRebirthPlan(
    OwnedPet PetAfter,
    PetRebirthMaterials Materials,
    int RequiredLevel);

internal sealed record AuthoritativePetMergeOutcome(
    decimal RankAfter,
    PetSavvy InitialSavvyAfter);

internal readonly record struct PetMergeMaterials(
    int MergedSpiritCount,
    int FusedHarpyiaCount);

internal sealed record PetMergePlan(
    OwnedPet PrimaryPetAfter,
    long ConsumedDeputyPetId,
    PetMergeMaterials Materials);

/// <summary>
/// Validates the currently captured transport-independent Pet Manager rules
/// and applies outcomes already calculated by trusted server policy. This
/// Project-authored rebirth growth is calculated by
/// <see cref="PetRebirthGrowthPolicy"/>. Unrecovered stock EXP conversion and
/// rank formulas remain outside this planner, and it exposes no packet opcodes.
/// </summary>
internal static class PetManagerPlanner
{
    public const int MaximumSpiritItems = 5;
    public const int MinimumPetMergeLevel = 30;
    public const int MinimumOwnerMergeAmity = 40;
    public const int MaximumPetLevel = 120;
    public const int MaximumPetSkillCount = 6;
    public const int MaximumOwnedPetCount = 8;
    public const int MaximumRebirthCount =
        PetRebirthGrowthPolicy.MaximumRebirthCount;

    public static int RequiredLevelForRebirth(int completedRebirths)
    {
        if (completedRebirths < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(completedRebirths));
        }

        return completedRebirths switch
        {
            0 => 50,
            1 => 80,
            2 => 100,
            3 => 110,
            _ => 120
        };
    }

    public static bool TryToggleOwnerMerge(
        OwnedPet? pet,
        long ownerCharacterId,
        AuthoritativePetOwnerMergeOutcome? outcome,
        out PetOwnerMergePlan? plan,
        out PetPlanRejection rejection)
    {
        plan = null;
        if (!TryValidatePet(pet, ownerCharacterId, out rejection))
        {
            return false;
        }

        if (pet!.OwnerMerge is { } currentMerge)
        {
            var storedSkillIds = currentMerge.GrantedSkillIds.ToArray();
            plan = new PetOwnerMergePlan(
                pet with { OwnerMerge = null },
                IsMerging: false,
                currentMerge.StatContribution,
                storedSkillIds);
            rejection = PetPlanRejection.None;
            return true;
        }

        if (pet.IsAway)
        {
            rejection = PetPlanRejection.PetUnavailable;
            return false;
        }

        if (!pet.IsSummoned)
        {
            rejection = PetPlanRejection.MustBeSummoned;
            return false;
        }

        if (!pet.HasOwnerMergeTalent)
        {
            rejection = PetPlanRejection.OwnerMergeTalentRequired;
            return false;
        }

        if (pet.MaximumEnergy <= 0 || pet.CurrentEnergy != pet.MaximumEnergy)
        {
            rejection = PetPlanRejection.EnergyNotFull;
            return false;
        }

        if (pet.Amity < MinimumOwnerMergeAmity)
        {
            rejection = PetPlanRejection.InsufficientAmity;
            return false;
        }

        if (!IsValidOwnerMergeOutcome(outcome, pet.CurrentEnergy))
        {
            rejection = PetPlanRejection.InvalidAuthoritativeOutcome;
            return false;
        }

        var grantedSkillIds = outcome!.GrantedSkillIds.ToArray();
        var mergeState = new PetOwnerMergeState(
            outcome.StatContribution,
            grantedSkillIds);
        plan = new PetOwnerMergePlan(
            pet with
            {
                CurrentEnergy = outcome.EnergyAfterMerge,
                OwnerMerge = mergeState
            },
            IsMerging: true,
            outcome.StatContribution,
            grantedSkillIds);
        rejection = PetPlanRejection.None;
        return true;
    }

    public static bool TryPlanRebirth(
        OwnedPet? pet,
        long ownerCharacterId,
        PetRebirthMaterials materials,
        AuthoritativePetRebirthOutcome? outcome,
        out PetRebirthPlan? plan,
        out PetPlanRejection rejection)
    {
        plan = null;
        if (!TryValidatePet(pet, ownerCharacterId, out rejection))
        {
            return false;
        }

        if (!HasAuthoritativeRarityAddedSavvyBaseline(pet!))
        {
            rejection = PetPlanRejection.InvalidPetState;
            return false;
        }

        if (!TryValidateRebirthSpiritSelection(
                materials.RebirthSpiritCount,
                materials.RebornHarpyiaCount,
                pet!.IsBound,
                out rejection))
        {
            return false;
        }

        if (pet.IsAway)
        {
            rejection = PetPlanRejection.PetUnavailable;
            return false;
        }

        if (pet.IsMergedWithOwner)
        {
            rejection = PetPlanRejection.AlreadyMergedWithOwner;
            return false;
        }

        if (!pet.IsSummoned)
        {
            rejection = PetPlanRejection.MustBeSummoned;
            return false;
        }

        if (!pet.HasSoulContract)
        {
            rejection = PetPlanRejection.SoulContractRequired;
            return false;
        }

        if (pet.RebirthsRemaining <= 0)
        {
            rejection = PetPlanRejection.NoRebirthsRemaining;
            return false;
        }

        if (pet.CompletedRebirths >= MaximumRebirthCount)
        {
            rejection = PetPlanRejection.MaximumRebirthsReached;
            return false;
        }

        var requiredLevel = RequiredLevelForRebirth(pet.CompletedRebirths);
        if (pet.Level < requiredLevel)
        {
            rejection = PetPlanRejection.LevelTooLow;
            return false;
        }

        if (!IsValidRebirthOutcome(outcome, pet))
        {
            rejection = PetPlanRejection.InvalidAuthoritativeOutcome;
            return false;
        }

        plan = new PetRebirthPlan(
            pet with
            {
                Level = 1,
                Experience = outcome!.CarriedExperience,
                Rank = outcome.RankAfter,
                AddedSavvy = pet.RarityAddedSavvy,
                GrowthAcceleration = outcome.GrowthAcceleration,
                CompletedRebirths = checked(pet.CompletedRebirths + 1),
                RebirthsRemaining = pet.RebirthsRemaining - 1
            },
            materials,
            requiredLevel);
        rejection = PetPlanRejection.None;
        return true;
    }

    public static bool TryPlanPetMerge(
        OwnedPet? primaryPet,
        OwnedPet? deputyPet,
        long ownerCharacterId,
        PetMergeMaterials materials,
        AuthoritativePetMergeOutcome? outcome,
        out PetMergePlan? plan,
        out PetPlanRejection rejection)
    {
        plan = null;
        if (!TryValidatePet(primaryPet, ownerCharacterId, out rejection) ||
            !TryValidatePet(deputyPet, ownerCharacterId, out rejection))
        {
            return false;
        }

        if (!TryValidateSpiritSelection(
                materials.MergedSpiritCount,
                materials.FusedHarpyiaCount,
                primaryPet!.IsBound,
                out rejection))
        {
            return false;
        }

        if (primaryPet.PetId == deputyPet!.PetId)
        {
            rejection = PetPlanRejection.SamePet;
            return false;
        }

        if (primaryPet.IsAway || deputyPet.IsAway)
        {
            rejection = PetPlanRejection.PetUnavailable;
            return false;
        }

        if (primaryPet.IsMergedWithOwner || deputyPet.IsMergedWithOwner)
        {
            rejection = PetPlanRejection.AlreadyMergedWithOwner;
            return false;
        }

        if (!primaryPet.IsSummoned)
        {
            rejection = PetPlanRejection.MustBeSummoned;
            return false;
        }

        if (primaryPet.Level < MinimumPetMergeLevel ||
            deputyPet.Level < MinimumPetMergeLevel)
        {
            rejection = PetPlanRejection.LevelTooLow;
            return false;
        }

        if (!IsValidPetMergeOutcome(outcome, primaryPet))
        {
            rejection = PetPlanRejection.InvalidAuthoritativeOutcome;
            return false;
        }

        plan = new PetMergePlan(
            primaryPet with
            {
                Rank = outcome!.RankAfter,
                InitialSavvy = outcome.InitialSavvyAfter,
                CompletedPetMerges = checked(primaryPet.CompletedPetMerges + 1)
            },
            deputyPet.PetId,
            materials);
        rejection = PetPlanRejection.None;
        return true;
    }

    private static bool TryValidatePet(
        OwnedPet? pet,
        long ownerCharacterId,
        out PetPlanRejection rejection)
    {
        if (pet is null)
        {
            rejection = PetPlanRejection.MissingPet;
            return false;
        }

        if (ownerCharacterId <= 0 || pet.OwnerCharacterId != ownerCharacterId)
        {
            rejection = PetPlanRejection.NotOwned;
            return false;
        }

        if (pet.PetId <= 0 ||
            !PetSpeciesCatalog.TryGet(pet.SpeciesType, out _) ||
            string.IsNullOrWhiteSpace(pet.Name) ||
            pet.Level is < 1 or > MaximumPetLevel ||
            pet.Experience < 0 ||
            pet.Rank < 0m ||
            !PetAptitudeCatalog.TryGet(pet.Aptitude, out _) ||
            !pet.InitialSavvy.IsNonNegative ||
            !pet.AddedSavvy.IsNonNegative ||
            !pet.RarityAddedSavvy.IsNonNegative ||
            !pet.AddedSavvy.IsAtLeast(pet.RarityAddedSavvy) ||
            !pet.GrowthAcceleration.IsNonNegative ||
            pet.CompletedPetMerges < 0 ||
            pet.CompletedRebirths is < 0 or > MaximumRebirthCount ||
            pet.RebirthsRemaining < 0 ||
            pet.CurrentEnergy < 0 ||
            pet.MaximumEnergy < 0 ||
            pet.CurrentEnergy > pet.MaximumEnergy ||
            pet.Amity < 0)
        {
            rejection = PetPlanRejection.InvalidPetState;
            return false;
        }

        rejection = PetPlanRejection.None;
        return true;
    }

    private static bool HasAuthoritativeRarityAddedSavvyBaseline(
        OwnedPet pet)
    {
        if (!PetAddedSavvyPolicy.TryGet(
                pet.Aptitude,
                out var bracket))
        {
            return false;
        }

        var baseline = pet.RarityAddedSavvy;
        if (baseline.Agility <= 0m ||
            baseline.Strength <= 0m ||
            baseline.Accuracy <= 0m ||
            baseline.Technique <= 0m ||
            baseline.Wisdom <= 0m ||
            baseline.Luck <= 0m)
        {
            return false;
        }

        if (baseline.Strength == baseline.Agility &&
            baseline.Accuracy == baseline.Agility &&
            baseline.Technique == baseline.Agility &&
            baseline.Wisdom == baseline.Agility &&
            baseline.Luck == baseline.Agility)
        {
            return false;
        }

        var total =
            baseline.Agility +
            baseline.Strength +
            baseline.Accuracy +
            baseline.Technique +
            baseline.Wisdom +
            baseline.Luck;
        return total >= bracket.MinimumTotalSavvy &&
            total <= bracket.MaximumTotalSavvy;
    }

    private static bool IsValidOwnerMergeOutcome(
        AuthoritativePetOwnerMergeOutcome? outcome,
        int currentEnergy)
    {
        if (outcome is null ||
            !outcome.StatContribution.IsNonNegative ||
            outcome.EnergyAfterMerge < 0 ||
            outcome.EnergyAfterMerge > currentEnergy ||
            outcome.GrantedSkillIds.Count > MaximumPetSkillCount)
        {
            return false;
        }

        var skillIds = new HashSet<int>();
        foreach (var skillId in outcome.GrantedSkillIds)
        {
            if (skillId <= 0 || !skillIds.Add(skillId))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidRebirthOutcome(
        AuthoritativePetRebirthOutcome? outcome,
        OwnedPet pet) =>
        outcome is not null &&
        outcome.CarriedExperience >= 0 &&
        outcome.RankAfter >= pet.Rank &&
        PetRebirthGrowthPolicy.IsValidOutcome(
            checked(pet.CompletedRebirths + 1),
            pet.GrowthAcceleration,
            outcome.GrowthAcceleration);

    private static bool IsValidPetMergeOutcome(
        AuthoritativePetMergeOutcome? outcome,
        OwnedPet primaryPet) =>
        outcome is not null &&
        outcome.RankAfter >= primaryPet.Rank &&
        outcome.InitialSavvyAfter.IsNonNegative &&
        outcome.InitialSavvyAfter.IsAtLeast(primaryPet.InitialSavvy) &&
        (outcome.RankAfter > primaryPet.Rank ||
         outcome.InitialSavvyAfter.HasAnyIncreaseOver(primaryPet.InitialSavvy));

    private static bool TryValidateSpiritSelection(
        int standardCount,
        int restrictedCount,
        bool petIsBound,
        out PetPlanRejection rejection)
    {
        if (standardCount < 0 ||
            restrictedCount < 0 ||
            (long)standardCount + restrictedCount > MaximumSpiritItems)
        {
            rejection = PetPlanRejection.InvalidMaterialCount;
            return false;
        }

        if (restrictedCount > 0 && !petIsBound)
        {
            rejection = PetPlanRejection.RestrictedMaterialRequiresBoundPet;
            return false;
        }

        rejection = PetPlanRejection.None;
        return true;
    }

    private static bool TryValidateRebirthSpiritSelection(
        int standardCount,
        int restrictedCount,
        bool petIsBound,
        out PetPlanRejection rejection)
    {
        if (!TryValidateSpiritSelection(
                standardCount,
                restrictedCount,
                petIsBound,
                out rejection))
        {
            return false;
        }

        if ((long)standardCount + restrictedCount !=
            PetRebirthGrowthPolicy.RequiredSpiritCount)
        {
            rejection = PetPlanRejection.InvalidMaterialCount;
            return false;
        }

        return true;
    }
}
