using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Pets;

internal enum PetDurableExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    RequestHashConflict = 4,
    InvalidIntent = 5,
    CharacterNotFound = 6
}

internal enum PetDurableReceiptStatus : byte
{
    EggHatched = 1,
    EquipmentEquipped = 2,
    PetLevelUpgraded = 3,
    PresenceChanged = 4,
    PetShedExpanded = 5,
    PetSkillCellMadeAvailable = 6,
    PetSkillCellOpened = 7,
    PetSkillUnlearned = 8,
    PetGrowthReset = 9,
    ItemNotFound = 10,
    UnsupportedItem = 11,
    EquipmentSlotOccupied = 12,
    EquipmentRestricted = 13,
    PetCapacityReached = 14,
    PetNotFound = 15,
    PetUnavailable = 16,
    PetMaximumLevel = 17,
    PetInsufficientExperience = 18,
    PetNotTaken = 19,
    PetShedMaximumReached = 20,
    PetSkillCellMaximumReached = 21,
    PetSkillCellNotAvailable = 22,
    StrongPurgePotionNotFound = 23,
    PetSkillNotFound = 24,
    PhoenixFeatherNotFound = 25,
    OwnerMerged = 26,
    OwnerUnmerged = 27,
    OwnerMergePetNotFound = 28,
    OwnerMergePetUnavailable = 29,
    OwnerMergeMustBeSummoned = 30,
    OwnerMergeTalentRequired = 31,
    OwnerMergeEnergyNotFull = 32,
    OwnerMergeInsufficientAmity = 33,
    OwnerMergeInvalidState = 34,
    OwnerMergeCharmInvalid = 35,
    PetExperienceAdded = 36,
    PetExperienceRestrictedPetUnbound = 37,
    PetExperienceMaximumReached = 38,
    PetToPetMerged = 39,
    PetMergePetNotFound = 40,
    PetMergeSamePet = 41,
    PetMergePetUnavailable = 42,
    PetMergeMustBeSummoned = 43,
    PetMergeLevelTooLow = 44,
    PetMergeInvalidMaterial = 45,
    PetMergeInsufficientMaterial = 46,
    PetMergeRestrictedMaterialRequiresBoundPet = 47,
    PetReborn = 48,
    PetRebirthPetNotFound = 49,
    PetRebirthInvalidState = 50,
    PetRebirthLevelTooLow = 51,
    PetRebirthMaximumReached = 52,
    PetRebirthSoulContractRequired = 53,
    PetRebirthInvalidMaterial = 54,
    PetRebirthInsufficientMaterial = 55,
    PetRebirthRestrictedRequiresBound = 56,
    PetRebirthConcurrentConflict = 57,
    PetGrowthPreviewed = 58,
    PetGrowthAccepted = 59,
    PetGrowthPreviewUnavailable = 60,
    FairyFeatherNotFound = 61,
    PetBasicSavvyPreviewed = 62,
    PetBasicSavvyAccepted = 63,
    PetBasicSavvyPreviewUnavailable = 64,
    PetAppearanceChanged = 65,
    MagicJadeNotFound = 66,
    MagicJadeIncompatible = 67,
    PetAppearancePetNotSummoned = 68,
    PetAppearancePetUnbound = 69,
    PetAppearancePetUnavailable = 70,
    PetBound = 71,
    PetAlreadyBound = 72,
    PetBindPetNotSummoned = 73,
    PetSoulContractSigned = 74,
    PetSoulContractPetNotFound = 75,
    PetSoulContractInvalidState = 76,
    PetSoulContractInvalidMaterial = 77,
    PetSoulContractInsufficientMaterial = 78,
    PetSoulContractPetNotSummoned = 79,
    PetGrowthChecked = 80,
    PetSealed = 81,
    PetUnsealed = 82,
    PetCallClaimed = 83,
    PetMergeClaimed = 84,
    PetGenderChanged = 85,
    PetManagerPetNotSummoned = 86,
    PetManagerPetUnavailable = 87,
    PetManagerMaterialNotFound = 88,
    PetManagerBagFull = 89,
    PetManagerPetBound = 90,
    PetManagerSealedLinkInvalid = 91,
    PetManagerClaimAlreadyHeld = 92,
    PetManagerGenderUnavailable = 93,
    PetManagerMalformedSelection = 94,
    PetManagerConcurrentConflict = 95,
    ConsumableCooldownActive = 96,
    PetManagerGenderPetUnbound = 97,
    PetSkillLearned = 98,
    PetSkillBookWrongSpecies = 99,
    PetSkillBookAlreadyLearned = 100,
    PetSkillBookPriorTierRequired = 101,
    PetSkillBookTraitRequirementNotMet = 102,
    PetSkillBookNoOpenSlot = 103,
    PetSkillBookInvalidState = 104,
    PetCaptured = 105,
    PetCaptureBagFull = 106
}

