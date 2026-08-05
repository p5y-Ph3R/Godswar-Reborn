using Godswar.Server.Application.Commands;
using Godswar.Server.Domain.Inventory;

namespace Godswar.Server.Application.Inventory;

internal enum HolyStoneCommandResultStatus : byte
{
    Mounted = 1,
    Removed = 2,
    Drilled = 3,
    WrongSelection = 4,
    TargetNotEquipment = 5,
    StoneNotHolyStone = 6,
    SocketNotDrilled = 7,
    StoneMissingSpirit = 8,
    SocketCapacityReached = 9,
    IncompatibleTarget = 10,
    InvalidSocket = 11,
    SocketEmpty = 12,
    BagFull = 13,
    MaximumSockets = 14,
    InsufficientFunds = 15,
    DuplicateSpirit = 16,
    StaleTarget = 17,
    StaleStone = 18,
    TargetMissing = 19,
    StoneMissing = 20,
    DrillPrerequisite = 21,
    Upgraded = 22,
    UpgradeFailedDowngraded = 23,
    UpgradeFailedProtected = 24,
    TargetNotHolyStone = 25,
    EclipseStoneRequired = 26,
    MaximumStoneLevel = 27,
    SignetMismatch = 28,
    CatalystMissing = 29,
    StaleCatalyst = 30,
    EclipseLevel1Missing = 31,
    EclipseLevel2Missing = 32,
    EclipseLevel3Missing = 33,
    SignetProtectionUnavailable = 34,
    Combined = 35,
    CombinationSelectionRequired = 36,
    CombinationNotAllowed = 37,
    SpiritImplemented = 38
}

internal sealed partial record HolyStoneExecutionReceipt
{
    public const int MaximumAuditReferenceUtf8Bytes = 256;
    public const int FirstDrillGoldCost =
        HolyStoneDrillCostPolicy.FirstSocketGoldCost;
    public const int SecondDrillGoldCost =
        HolyStoneDrillCostPolicy.SecondSocketGoldCost;

