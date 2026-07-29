using System.Text;
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
    StoneMissing = 20
}

internal sealed record HolyStoneExecutionReceipt
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
        Guid? outboxEventId)
    {
        if (characterId <= 0 ||
            !Enum.IsDefined(operation) ||
            !HolyStoneCommandEnvelope.IsEndpoint(
                npcId,
                dialogIndex) ||
            !Enum.IsDefined(status) ||
            !HolyStoneNativeResults.IsReachable(operation, status) ||
            nativeResultSubId !=
                HolyStoneNativeResults.GetResultSubId(operation, status) ||
            !Enum.IsDefined(targetLocation) ||
            !IsValidTargetSlot(targetLocation, targetSlot) ||
            socketIndex is
                < HolyStoneCommandEnvelope.ServerSelectedSocketIndex or
                > HolyStoneCommandEnvelope.MaximumSocketIndex ||
            !IsValidInstanceId(targetItemInstanceId) ||
            !IsValidInstanceId(stoneItemInstanceId) ||
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

        if (stoneKitBagSlot !=
                HolyStoneCommandEnvelope.NoStoneKitBagSlot ||
            stoneItemInstanceId.HasValue ||
            expectedStone != "[]" ||
            authoritativeStoneBefore != "[]" ||
            authoritativeStoneAfter != "[]")
        {
            throw new ArgumentException(
                "Only Mount may contain source-stone evidence.");
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

    private static void ValidateOutcomeEvidence(
        HolyStoneCommandResultStatus status,
        long? targetItemInstanceId,
        long inventoryRevision,
        Guid? outboxEventId)
    {
        var success = HolyStoneNativeResults.IsSuccess(status);
        if (success != outboxEventId.HasValue ||
            (success &&
             (outboxEventId == Guid.Empty ||
              !targetItemInstanceId.HasValue ||
              inventoryRevision <= 0)))
        {
            throw new ArgumentException(
                "Only a successful Holy Stone operation may publish an " +
                "inventory event.");
        }
    }

    private static void ValidateWalletEvidence(
        HolyStoneCommandOperation operation,
        HolyStoneCommandResultStatus status,
        string targetBefore,
        int goldSpent,
        long walletRevision)
    {
        if (status != HolyStoneCommandResultStatus.Drilled)
        {
            if (goldSpent != 0)
            {
                throw new ArgumentException(
                    "Only a successful Drill may spend Gold.");
            }
            return;
        }

        var hasGoldCost =
            HolyStoneDrillCostPolicy
                .TryGetGoldCostFromCompactTargetState(
            targetBefore,
            out var expectedCost);
        if (operation != HolyStoneCommandOperation.Drill ||
            !hasGoldCost ||
            goldSpent != expectedCost ||
            walletRevision <= 0)
        {
            throw new ArgumentException(
                "The Drill Gold evidence is inconsistent.");
        }
    }

    private static bool IsValidTargetSlot(
        HolyStoneTargetLocation location,
        int slot) =>
        location switch
        {
            HolyStoneTargetLocation.Equipment =>
                slot == HolyStoneCommandEnvelope.WeaponEquipmentSlot,
            HolyStoneTargetLocation.KitBag =>
                slot is
                    >= HolyStoneCommandEnvelope.MinimumKitBagSlot and
                    <= HolyStoneCommandEnvelope.MaximumKitBagSlot,
            _ => false
        };

    private static bool IsValidInstanceId(long? value) =>
        !value.HasValue || value.Value > 0;

    private static bool IsOptionalCompactState(string? value) =>
        value is null || IsBoundedCompactState(value, allowEmpty: true);

    private static bool IsBoundedCompactState(
        string? value,
        bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl) ||
            value[0] != '[' ||
            value[^1] != ']' ||
            (!allowEmpty && value == "[]"))
        {
            return false;
        }

        return Encoding.UTF8.GetByteCount(value) <=
            HolyStoneCommandEnvelope.MaximumCompactItemStateUtf8Bytes;
    }

    private static void ValidateAuditReference(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(value) >
                MaximumAuditReferenceUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }
    }
}

internal enum HolyStoneExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    ReplayNotFound = 4,
    RequestHashConflict = 5,
    InvalidIntent = 6,
    PreconditionFailed = 7
}

internal sealed record HolyStoneExecutionResult
{
    private HolyStoneExecutionResult(
        HolyStoneExecutionDisposition disposition,
        HolyStoneExecutionReceipt? receipt = null)
    {
        var needsReceipt = disposition is
            HolyStoneExecutionDisposition.Committed or
            HolyStoneExecutionDisposition.Duplicate or
            HolyStoneExecutionDisposition.TerminalRejected;
        if (!Enum.IsDefined(disposition) ||
            needsReceipt != (receipt is not null) ||
            disposition == HolyStoneExecutionDisposition.Committed &&
            !HolyStoneNativeResults.IsSuccess(receipt!.Status) ||
            disposition == HolyStoneExecutionDisposition.TerminalRejected &&
            HolyStoneNativeResults.IsSuccess(receipt!.Status))
        {
            throw new ArgumentException(
                "The execution disposition and receipt are inconsistent.");
        }

        Disposition = disposition;
        Receipt = receipt;
    }

    public HolyStoneExecutionDisposition Disposition { get; }
    public HolyStoneExecutionReceipt? Receipt { get; }
    public bool IsDurable => Receipt is not null;
    public bool IsSuccess =>
        Receipt is not null &&
        HolyStoneNativeResults.IsSuccess(Receipt.Status) &&
        Disposition is
            HolyStoneExecutionDisposition.Committed or
            HolyStoneExecutionDisposition.Duplicate;

    public static HolyStoneExecutionResult Committed(
        HolyStoneExecutionReceipt receipt) =>
        new(HolyStoneExecutionDisposition.Committed, receipt);
    public static HolyStoneExecutionResult Duplicate(
        HolyStoneExecutionReceipt receipt) =>
        new(HolyStoneExecutionDisposition.Duplicate, receipt);
    public static HolyStoneExecutionResult TerminalRejected(
        HolyStoneExecutionReceipt receipt) =>
        new(HolyStoneExecutionDisposition.TerminalRejected, receipt);
    public static HolyStoneExecutionResult ReplayNotFound() =>
        new(HolyStoneExecutionDisposition.ReplayNotFound);
    public static HolyStoneExecutionResult RequestHashConflict() =>
        new(HolyStoneExecutionDisposition.RequestHashConflict);
    public static HolyStoneExecutionResult InvalidIntent() =>
        new(HolyStoneExecutionDisposition.InvalidIntent);
    public static HolyStoneExecutionResult PreconditionFailed() =>
        new(HolyStoneExecutionDisposition.PreconditionFailed);
}