internal sealed partial record PetDurableReceipt(
    CommandFamily Family,
    PetDurableReceiptStatus Status,
    int AccountId,
    int CharacterId,
    int KitBagSlot,
    int EquipmentSlot,
    long PetId,
    short PetLevel,
    long PetExperience,
    long PetRevision,
    bool IsCarried,
    bool IsSummoned,
    byte PresenceOperation,
    long AggregateRevision,
    string AuditReference,
    Guid? OutboxEventId,
    long DeputyPetId = 0,
    PetToPetMergeDelta? PetMergeDelta = null,
    PetGrowthPreviewSnapshot? GrowthPreview = null,
    PetBasicSavvyPreviewSnapshot? BasicSavvyPreview = null,
    PetHatchRankEvidence? HatchRank = null,
    PetAppearanceChangeEvidence? AppearanceChange = null,
    PetSoulContractEvidence? SoulContract = null,
    PetManagerUtilityEvidence? PetManagerUtility = null,
    PetRebirthGrowthEvidence? RebirthGrowth = null,
    PetSkillLearnEvidence? SkillLearn = null)
{
    public bool Succeeded =>
        Status is PetDurableReceiptStatus.PetCaptured or
            PetDurableReceiptStatus.EggHatched or
            PetDurableReceiptStatus.EquipmentEquipped or
            PetDurableReceiptStatus.PetLevelUpgraded or
            PetDurableReceiptStatus.PresenceChanged or
            PetDurableReceiptStatus.PetShedExpanded or
            PetDurableReceiptStatus.PetSkillCellMadeAvailable or
            PetDurableReceiptStatus.PetSkillCellOpened or
            PetDurableReceiptStatus.PetSkillUnlearned or
            PetDurableReceiptStatus.PetGrowthReset or
            PetDurableReceiptStatus.PetGrowthPreviewed or
            PetDurableReceiptStatus.PetGrowthAccepted or
            PetDurableReceiptStatus.PetBasicSavvyPreviewed or
            PetDurableReceiptStatus.PetBasicSavvyAccepted or
            PetDurableReceiptStatus.PetExperienceAdded or
            PetDurableReceiptStatus.PetToPetMerged or
            PetDurableReceiptStatus.PetReborn or
            PetDurableReceiptStatus.PetSoulContractSigned or
            PetDurableReceiptStatus.PetGrowthChecked or
            PetDurableReceiptStatus.PetSealed or
            PetDurableReceiptStatus.PetUnsealed or
            PetDurableReceiptStatus.PetCallClaimed or
            PetDurableReceiptStatus.PetMergeClaimed or
            PetDurableReceiptStatus.PetGenderChanged or
            PetDurableReceiptStatus.PetAppearanceChanged or
            PetDurableReceiptStatus.PetBound or
            PetDurableReceiptStatus.PetSkillLearned or
            PetDurableReceiptStatus.OwnerMerged or
            PetDurableReceiptStatus.OwnerUnmerged;

    public void Validate()
    {
        if (Family is not (
                CommandFamily.BagItemActivation or
                CommandFamily.PetLevelUpgrade or
                CommandFamily.PetPresenceTransition or
                CommandFamily.PetSkillUnlearn or
                CommandFamily.PetGrowthReset or
                CommandFamily.PetBasicSavvyReset or
                CommandFamily.PetOwnerMergeToggle or
                CommandFamily.PetToPetMerge or
                CommandFamily.PetRebirth or
                CommandFamily.PetAppearanceChange or
                CommandFamily.PetBind or
                CommandFamily.PetSoulContract or
                CommandFamily.PetManagerUtility) ||
            !Enum.IsDefined(Status) ||
            AccountId <= 0 ||
            CharacterId <= 0 ||
            KitBagSlot is < -1 or >
                PetDurableCommandContract.MaximumKitBagSlot ||
            EquipmentSlot is < -1 or > 20 ||
            PetId < 0 ||
            PetLevel is < 0 or > 120 ||
            PetExperience < 0 ||
            PetRevision < 0 ||
            PresenceOperation > 3 ||
            AggregateRevision < 0 ||
            string.IsNullOrWhiteSpace(AuditReference) ||
            AuditReference.Any(char.IsControl) ||
            Succeeded != (OutboxEventId is { } id && id != Guid.Empty))
        {
            throw new InvalidDataException(
                "Pet durable receipt evidence is inconsistent.");
        }
        if (!StatusMatchesFamily() ||
            (Family == CommandFamily.PetPresenceTransition) !=
                (PresenceOperation is >= 1 and <= 3) ||
            Status == PetDurableReceiptStatus.EggHatched &&
                (PetId <= 0 || KitBagSlot < 0) ||
            Status == PetDurableReceiptStatus.PetCaptured &&
                (PetId != 0 || KitBagSlot < 0) ||
            Status == PetDurableReceiptStatus.EquipmentEquipped &&
                (KitBagSlot < 0 || EquipmentSlot < 0) ||
            Status is (
                PetDurableReceiptStatus.PetShedExpanded or
                PetDurableReceiptStatus.PetShedMaximumReached) &&
                KitBagSlot < 0 ||
            Status is (
                PetDurableReceiptStatus.PetSkillCellMadeAvailable or
                PetDurableReceiptStatus.PetSkillCellOpened) &&
                (PetId <= 0 || PetRevision <= 0 || KitBagSlot < 0) ||
            Status == PetDurableReceiptStatus.PetSkillUnlearned &&
                (PetId <= 0 || PetRevision <= 0 || KitBagSlot < 0) ||
            Status == PetDurableReceiptStatus.PetGrowthReset &&
                (PetId <= 0 || PetRevision <= 0 || KitBagSlot < 0) ||
            Status == PetDurableReceiptStatus.PetGrowthPreviewed &&
                (PetId <= 0 || KitBagSlot < 0 ||
                 GrowthPreview is not { IsValid: true } preview ||
                 preview.PetId != PetId ||
                 preview.PetLevel != PetLevel ||
                 preview.ExpectedPetRevision != PetRevision) ||
            Status == PetDurableReceiptStatus.PetGrowthAccepted &&
                (PetId <= 0 || PetRevision <= 0 || KitBagSlot != -1 ||
                 GrowthPreview is not null) ||
            Status == PetDurableReceiptStatus.PetBasicSavvyPreviewed &&
                (PetId <= 0 || KitBagSlot < 0 ||
                 BasicSavvyPreview is not { IsValid: true } savvyPreview ||
                 savvyPreview.PetId != PetId ||
                 savvyPreview.PetLevel != PetLevel ||
                 savvyPreview.ExpectedPetRevision != PetRevision) ||
            Status == PetDurableReceiptStatus.PetBasicSavvyAccepted &&
                (PetId <= 0 || PetRevision <= 0 ||
                 !IsValidAcceptedBasicSavvyEvidence()) ||
            Status == PetDurableReceiptStatus.PetExperienceAdded &&
                (PetId <= 0 || PetRevision <= 0 || KitBagSlot < 0) ||
            Status == PetDurableReceiptStatus.PetToPetMerged &&
                (PetId <= 0 || DeputyPetId <= 0 ||
                 PetId == DeputyPetId || PetRevision <= 0 ||
                 PetMergeDelta is not { } delta || !delta.IsValid) ||
            Status == PetDurableReceiptStatus.PetReborn &&
                (PetId <= 0 || PetRevision <= 0 || KitBagSlot < -1 ||
                  PetLevel != 1 || RebirthGrowth is { IsValid: false }) ||
            Family == CommandFamily.PetToPetMerge &&
                Status != PetDurableReceiptStatus.PetToPetMerged &&
                PetMergeDelta is not null ||
            Family != CommandFamily.PetToPetMerge &&
                (DeputyPetId != 0 || PetMergeDelta is not null) ||
            Family != CommandFamily.PetGrowthReset &&
                GrowthPreview is not null ||
            Family != CommandFamily.PetRebirth &&
                RebirthGrowth is not null ||
            Family == CommandFamily.PetRebirth &&
                Status != PetDurableReceiptStatus.PetReborn &&
                RebirthGrowth is not null ||
            Status == PetDurableReceiptStatus.PetSkillLearned &&
                (PetId <= 0 || PetRevision <= 0 || KitBagSlot < 0 ||
                 !IsCarried ||
                 SkillLearn is not { IsValid: true } learned ||
                 learned.PetId != PetId ||
                 learned.ItemTemplateId == 0) ||
            Status != PetDurableReceiptStatus.PetSkillLearned &&
                SkillLearn is not null ||
            Family == CommandFamily.PetGrowthReset &&
                Status != PetDurableReceiptStatus.PetGrowthPreviewed &&
                GrowthPreview is not null ||
            Family != CommandFamily.PetBasicSavvyReset &&
                BasicSavvyPreview is not null ||
            Family == CommandFamily.PetBasicSavvyReset &&
                Status is not (
                    PetDurableReceiptStatus.PetBasicSavvyPreviewed or
                    PetDurableReceiptStatus.PetBasicSavvyAccepted) &&
                BasicSavvyPreview is not null ||
            HatchRank is { IsValid: false } ||
            HatchRank is not null &&
                (Family != CommandFamily.BagItemActivation ||
                 Status != PetDurableReceiptStatus.EggHatched) ||
            Status == PetDurableReceiptStatus.PetAppearanceChanged &&
                (PetId <= 0 || PetRevision <= 0 || KitBagSlot < 0 ||
                 !IsCarried || !IsSummoned ||
                 AppearanceChange is not { IsValid: true } appearance ||
                 appearance.KitBagSlot != KitBagSlot) ||
            Status == PetDurableReceiptStatus.PetBound &&
                (PetId <= 0 || PetRevision <= 0 ||
                 !IsCarried || !IsSummoned || KitBagSlot != -1) ||
            Status == PetDurableReceiptStatus.PetSoulContractSigned &&
                (PetId <= 0 || PetRevision <= 0 ||
                 !IsCarried || !IsSummoned ||
                 SoulContract is not { IsValid: true } contract ||
                 contract.NewStage is < 1 or > 6 ||
                 contract.PetId != PetId) ||
            Family != CommandFamily.PetSoulContract &&
                SoulContract is not null ||
            Family == CommandFamily.PetSoulContract &&
                Status != PetDurableReceiptStatus.PetSoulContractSigned &&
                SoulContract is not null ||
            Family != CommandFamily.PetManagerUtility &&
                PetManagerUtility is not null ||
            Family == CommandFamily.PetManagerUtility &&
                (PetManagerUtility is not { IsValid: true } utility ||
                 utility.PetId != PetId ||
                  !utility.MatchesStatus(Status) ||
                  Succeeded &&
                     (utility.AfterPetState is { } utilityAfter
                        ? PetRevision != utilityAfter.Revision ||
                          IsCarried != utilityAfter.IsCarried ||
                          IsSummoned != utilityAfter.IsSummoned
                        : PetRevision != 0)) ||
            Family != CommandFamily.PetAppearanceChange &&
                AppearanceChange is not null ||
            Family == CommandFamily.PetAppearanceChange &&
                Status != PetDurableReceiptStatus.PetAppearanceChanged &&
                AppearanceChange is not null ||
            Status is (
                PetDurableReceiptStatus.OwnerMerged or
                PetDurableReceiptStatus.OwnerUnmerged) &&
                (PetId <= 0 || PetRevision <= 0 ||
                 Family == CommandFamily.BagItemActivation &&
                    KitBagSlot < 0 ||
                 Family == CommandFamily.PetOwnerMergeToggle &&
                    KitBagSlot != -1) ||
            Status == PetDurableReceiptStatus.PetLevelUpgraded &&
                (PetId <= 0 || PetLevel <= 1 || PetRevision <= 0) ||
            Status == PetDurableReceiptStatus.PresenceChanged &&
                (PetId <= 0 || PetRevision <= 0))
        {
            throw new InvalidDataException(
                "Pet durable receipt status does not match its family.");
        }

    }

    private bool IsValidAcceptedBasicSavvyEvidence()
    {
        // Contract-v2 receipts written by the former two-phase flow contain
        // no item slot or roll on Accept. Keep them replay-decodable during a
        // rolling upgrade. New one-phase receipts prove both the consumed bag
        // slot and the exact committed six-stat roll.
        if (KitBagSlot == -1 && BasicSavvyPreview is null)
        {
            return true;
        }

        return KitBagSlot >= 0 &&
            BasicSavvyPreview is { IsValid: true } committedRoll &&
            committedRoll.PetId == PetId &&
            committedRoll.PetLevel == PetLevel &&
            committedRoll.ExpectedPetRevision == PetRevision;
    }

}