    public HolyStoneExecutionReceipt(
        int characterId,
        HolyStoneCommandOperation operation,
        int npcId,
        int dialogIndex,
        HolyStoneCommandResultStatus status,
        int nativeResultSubId,
        HolyStoneTargetLocation targetLocation,
        int targetSlot,
        int socketIndex,
        long? targetItemInstanceId,
        string expectedTargetCompactItemState,
        string authoritativeTargetBeforeCompactItemState,
        string authoritativeTargetAfterCompactItemState,
        int stoneKitBagSlot,
        long? stoneItemInstanceId,
        string expectedStoneCompactItemState,
        string authoritativeStoneBeforeCompactItemState,
        string authoritativeStoneAfterCompactItemState,
        int outputKitBagSlot,
        long? outputItemInstanceId,
        string? outputBeforeCompactItemState,
        string? outputAfterCompactItemState,
        int goldSpent,
        int goldBefore,
        int goldAfter,
        long walletRevision,
        long inventoryRevision,
        string auditReference,
        Guid? outboxEventId,
        int catalystKitBagSlot = -1,
        long? catalystItemInstanceId = null,
        string expectedCatalystCompactItemState = "[]",
        string authoritativeCatalystBeforeCompactItemState = "[]",
        string authoritativeCatalystAfterCompactItemState = "[]",
        int? upgradeRoll = null,
        int? upgradeSuccessRate = null,
        HolyStoneCombinationReceiptEvidence? combinationEvidence = null)
    {
        if (characterId <= 0 ||
            !Enum.IsDefined(operation) ||
            !HolyStoneCommandEnvelope.IsEndpoint(
                npcId,
                dialogIndex) ||
            !Enum.IsDefined(status) ||
            !HolyStoneNativeResults.IsReachable(operation, status) ||
            !HolySpiritNativeResult.IsValid(
                operation,
                status,
                nativeResultSubId,
                authoritativeTargetBeforeCompactItemState,
                authoritativeTargetAfterCompactItemState,
                authoritativeStoneBeforeCompactItemState) ||
            !Enum.IsDefined(targetLocation) ||
            !IsValidTargetSlot(targetLocation, targetSlot) ||
            socketIndex is
                < HolyStoneCommandEnvelope.ServerSelectedSocketIndex or
                > HolyStoneCommandEnvelope.MaximumSocketIndex ||
            !IsValidInstanceId(targetItemInstanceId) ||
            !IsValidInstanceId(stoneItemInstanceId) ||
            !IsValidInstanceId(catalystItemInstanceId) ||
            !IsValidInstanceId(outputItemInstanceId) ||
            !IsBoundedCompactState(
                expectedTargetCompactItemState,
                allowEmpty: true) ||
            !IsBoundedCompactState(
                authoritativeTargetBeforeCompactItemState,
                allowEmpty: true) ||
            !IsBoundedCompactState(
                authoritativeTargetAfterCompactItemState,
                allowEmpty: true) ||
            !IsBoundedCompactState(
                expectedStoneCompactItemState,
                allowEmpty: true) ||
            !IsBoundedCompactState(
                authoritativeStoneBeforeCompactItemState,
                allowEmpty: true) ||
            !IsBoundedCompactState(
                authoritativeStoneAfterCompactItemState,
                allowEmpty: true) ||
            !IsBoundedCompactState(
                expectedCatalystCompactItemState,
                allowEmpty: true) ||
            !IsBoundedCompactState(
                authoritativeCatalystBeforeCompactItemState,
                allowEmpty: true) ||
            !IsBoundedCompactState(
                authoritativeCatalystAfterCompactItemState,
                allowEmpty: true) ||
            !IsOptionalCompactState(outputBeforeCompactItemState) ||
            !IsOptionalCompactState(outputAfterCompactItemState) ||
            goldSpent < 0 ||
            goldBefore < 0 ||
            goldAfter < 0 ||
            goldAfter != goldBefore - goldSpent ||
            walletRevision < 0 ||
            inventoryRevision < 0)
        {
            throw new ArgumentException(
                "The Holy Stone receipt contains invalid identity or " +
                "item evidence.");
        }

        ValidateOperationEvidence(
            operation,
            status,
            socketIndex,
            stoneKitBagSlot,
            stoneItemInstanceId,
            expectedStoneCompactItemState,
            authoritativeStoneBeforeCompactItemState,
            authoritativeStoneAfterCompactItemState,
            outputKitBagSlot,
            outputItemInstanceId,
            outputBeforeCompactItemState,
            outputAfterCompactItemState);
        ValidateOutcomeEvidence(
            status,
            targetItemInstanceId,
            inventoryRevision,
            outboxEventId);
        HolyStoneUpgradeReceiptEvidence.Validate(
            operation,
            status,
            authoritativeTargetBeforeCompactItemState,
            authoritativeTargetAfterCompactItemState,
            authoritativeStoneBeforeCompactItemState,
            authoritativeStoneAfterCompactItemState,
            catalystKitBagSlot,
            catalystItemInstanceId,
            expectedCatalystCompactItemState,
            authoritativeCatalystBeforeCompactItemState,
            authoritativeCatalystAfterCompactItemState,
            upgradeRoll,
            upgradeSuccessRate);
        HolyStoneCombinationReceiptEvidence.Validate(
            operation,
            status,
            targetLocation,
            targetSlot,
            targetItemInstanceId,
            expectedTargetCompactItemState,
            authoritativeTargetBeforeCompactItemState,
            authoritativeTargetAfterCompactItemState,
            stoneKitBagSlot,
            stoneItemInstanceId,
            expectedStoneCompactItemState,
            authoritativeStoneBeforeCompactItemState,
            authoritativeStoneAfterCompactItemState,
            catalystKitBagSlot,
            catalystItemInstanceId,
            expectedCatalystCompactItemState,
            authoritativeCatalystBeforeCompactItemState,
            authoritativeCatalystAfterCompactItemState,
            combinationEvidence);
        HolySpiritImplementationReceiptEvidence.Validate(
            operation,
            status,
            authoritativeTargetBeforeCompactItemState,
            authoritativeTargetAfterCompactItemState,
            authoritativeStoneBeforeCompactItemState,
            authoritativeStoneAfterCompactItemState,
            catalystKitBagSlot,
            catalystItemInstanceId,
            expectedCatalystCompactItemState,
            authoritativeCatalystBeforeCompactItemState,
            authoritativeCatalystAfterCompactItemState);
        ValidateWalletEvidence(
            operation,
            status,
            authoritativeTargetBeforeCompactItemState,
            goldSpent,
            walletRevision);
        ValidateAuditReference(auditReference);

        CharacterId = characterId;
        Operation = operation;
        NpcId = npcId;
        DialogIndex = dialogIndex;
        Status = status;
        NativeResultSubId = nativeResultSubId;
        TargetLocation = targetLocation;
        TargetSlot = targetSlot;
        SocketIndex = socketIndex;
        TargetItemInstanceId = targetItemInstanceId;
        ExpectedTargetCompactItemState =
            expectedTargetCompactItemState;
        AuthoritativeTargetBeforeCompactItemState =
            authoritativeTargetBeforeCompactItemState;
        AuthoritativeTargetAfterCompactItemState =
            authoritativeTargetAfterCompactItemState;
        StoneKitBagSlot = stoneKitBagSlot;
        StoneItemInstanceId = stoneItemInstanceId;
        ExpectedStoneCompactItemState =
            expectedStoneCompactItemState;
        AuthoritativeStoneBeforeCompactItemState =
            authoritativeStoneBeforeCompactItemState;
        AuthoritativeStoneAfterCompactItemState =
            authoritativeStoneAfterCompactItemState;
        OutputKitBagSlot = outputKitBagSlot;
        OutputItemInstanceId = outputItemInstanceId;
        OutputBeforeCompactItemState =
            outputBeforeCompactItemState;
        OutputAfterCompactItemState =
            outputAfterCompactItemState;
        GoldSpent = goldSpent;
        GoldBefore = goldBefore;
        GoldAfter = goldAfter;
        WalletRevision = walletRevision;
        InventoryRevision = inventoryRevision;
        AuditReference = auditReference;
        OutboxEventId = outboxEventId;
        CatalystKitBagSlot = catalystKitBagSlot;
        CatalystItemInstanceId = catalystItemInstanceId;
        ExpectedCatalystCompactItemState =
            expectedCatalystCompactItemState;
        AuthoritativeCatalystBeforeCompactItemState =
            authoritativeCatalystBeforeCompactItemState;
        AuthoritativeCatalystAfterCompactItemState =
            authoritativeCatalystAfterCompactItemState;
        UpgradeRoll = upgradeRoll;
        UpgradeSuccessRate = upgradeSuccessRate;
        CombinationEvidence = combinationEvidence;
    }

