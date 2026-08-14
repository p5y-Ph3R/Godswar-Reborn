using System.Text.Json.Serialization;

namespace Godswar.Server.Application.Pets;

internal sealed record PetManagerGrowthEvidence(
    decimal Agility,
    decimal Strength,
    decimal Accuracy,
    decimal Technique,
    decimal Wisdom,
    decimal Luck)
{
    public IReadOnlyList<decimal> Values =>
        [Agility, Strength, Accuracy, Technique, Wisdom, Luck];

    public bool IsValid => Values.All(static value => value is >= 0 and <= 100m);
}

internal sealed record PetManagerUtilityPetState(
    string ActivityState,
    bool IsCarried,
    bool IsSummoned,
    bool ContributesToCharacter,
    bool GrowthRevealed,
    bool HasSoulContract,
    byte SoulContractStage,
    byte Sex,
    long Revision)
{
    // Added after the original family-55 receipt contract shipped. Nullable
    // fields keep historical inbox/outbox receipts replay-readable while new
    // utility transitions can pin the authoritative energy change.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CurrentEnergy { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaximumEnergy { get; init; }

    [JsonIgnore]
    public bool HasEnergyEvidence =>
        CurrentEnergy.HasValue && MaximumEnergy.HasValue;

    public bool IsValid =>
        ActivityState is "owned" or "sealed" &&
        SoulContractStage <= 6 &&
        Sex <= 1 &&
        Revision > 0 &&
        (CurrentEnergy is null && MaximumEnergy is null ||
         CurrentEnergy is >= 0 && MaximumEnergy is > 0 &&
         CurrentEnergy <= MaximumEnergy);
}

internal sealed record PetManagerUtilityEvidence(
    PetManagerUtilityOperation Operation,
    long PetId,
    int ItemTemplateId,
    long ItemInstanceId,
    int KitBagSlot,
    byte PreviousSex,
    byte NewSex,
    PetManagerGrowthEvidence? Growth,
    PetManagerUtilityPetState? BeforePetState = null,
    PetManagerUtilityPetState? AfterPetState = null)
{
    public bool IsValid =>
        Enum.IsDefined(Operation) &&
        PetId >= 0 &&
        ItemTemplateId >= 0 &&
        ItemInstanceId >= 0 &&
        KitBagSlot is >= -1 and <=
            PetDurableCommandContract.MaximumKitBagSlot &&
        PreviousSex <= 1 &&
        NewSex <= 1 &&
        Growth is not { IsValid: false } &&
        BeforePetState is not { IsValid: false } &&
        AfterPetState is not { IsValid: false };

    public bool MatchesStatus(PetDurableReceiptStatus status)
    {
        PetDurableReceiptStatus? expectedSuccess = Operation switch
        {
            PetManagerUtilityOperation.CheckGrowth =>
                PetDurableReceiptStatus.PetGrowthChecked,
            PetManagerUtilityOperation.Seal =>
                PetDurableReceiptStatus.PetSealed,
            PetManagerUtilityOperation.Unseal =>
                PetDurableReceiptStatus.PetUnsealed,
            PetManagerUtilityOperation.ClaimPetCall =>
                PetDurableReceiptStatus.PetCallClaimed,
            PetManagerUtilityOperation.ClaimMerge =>
                PetDurableReceiptStatus.PetMergeClaimed,
            PetManagerUtilityOperation.ChangeGender =>
                PetDurableReceiptStatus.PetGenderChanged,
            _ => null
        };
        if (expectedSuccess.HasValue && status == expectedSuccess.Value)
        {
            return HasValidSuccessEvidence();
        }

        return (status is >=
                    PetDurableReceiptStatus.PetManagerPetNotSummoned and <=
                    PetDurableReceiptStatus.PetManagerConcurrentConflict ||
                status ==
                    PetDurableReceiptStatus.PetManagerGenderPetUnbound) &&
            Growth is null && AfterPetState is null;
    }

    private bool HasValidSuccessEvidence() =>
        Operation switch
        {
            PetManagerUtilityOperation.CheckGrowth =>
                PetId > 0 && ItemTemplateId == 10106 &&
                ItemInstanceId > 0 &&
                KitBagSlot >= 0 &&
                Growth is { IsValid: true } &&
                BeforePetState is { IsValid: true } before &&
                AfterPetState is { IsValid: true } after &&
                before.ActivityState == "owned" &&
                after.ActivityState == "owned" &&
                after.GrowthRevealed &&
                after.Revision == before.Revision + 1,
            PetManagerUtilityOperation.Seal =>
                PetId > 0 && ItemTemplateId == 10109 &&
                ItemInstanceId > 0 &&
                KitBagSlot >= 0 &&
                Growth is null &&
                BeforePetState is
                    { ActivityState: "owned" } sealBefore &&
                AfterPetState is
                {
                    ActivityState: "sealed",
                    IsCarried: false,
                    IsSummoned: false,
                    ContributesToCharacter: false,
                    HasSoulContract: false,
                    SoulContractStage: 0
                } sealAfter &&
                sealAfter.Revision == sealBefore.Revision + 1,
            PetManagerUtilityOperation.Unseal =>
                PetId > 0 && ItemTemplateId == 10109 &&
                ItemInstanceId > 0 &&
                KitBagSlot >= 0 &&
                Growth is null &&
                BeforePetState is
                    { ActivityState: "sealed" } unsealBefore &&
                AfterPetState is
                {
                    ActivityState: "owned",
                    ContributesToCharacter: false
                } unsealAfter &&
                unsealAfter.IsCarried == unsealAfter.IsSummoned &&
                HasCompatibleUnsealEnergyEvidence(
                    unsealBefore,
                    unsealAfter) &&
                unsealAfter.Revision == unsealBefore.Revision + 1,
            PetManagerUtilityOperation.ClaimPetCall =>
                PetId == 0 && ItemTemplateId == 11003 &&
                ItemInstanceId > 0 &&
                KitBagSlot >= 0 &&
                Growth is null,
            PetManagerUtilityOperation.ClaimMerge =>
                PetId == 0 && ItemTemplateId == 11004 &&
                ItemInstanceId > 0 &&
                KitBagSlot >= 0 &&
                Growth is null,
            PetManagerUtilityOperation.ChangeGender =>
                PetId > 0 && ItemTemplateId == 11015 &&
                ItemInstanceId > 0 &&
                KitBagSlot >= 0 &&
                PreviousSex != NewSex &&
                Growth is null &&
                BeforePetState is { IsValid: true } genderBefore &&
                AfterPetState is { IsValid: true } genderAfter &&
                genderBefore.Sex == PreviousSex &&
                genderAfter.Sex == NewSex &&
                genderAfter.Revision == genderBefore.Revision + 1,
            _ => false
        };

    private static bool HasCompatibleUnsealEnergyEvidence(
        PetManagerUtilityPetState before,
        PetManagerUtilityPetState after)
    {
        // Receipts persisted before energy evidence was introduced have both
        // pairs absent. Accept only that exact legacy shape; any partially
        // populated or contradictory new shape fails closed.
        if (!before.HasEnergyEvidence && !after.HasEnergyEvidence)
        {
            return !after.IsCarried &&
                !after.IsSummoned &&
                before.CurrentEnergy is null &&
                before.MaximumEnergy is null &&
                after.CurrentEnergy is null &&
                after.MaximumEnergy is null;
        }

        return before.HasEnergyEvidence &&
            after.HasEnergyEvidence &&
            before.MaximumEnergy == after.MaximumEnergy &&
            after.CurrentEnergy == after.MaximumEnergy;
    }
}
