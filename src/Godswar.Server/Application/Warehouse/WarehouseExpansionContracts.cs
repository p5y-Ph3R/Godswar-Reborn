using Godswar.Server.Application.Characters;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Warehouse;

internal readonly record struct WarehouseExpansionCommand(
    WarehouseOperationIdentity Identity,
    int RealmId,
    int NpcId,
    int DialogIndex,
    int ActionSubId,
    int ExpectedCapacity,
    int TargetCapacity,
    long PolicyRevision,
    string PolicySha256);

internal readonly record struct WarehouseExpansionReplayIntent(
    int RealmId,
    int ActionSubId);

internal enum WarehouseExpansionResultStatus : byte
{
    Expanded = 1,
    InsufficientKeys = 2,
    AlreadyMaximum = 3,
    CapacityConflict = 4,
    ConcurrentConflict = 5
}

internal sealed record WarehouseExpansionExecutionReceipt(
    int CharacterId,
    int RealmId,
    int ActionSubId,
    WarehouseExpansionResultStatus Status,
    int PreviousCapacity,
    int CurrentCapacity,
    int KeyItemId,
    int RequiredKeyCount,
    int ConsumedKeyCount,
    long PolicyRevision,
    string PolicySha256,
    long WarehouseRevision,
    long InventoryRevision,
    IReadOnlyList<WarehouseItemMutation> KeyMutations,
    string AuditReference,
    Guid? OutboxEventId)
{
    public bool Succeeded =>
        Status == WarehouseExpansionResultStatus.Expanded;

    public void Validate()
    {
        var expectedTarget = checked(
            PreviousCapacity + WarehouseCapacityPolicy.SlotsPerBox);
        if (CharacterId <= 0 ||
            RealmId <= 0 ||
            ActionSubId != WarehouseExpansionCommandEnvelope.ActionSubId ||
            !WarehouseCapacityPolicy.IsValidCapacity(PreviousCapacity) ||
            !WarehouseCapacityPolicy.IsValidCapacity(CurrentCapacity) ||
            KeyItemId <= 0 ||
            RequiredKeyCount is < 0 or >
                WarehouseExpansionPolicySnapshot.MaximumKeyCost ||
            ConsumedKeyCount < 0 ||
            PolicyRevision <= 0 ||
            PolicySha256 is null ||
            PolicySha256.Length != 64 ||
            !PolicySha256.All(Uri.IsHexDigit) ||
            WarehouseRevision < 0 ||
            InventoryRevision < 0 ||
            KeyMutations is null ||
            KeyMutations.Count > 96 ||
            KeyMutations.Any(static mutation =>
                !mutation.IsValid ||
                mutation.BeforeLocation !=
                    WarehouseInventoryLocation.KitBag ||
                mutation.AfterLocation is not null and not
                    WarehouseInventoryLocation.KitBag) ||
            string.IsNullOrWhiteSpace(AuditReference) ||
            AuditReference.Length > 256 ||
            AuditReference.Any(char.IsControl) ||
            Succeeded != (OutboxEventId is { } id && id != Guid.Empty) ||
            Succeeded &&
                (PreviousCapacity >=
                    WarehouseCapacityPolicy.MaximumSupportedCapacity ||
                 CurrentCapacity != expectedTarget ||
                 ConsumedKeyCount != RequiredKeyCount ||
                 KeyMutations.Count == 0 ||
                 WarehouseRevision <= 0 ||
                 InventoryRevision <= 0) ||
            Status == WarehouseExpansionResultStatus.AlreadyMaximum &&
                (RequiredKeyCount != 0 || ConsumedKeyCount != 0) ||
            Status != WarehouseExpansionResultStatus.AlreadyMaximum &&
                RequiredKeyCount == 0 ||
            !Succeeded &&
                (CurrentCapacity != PreviousCapacity ||
                 ConsumedKeyCount != 0 ||
                 KeyMutations.Count != 0))
        {
            throw new InvalidDataException(
                "The warehouse expansion receipt is inconsistent.");
        }
    }
}

internal enum WarehouseExpansionExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    ReplayNotFound = 4,
    RequestHashConflict = 5,
    InvalidIntent = 6,
    PreconditionFailed = 7
}

internal sealed record WarehouseExpansionExecutionResult(
    WarehouseExpansionExecutionDisposition Disposition,
    WarehouseExpansionExecutionReceipt? Receipt)
{
    public bool IsSuccess =>
        Receipt?.Succeeded == true &&
        Disposition is WarehouseExpansionExecutionDisposition.Committed or
            WarehouseExpansionExecutionDisposition.Duplicate;

    public bool IsDurable => Receipt is not null;

    public static WarehouseExpansionExecutionResult Terminal(
        WarehouseExpansionExecutionDisposition disposition,
        WarehouseExpansionExecutionReceipt? receipt = null)
    {
        receipt?.Validate();
        var requiresReceipt = disposition is
            WarehouseExpansionExecutionDisposition.Committed or
            WarehouseExpansionExecutionDisposition.Duplicate or
            WarehouseExpansionExecutionDisposition.TerminalRejected;
        if (requiresReceipt != (receipt is not null) ||
            disposition ==
                WarehouseExpansionExecutionDisposition.Committed &&
            receipt?.Succeeded != true ||
            disposition ==
                WarehouseExpansionExecutionDisposition.TerminalRejected &&
            receipt?.Succeeded != false)
        {
            throw new ArgumentException(
                "The warehouse expansion result is inconsistent.");
        }

        return new(disposition, receipt);
    }
}

internal interface IWarehouseExpansionCommandExecutor
{
    Task<WarehouseExpansionExecutionResult> ExecuteAsync(
        CommandEnvelope<WarehouseExpansionCommand> envelope,
        CancellationToken cancellationToken = default);

    Task<WarehouseExpansionExecutionResult> TryReplayAsync(
        CommandSubject subject,
        PlayerOwnershipFence ownership,
        WarehouseExpansionReplayIntent intent,
        WarehouseOperationIdentity identity,
        CancellationToken cancellationToken = default);
}