    public CommandFamily Family =>
        HolyStoneCommandEnvelope.Family(Operation);
    public int CharacterId { get; }
    public HolyStoneCommandOperation Operation { get; }
    public int NpcId { get; }
    public int DialogIndex { get; }
    public HolyStoneCommandResultStatus Status { get; }
    public int NativeResultSubId { get; }
    public HolyStoneTargetLocation TargetLocation { get; }
    public int TargetSlot { get; }
    public int SocketIndex { get; }
    public long? TargetItemInstanceId { get; }
    public string ExpectedTargetCompactItemState { get; }
    public string AuthoritativeTargetBeforeCompactItemState { get; }
    public string AuthoritativeTargetAfterCompactItemState { get; }
    public int StoneKitBagSlot { get; }
    public long? StoneItemInstanceId { get; }
    public string ExpectedStoneCompactItemState { get; }
    public string AuthoritativeStoneBeforeCompactItemState { get; }
    public string AuthoritativeStoneAfterCompactItemState { get; }
    public int OutputKitBagSlot { get; }
    public long? OutputItemInstanceId { get; }
    public string? OutputBeforeCompactItemState { get; }
    public string? OutputAfterCompactItemState { get; }
    public int GoldSpent { get; }
    public int GoldBefore { get; }
    public int GoldAfter { get; }
    public long WalletRevision { get; }
    public long InventoryRevision { get; }
    public string AuditReference { get; }
    public Guid? OutboxEventId { get; }
    public int CatalystKitBagSlot { get; }
    public long? CatalystItemInstanceId { get; }
    public string ExpectedCatalystCompactItemState { get; }
    public string AuthoritativeCatalystBeforeCompactItemState { get; }
    public string AuthoritativeCatalystAfterCompactItemState { get; }
    public int? UpgradeRoll { get; }
    public int? UpgradeSuccessRate { get; }
    public HolyStoneCombinationReceiptEvidence? CombinationEvidence
    {
        get;
    }

