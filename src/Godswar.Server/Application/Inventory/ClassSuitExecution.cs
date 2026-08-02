using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal enum ClassSuitCommandResultStatus : byte
{
    Succeeded = 1,
    SelectionMissing = 2,
    StaleSelection = 3,
    InvalidEquipment = 4,
    UnsupportedSource = 5,
    ProfessionMismatch = 6,
    UnsupportedReverseTier = 7,
    ContentMismatch = 8,
    PlayerLevelTooLow = 9,
    InvalidMaterial = 10,
    InsufficientMaterial = 11,
    InsufficientCapacity = 12,
    AttributeAlreadyPresent = 13,
    AttributeMissing = 14,
    AttributeSlotsFull = 15
}

internal readonly record struct ClassSuitReceiptMutation(
    int KitBagSlot,
    uint BeforeItemId,
    uint AfterItemId,
    string BeforeCompactItemState,
    string AfterCompactItemState);

internal sealed record ClassSuitExecutionReceipt(
    CommandFamily Family,
    int CharacterId,
    ClassSuitCommandOperation Operation,
    int NpcId,
    int DialogIndex,
    ClassSuitReplayIntent ReplayIntent,
    ClassSuitCommandResultStatus Status,
    int NativeResultSubId,
    IReadOnlyList<ClassSuitReceiptMutation> Mutations,
    long InventoryRevision,
    string AuditReference,
    Guid? OutboxEventId);

internal enum ClassSuitExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    ReplayNotFound = 4,
    RequestHashConflict = 5,
    InvalidIntent = 6,
    PreconditionFailed = 7
}

internal sealed record ClassSuitExecutionResult
{
    private ClassSuitExecutionResult(
        ClassSuitExecutionDisposition disposition,
        ClassSuitExecutionReceipt? receipt = null)
    {
        Disposition = disposition;
        Receipt = receipt;
    }

    public ClassSuitExecutionDisposition Disposition { get; }

    public ClassSuitExecutionReceipt? Receipt { get; }

    public bool IsDurable => Receipt is not null;

    public static ClassSuitExecutionResult Committed(
        ClassSuitExecutionReceipt receipt) =>
        new(ClassSuitExecutionDisposition.Committed, receipt);

    public static ClassSuitExecutionResult Duplicate(
        ClassSuitExecutionReceipt receipt) =>
        new(ClassSuitExecutionDisposition.Duplicate, receipt);

    public static ClassSuitExecutionResult TerminalRejected(
        ClassSuitExecutionReceipt receipt) =>
        new(ClassSuitExecutionDisposition.TerminalRejected, receipt);

    public static ClassSuitExecutionResult ReplayNotFound() =>
        new(ClassSuitExecutionDisposition.ReplayNotFound);

    public static ClassSuitExecutionResult RequestHashConflict() =>
        new(ClassSuitExecutionDisposition.RequestHashConflict);

    public static ClassSuitExecutionResult InvalidIntent() =>
        new(ClassSuitExecutionDisposition.InvalidIntent);

    public static ClassSuitExecutionResult PreconditionFailed() =>
        new(ClassSuitExecutionDisposition.PreconditionFailed);
}

internal static class ClassSuitNativeResults
{
    public const int GenericWrongSelection = 149;
    public const int UnsupportedFifthAttribute = 159;

    public static int Resolve(
        ClassSuitCommandOperation operation,
        ClassSuitCommandResultStatus status)
    {
        if (status == ClassSuitCommandResultStatus.Succeeded)
        {
            return operation switch
            {
                ClassSuitCommandOperation.ExchangeTierI => 120,
                ClassSuitCommandOperation.AddAttribute => 119,
                ClassSuitCommandOperation.DeleteAttribute => 121,
                ClassSuitCommandOperation.ConvertToCommon => 152,
                ClassSuitCommandOperation.UpgradeTierII => 300,
                ClassSuitCommandOperation.UpgradeTierIII => 157,
                ClassSuitCommandOperation.UpgradeTierIV => 169,
                _ => GenericWrongSelection
            };
        }

        return operation switch
        {
            ClassSuitCommandOperation.ExchangeTierI =>
                ResolveTierOneFailure(status),
            ClassSuitCommandOperation.AddAttribute =>
                ResolveAddAttributeFailure(status),
            ClassSuitCommandOperation.DeleteAttribute =>
                ResolveDeleteAttributeFailure(status),
            ClassSuitCommandOperation.ConvertToCommon =>
                status == ClassSuitCommandResultStatus.InsufficientCapacity
                    ? 151
                    : 150,
            ClassSuitCommandOperation.UpgradeTierII =>
                status is ClassSuitCommandResultStatus.InvalidMaterial or
                    ClassSuitCommandResultStatus.InsufficientMaterial
                    ? 301
                    : status ==
                        ClassSuitCommandResultStatus.InsufficientCapacity
                        ? 302
                        : GenericWrongSelection,
            ClassSuitCommandOperation.UpgradeTierIII =>
                ResolveTierThreeFailure(status),
            ClassSuitCommandOperation.UpgradeTierIV =>
                ResolveTierFourFailure(status),
            _ => GenericWrongSelection
        };
    }

    private static int ResolveTierOneFailure(
        ClassSuitCommandResultStatus status) =>
        status switch
        {
            ClassSuitCommandResultStatus.InvalidMaterial or
                ClassSuitCommandResultStatus.InsufficientMaterial => 148,
            ClassSuitCommandResultStatus.PlayerLevelTooLow => 147,
            ClassSuitCommandResultStatus.SelectionMissing => 146,
            _ => GenericWrongSelection
        };

    private static int ResolveAddAttributeFailure(
        ClassSuitCommandResultStatus status) =>
        status switch
        {
            ClassSuitCommandResultStatus.SelectionMissing => 115,
            ClassSuitCommandResultStatus.InvalidMaterial => 116,
            ClassSuitCommandResultStatus.AttributeAlreadyPresent => 117,
            ClassSuitCommandResultStatus.ProfessionMismatch => 118,
            _ => 123
        };

    private static int ResolveDeleteAttributeFailure(
        ClassSuitCommandResultStatus status) =>
        status switch
        {
            ClassSuitCommandResultStatus.InvalidMaterial or
                ClassSuitCommandResultStatus.SelectionMissing => 120,
            ClassSuitCommandResultStatus.AttributeMissing => 122,
            _ => 123
        };

    private static int ResolveTierThreeFailure(
        ClassSuitCommandResultStatus status) =>
        status switch
        {
            ClassSuitCommandResultStatus.SelectionMissing or
                ClassSuitCommandResultStatus.UnsupportedSource => 153,
            ClassSuitCommandResultStatus.InvalidMaterial => 154,
            ClassSuitCommandResultStatus.InsufficientMaterial => 155,
            ClassSuitCommandResultStatus.PlayerLevelTooLow => 156,
            _ => GenericWrongSelection
        };

    private static int ResolveTierFourFailure(
        ClassSuitCommandResultStatus status) =>
        status switch
        {
            ClassSuitCommandResultStatus.SelectionMissing or
                ClassSuitCommandResultStatus.UnsupportedSource => 165,
            ClassSuitCommandResultStatus.InvalidMaterial => 166,
            ClassSuitCommandResultStatus.InsufficientMaterial => 167,
            ClassSuitCommandResultStatus.PlayerLevelTooLow => 168,
            _ => GenericWrongSelection
        };
}
