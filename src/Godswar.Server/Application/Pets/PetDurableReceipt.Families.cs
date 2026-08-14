using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal sealed partial record PetDurableReceipt
{
    private bool StatusMatchesFamily() =>
        Family switch
        {
            CommandFamily.BagItemActivation => MatchesBagActivation(),
            CommandFamily.PetLevelUpgrade =>
                Status is PetDurableReceiptStatus.PetLevelUpgraded or
                    PetDurableReceiptStatus.PetNotFound or
                    PetDurableReceiptStatus.PetUnavailable or
                    PetDurableReceiptStatus.PetMaximumLevel or
                    PetDurableReceiptStatus.PetInsufficientExperience,
            CommandFamily.PetPresenceTransition =>
                Status is PetDurableReceiptStatus.PresenceChanged or
                    PetDurableReceiptStatus.PetNotFound or
                    PetDurableReceiptStatus.PetUnavailable or
                    PetDurableReceiptStatus.PetNotTaken,
            CommandFamily.PetSkillUnlearn =>
                Status is PetDurableReceiptStatus.PetSkillUnlearned or
                    PetDurableReceiptStatus.PetNotTaken or
                    PetDurableReceiptStatus.StrongPurgePotionNotFound or
                    PetDurableReceiptStatus.PetSkillNotFound,
            CommandFamily.PetGrowthReset =>
                Status is PetDurableReceiptStatus.PetGrowthReset or
                    PetDurableReceiptStatus.PetGrowthPreviewed or
                    PetDurableReceiptStatus.PetGrowthAccepted or
                    PetDurableReceiptStatus.PetGrowthPreviewUnavailable or
                    PetDurableReceiptStatus.PetNotTaken or
                    PetDurableReceiptStatus.PhoenixFeatherNotFound,
            CommandFamily.PetBasicSavvyReset =>
                Status is PetDurableReceiptStatus.PetBasicSavvyPreviewed or
                    PetDurableReceiptStatus.PetBasicSavvyAccepted or
                    PetDurableReceiptStatus
                        .PetBasicSavvyPreviewUnavailable or
                    PetDurableReceiptStatus.PetNotTaken or
                    PetDurableReceiptStatus.FairyFeatherNotFound,
            CommandFamily.PetOwnerMergeToggle => MatchesOwnerMerge(),
            CommandFamily.PetToPetMerge => MatchesPetToPetMerge(),
            CommandFamily.PetRebirth => MatchesRebirth(),
            CommandFamily.PetAppearanceChange =>
                Status is PetDurableReceiptStatus.PetAppearanceChanged or
                    PetDurableReceiptStatus.MagicJadeNotFound or
                    PetDurableReceiptStatus.MagicJadeIncompatible or
                    PetDurableReceiptStatus.PetAppearancePetNotSummoned or
                    PetDurableReceiptStatus.PetAppearancePetUnbound or
                    PetDurableReceiptStatus.PetAppearancePetUnavailable,
            CommandFamily.PetBind =>
                Status is PetDurableReceiptStatus.PetBound or
                    PetDurableReceiptStatus.PetAlreadyBound or
                    PetDurableReceiptStatus.PetBindPetNotSummoned,
            CommandFamily.PetSoulContract =>
                Status is PetDurableReceiptStatus.PetSoulContractSigned or
                    PetDurableReceiptStatus.PetSoulContractPetNotFound or
                    PetDurableReceiptStatus.PetSoulContractInvalidState or
                    PetDurableReceiptStatus.PetSoulContractInvalidMaterial or
                    PetDurableReceiptStatus
                        .PetSoulContractInsufficientMaterial or
                    PetDurableReceiptStatus.PetSoulContractPetNotSummoned,
            CommandFamily.PetManagerUtility => MatchesPetManagerUtility(),
            _ => false
        };

    private bool MatchesBagActivation() =>
        Status is PetDurableReceiptStatus.EggHatched or
            PetDurableReceiptStatus.EquipmentEquipped or
            PetDurableReceiptStatus.PetShedExpanded or
            PetDurableReceiptStatus.PetSkillCellMadeAvailable or
            PetDurableReceiptStatus.PetSkillCellOpened or
            PetDurableReceiptStatus.PetSkillLearned or
            PetDurableReceiptStatus.ItemNotFound or
            PetDurableReceiptStatus.UnsupportedItem or
            PetDurableReceiptStatus.EquipmentSlotOccupied or
            PetDurableReceiptStatus.EquipmentRestricted or
            PetDurableReceiptStatus.PetCapacityReached or
            PetDurableReceiptStatus.PetShedMaximumReached or
            PetDurableReceiptStatus.PetSkillCellMaximumReached or
            PetDurableReceiptStatus.PetSkillCellNotAvailable or
            PetDurableReceiptStatus.PetSkillBookWrongSpecies or
            PetDurableReceiptStatus.PetSkillBookAlreadyLearned or
            PetDurableReceiptStatus.PetSkillBookPriorTierRequired or
            PetDurableReceiptStatus.PetSkillBookTraitRequirementNotMet or
            PetDurableReceiptStatus.PetSkillBookNoOpenSlot or
            PetDurableReceiptStatus.PetSkillBookInvalidState or
            PetDurableReceiptStatus.PetNotTaken or
            PetDurableReceiptStatus.PetExperienceAdded or
            PetDurableReceiptStatus.PetExperienceRestrictedPetUnbound or
            PetDurableReceiptStatus.PetExperienceMaximumReached or
            PetDurableReceiptStatus.OwnerMerged or
            PetDurableReceiptStatus.OwnerUnmerged or
            PetDurableReceiptStatus.OwnerMergePetNotFound or
            PetDurableReceiptStatus.OwnerMergePetUnavailable or
            PetDurableReceiptStatus.OwnerMergeMustBeSummoned or
            PetDurableReceiptStatus.OwnerMergeTalentRequired or
            PetDurableReceiptStatus.OwnerMergeEnergyNotFull or
            PetDurableReceiptStatus.OwnerMergeInsufficientAmity or
            PetDurableReceiptStatus.OwnerMergeInvalidState or
            PetDurableReceiptStatus.OwnerMergeCharmInvalid or
            PetDurableReceiptStatus.ConsumableCooldownActive;

    private bool MatchesOwnerMerge() =>
        Status is PetDurableReceiptStatus.OwnerMerged or
            PetDurableReceiptStatus.OwnerUnmerged or
            PetDurableReceiptStatus.OwnerMergePetNotFound or
            PetDurableReceiptStatus.OwnerMergePetUnavailable or
            PetDurableReceiptStatus.OwnerMergeMustBeSummoned or
            PetDurableReceiptStatus.OwnerMergeTalentRequired or
            PetDurableReceiptStatus.OwnerMergeEnergyNotFull or
            PetDurableReceiptStatus.OwnerMergeInsufficientAmity or
            PetDurableReceiptStatus.OwnerMergeInvalidState;

    private bool MatchesPetToPetMerge() =>
        Status is PetDurableReceiptStatus.PetToPetMerged or
            PetDurableReceiptStatus.PetMergePetNotFound or
            PetDurableReceiptStatus.PetMergeSamePet or
            PetDurableReceiptStatus.PetMergePetUnavailable or
            PetDurableReceiptStatus.PetMergeMustBeSummoned or
            PetDurableReceiptStatus.PetMergeLevelTooLow or
            PetDurableReceiptStatus.PetMergeInvalidMaterial or
            PetDurableReceiptStatus.PetMergeInsufficientMaterial or
            PetDurableReceiptStatus
                .PetMergeRestrictedMaterialRequiresBoundPet;

    private bool MatchesRebirth() =>
        Status is PetDurableReceiptStatus.PetReborn or
            PetDurableReceiptStatus.PetRebirthPetNotFound or
            PetDurableReceiptStatus.PetRebirthInvalidState or
            PetDurableReceiptStatus.PetRebirthLevelTooLow or
            PetDurableReceiptStatus.PetRebirthMaximumReached or
            PetDurableReceiptStatus.PetRebirthSoulContractRequired or
            PetDurableReceiptStatus.PetRebirthInvalidMaterial or
            PetDurableReceiptStatus.PetRebirthInsufficientMaterial or
            PetDurableReceiptStatus.PetRebirthRestrictedRequiresBound or
            PetDurableReceiptStatus.PetRebirthConcurrentConflict;

    private bool MatchesPetManagerUtility() =>
        Status is >= PetDurableReceiptStatus.PetGrowthChecked and
                <= PetDurableReceiptStatus.PetManagerConcurrentConflict or
            PetDurableReceiptStatus.PetManagerGenderPetUnbound;
}