    private static void ValidateOperationEvidence(
        HolyStoneCommandOperation operation,
        HolyStoneCommandResultStatus status,
        int socketIndex,
        int stoneKitBagSlot,
        long? stoneItemInstanceId,
        string expectedStone,
        string authoritativeStoneBefore,
        string authoritativeStoneAfter,
        int outputKitBagSlot,
        long? outputItemInstanceId,
        string? outputBefore,
        string? outputAfter)
    {
        if (operation == HolyStoneCommandOperation.Mount)
        {
            if (stoneKitBagSlot is
                    < HolyStoneCommandEnvelope.MinimumKitBagSlot or
                    > HolyStoneCommandEnvelope.MaximumKitBagSlot ||
                outputKitBagSlot != -1 ||
                outputItemInstanceId.HasValue ||
                outputBefore is not null ||
                outputAfter is not null ||
                (status == HolyStoneCommandResultStatus.Mounted &&
                 (socketIndex is
                        < HolyStoneCommandEnvelope.MinimumSocketIndex or
                        > HolyStoneCommandEnvelope.MaximumSocketIndex ||
                  !stoneItemInstanceId.HasValue ||
                  authoritativeStoneBefore == "[]")))
            {
                throw new ArgumentException(
                    "The Mount receipt evidence is invalid.");
            }
            return;
        }

        if (operation == HolyStoneCommandOperation.AdvancedDrill)
        {
            var drilled =
                status == HolyStoneCommandResultStatus.Drilled;
            if (stoneKitBagSlot is
                    < HolyStoneCommandEnvelope.MinimumKitBagSlot or
                    > HolyStoneCommandEnvelope.MaximumKitBagSlot ||
                outputKitBagSlot != -1 ||
                outputItemInstanceId.HasValue ||
                outputBefore is not null ||
                outputAfter is not null ||
                (drilled &&
                 (socketIndex is not (2 or 3) ||
                  !stoneItemInstanceId.HasValue ||
                  authoritativeStoneBefore == "[]")) ||
                (!drilled &&
                 socketIndex !=
                    HolyStoneCommandEnvelope.ServerSelectedSocketIndex))
            {
                throw new ArgumentException(
                    "The Advanced Drill receipt evidence is invalid.");
            }
            return;
        }

        if (operation == HolyStoneCommandOperation.Upgrade)
        {
            var committedOutcome =
                HolyStoneNativeResults.IsSuccess(status);
            if (socketIndex !=
                    HolyStoneCommandEnvelope.ServerSelectedSocketIndex ||
                stoneKitBagSlot is
                    < HolyStoneCommandEnvelope.MinimumKitBagSlot or
                    > HolyStoneCommandEnvelope.MaximumKitBagSlot ||
                outputKitBagSlot != -1 ||
                outputItemInstanceId.HasValue ||
                outputBefore is not null ||
                outputAfter is not null ||
                (committedOutcome &&
                 (!stoneItemInstanceId.HasValue ||
                  authoritativeStoneBefore == "[]")))
            {
                throw new ArgumentException(
                    "The Holy Stone Upgrade receipt evidence is invalid.");
            }
            return;
        }

        if (operation == HolyStoneCommandOperation.Combine)
        {
            var committedOutcome =
                status == HolyStoneCommandResultStatus.Combined;
            if (socketIndex !=
                    HolyStoneCommandEnvelope.ServerSelectedSocketIndex ||
                stoneKitBagSlot is
                    < HolyStoneCommandEnvelope.MinimumKitBagSlot or
                    > HolyStoneCommandEnvelope.MaximumKitBagSlot ||
                outputKitBagSlot != -1 ||
                outputItemInstanceId.HasValue ||
                outputBefore is not null ||
                outputAfter is not null ||
                (committedOutcome &&
                 (!stoneItemInstanceId.HasValue ||
                  authoritativeStoneBefore == "[]")))
            {
                throw new ArgumentException(
                    "The Holy Stone Combination receipt evidence is " +
                    "invalid.");
            }
            return;
        }

        if (operation == HolyStoneCommandOperation.ImplementSpirit)
        {
            var implemented =
                status == HolyStoneCommandResultStatus.SpiritImplemented;
            if (socketIndex !=
                    HolyStoneCommandEnvelope.ServerSelectedSocketIndex ||
                stoneKitBagSlot is
                    < HolyStoneCommandEnvelope.MinimumKitBagSlot or
                    > HolyStoneCommandEnvelope.MaximumKitBagSlot ||
                outputKitBagSlot != -1 ||
                outputItemInstanceId.HasValue ||
                outputBefore is not null ||
                outputAfter is not null ||
                implemented &&
                (!stoneItemInstanceId.HasValue ||
                 authoritativeStoneBefore == "[]"))
            {
                throw new ArgumentException(
                    "Holy Spirit implementation evidence is invalid.");
            }
            return;
        }

        if (stoneKitBagSlot !=
                HolyStoneCommandEnvelope.NoStoneKitBagSlot ||
            stoneItemInstanceId.HasValue ||
            expectedStone != "[]" ||
            authoritativeStoneBefore != "[]" ||
            authoritativeStoneAfter != "[]")
        {
            throw new ArgumentException(
                "Only Mount or Advanced Drill may contain material " +
                "evidence.");
        }

        if (operation == HolyStoneCommandOperation.Remove)
        {
            if (socketIndex is
                    < HolyStoneCommandEnvelope.MinimumSocketIndex or
                    > HolyStoneCommandEnvelope.MaximumSocketIndex)
            {
                throw new ArgumentException(
                    "Remove requires a finite socket index.");
            }

            var removed =
                status == HolyStoneCommandResultStatus.Removed;
            if (removed !=
                (outputKitBagSlot is
                    >= HolyStoneCommandEnvelope.MinimumKitBagSlot and
                    <= HolyStoneCommandEnvelope.MaximumKitBagSlot &&
                 outputItemInstanceId.HasValue &&
                 outputBefore is null &&
                 outputAfter is not null &&
                 outputAfter != "[]"))
            {
                throw new ArgumentException(
                    "The Remove output evidence is invalid.");
            }
            if (!removed &&
                (outputKitBagSlot != -1 ||
                 outputItemInstanceId.HasValue ||
                 outputBefore is not null ||
                 outputAfter is not null))
            {
                throw new ArgumentException(
                    "A rejected Remove cannot contain output evidence.");
            }
            return;
        }

        if (socketIndex !=
                HolyStoneCommandEnvelope.ServerSelectedSocketIndex ||
            outputKitBagSlot != -1 ||
            outputItemInstanceId.HasValue ||
            outputBefore is not null ||
            outputAfter is not null)
        {
            throw new ArgumentException(
                "The Drill receipt evidence is invalid.");
        }
    }

}