internal sealed record PetDurableExecutionResult(
    PetDurableExecutionDisposition Disposition,
    PetDurableReceipt? Receipt)
{
    public bool IsDurable => Receipt is not null;
    public bool IsSuccess =>
        Receipt?.Succeeded == true &&
        Disposition is PetDurableExecutionDisposition.Committed or
            PetDurableExecutionDisposition.Duplicate;

    public static PetDurableExecutionResult Committed(
        PetDurableReceipt receipt) =>
        Create(PetDurableExecutionDisposition.Committed, receipt);

    public static PetDurableExecutionResult Duplicate(
        PetDurableReceipt receipt) =>
        Create(PetDurableExecutionDisposition.Duplicate, receipt);

    public static PetDurableExecutionResult Rejected(
        PetDurableReceipt receipt) =>
        Create(
            PetDurableExecutionDisposition.TerminalRejected,
            receipt);

    public static PetDurableExecutionResult NonDurable(
        PetDurableExecutionDisposition disposition) =>
        Create(disposition, null);

    private static PetDurableExecutionResult Create(
        PetDurableExecutionDisposition disposition,
        PetDurableReceipt? receipt)
    {
        receipt?.Validate();
        var requiresReceipt = disposition is
            PetDurableExecutionDisposition.Committed or
            PetDurableExecutionDisposition.Duplicate or
            PetDurableExecutionDisposition.TerminalRejected;
        if (requiresReceipt != (receipt is not null) ||
            disposition == PetDurableExecutionDisposition.Committed &&
                receipt?.Succeeded != true ||
            disposition ==
                PetDurableExecutionDisposition.TerminalRejected &&
                receipt?.Succeeded != false)
        {
            throw new ArgumentException(
                "Pet durable execution evidence is invalid.");
        }

        return new(disposition, receipt);
    }
}
