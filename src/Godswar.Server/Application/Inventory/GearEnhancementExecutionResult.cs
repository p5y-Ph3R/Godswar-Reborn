using System.Collections.Immutable;
using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Inventory;

internal readonly record struct GearEnhancementReceiptMutation(
    GearEnhancementCommandItemRole Role,
    int KitBagSlot,
    uint ItemId,
    string BeforeCompactItemState,
    string AfterCompactItemState);

internal sealed record GearEnhancementExecutionReceipt
{
    public const int MaximumCompactItemStateUtf8Bytes = 512;
    public const int MaximumAuditReferenceUtf8Bytes = 256;

    public GearEnhancementExecutionReceipt(
        int characterId,
        GearEnhancementCommandOperation operation,
        int npcId,
        int dialogIndex,
        GearEnhancementCommandResultStatus status,
        int nativeResultSubId,
        IReadOnlyList<GearEnhancementReceiptMutation> mutations,
        long inventoryRevision,
        string auditReference,
        Guid? outboxEventId)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }
        if (!Enum.IsDefined(operation) ||
            !GearEnhancementCommandEnvelope.IsEndpoint(
                npcId,
                dialogIndex))
        {
            throw new ArgumentException(
                "The receipt operation or endpoint is invalid.");
        }
        if (!Enum.IsDefined(status) ||
            !GearEnhancementNativeResults.IsReachable(operation, status))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }
        if (nativeResultSubId !=
            GearEnhancementNativeResults.GetResultSubId(
                operation,
                status))
        {
            throw new ArgumentException(
                "The native result does not match the operation and status.",
                nameof(nativeResultSubId));
        }

        Mutations = CopyAndValidateMutations(status, mutations);
        if (inventoryRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inventoryRevision));
        }

        AuditReference = RequireAuditReference(auditReference);
        var succeeded =
            status == GearEnhancementCommandResultStatus.Succeeded;
        if (succeeded)
        {
            if (!outboxEventId.HasValue ||
                outboxEventId.Value == Guid.Empty ||
                inventoryRevision == 0)
            {
                throw new ArgumentException(
                    "A successful Gear Enhancement receipt requires a " +
                    "revision and outbox event.");
            }
        }
        else if (outboxEventId is not null)
        {
            throw new ArgumentException(
                "A rejected Gear Enhancement command cannot publish an " +
                "inventory event.",
                nameof(outboxEventId));
        }

        CharacterId = characterId;
        Operation = operation;
        NpcId = npcId;
        DialogIndex = dialogIndex;
        Status = status;
        NativeResultSubId = nativeResultSubId;
        InventoryRevision = inventoryRevision;
        OutboxEventId = outboxEventId;
    }

    public CommandFamily Family =>
        GearEnhancementCommandEnvelope.Family(Operation);

    public int CharacterId { get; }

    public GearEnhancementCommandOperation Operation { get; }

    public int NpcId { get; }

    public int DialogIndex { get; }

    public GearEnhancementCommandResultStatus Status { get; }

    public int NativeResultSubId { get; }

    public ImmutableArray<GearEnhancementReceiptMutation> Mutations
    {
        get;
    }

    public long InventoryRevision { get; }

    public string AuditReference { get; }

    public Guid? OutboxEventId { get; }

    private static ImmutableArray<GearEnhancementReceiptMutation>
        CopyAndValidateMutations(
            GearEnhancementCommandResultStatus status,
            IReadOnlyList<GearEnhancementReceiptMutation>? mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        var copy = ImmutableArray.CreateRange(mutations);
        if (status != GearEnhancementCommandResultStatus.Succeeded)
        {
            if (!copy.IsEmpty)
            {
                throw new ArgumentException(
                    "A rejected receipt cannot contain item mutations.",
                    nameof(mutations));
            }

            return copy;
        }

        if (copy.Length != 3 ||
            copy[0].Role != GearEnhancementCommandItemRole.Gear ||
            copy[1].Role != GearEnhancementCommandItemRole.Catalyst ||
            copy[2].Role != GearEnhancementCommandItemRole.AttributeStone ||
            copy.Select(static value => value.KitBagSlot)
                .Distinct()
                .Count() != copy.Length)
        {
            throw new ArgumentException(
                "A successful receipt requires Gear, Catalyst, and Attribute " +
                "Stone mutations in role order.",
                nameof(mutations));
        }

        foreach (var mutation in copy)
        {
            if (mutation.KitBagSlot is
                    < GearEnhancementCommandEnvelope.MinimumKitBagSlot or
                    > GearEnhancementCommandEnvelope.MaximumKitBagSlot ||
                mutation.ItemId == 0 ||
                !IsBoundedCompactState(
                    mutation.BeforeCompactItemState,
                    allowEmpty: false) ||
                !IsBoundedCompactState(
                    mutation.AfterCompactItemState,
                    allowEmpty:
                        mutation.Role !=
                            GearEnhancementCommandItemRole.Gear))
            {
                throw new ArgumentException(
                    "The receipt contains invalid mutation evidence.",
                    nameof(mutations));
            }
        }

        return copy;
    }

    private static bool IsBoundedCompactState(
        string? value,
        bool allowEmpty)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl) ||
            (!allowEmpty && value == "[]"))
        {
            return false;
        }

        return Encoding.UTF8.GetByteCount(value) <=
            MaximumCompactItemStateUtf8Bytes;
    }

    private static string RequireAuditReference(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(value) >
                MaximumAuditReferenceUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return value;
    }
}

internal enum GearEnhancementExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    ReplayNotFound = 4,
    RequestHashConflict = 5,
    InvalidIntent = 6,
    PreconditionFailed = 7
}

internal sealed record GearEnhancementExecutionResult
{
    private GearEnhancementExecutionResult(
        GearEnhancementExecutionDisposition disposition,
        GearEnhancementExecutionReceipt? receipt = null)
    {
        var needsReceipt = disposition is
            GearEnhancementExecutionDisposition.Committed or
            GearEnhancementExecutionDisposition.Duplicate or
            GearEnhancementExecutionDisposition.TerminalRejected;
        if (!Enum.IsDefined(disposition) ||
            needsReceipt != (receipt is not null))
        {
            throw new ArgumentException(
                "The execution disposition and receipt are inconsistent.");
        }
        if (disposition == GearEnhancementExecutionDisposition.Committed &&
            receipt!.Status !=
                GearEnhancementCommandResultStatus.Succeeded ||
            disposition ==
                GearEnhancementExecutionDisposition.TerminalRejected &&
            receipt!.Status ==
                GearEnhancementCommandResultStatus.Succeeded)
        {
            throw new ArgumentException(
                "The receipt status does not match its disposition.",
                nameof(receipt));
        }

        Disposition = disposition;
        Receipt = receipt;
    }

    public GearEnhancementExecutionDisposition Disposition { get; }

    public GearEnhancementExecutionReceipt? Receipt { get; }

    public bool IsDurable => Receipt is not null;

    public bool IsSuccess =>
        Receipt?.Status ==
            GearEnhancementCommandResultStatus.Succeeded &&
        Disposition is GearEnhancementExecutionDisposition.Committed or
            GearEnhancementExecutionDisposition.Duplicate;

    public static GearEnhancementExecutionResult Committed(
        GearEnhancementExecutionReceipt receipt) =>
        new(
            GearEnhancementExecutionDisposition.Committed,
            receipt);

    public static GearEnhancementExecutionResult Duplicate(
        GearEnhancementExecutionReceipt receipt) =>
        new(
            GearEnhancementExecutionDisposition.Duplicate,
            receipt);

    public static GearEnhancementExecutionResult TerminalRejected(
        GearEnhancementExecutionReceipt receipt) =>
        new(
            GearEnhancementExecutionDisposition.TerminalRejected,
            receipt);

    public static GearEnhancementExecutionResult ReplayNotFound() =>
        new(GearEnhancementExecutionDisposition.ReplayNotFound);

    public static GearEnhancementExecutionResult RequestHashConflict() =>
        new(GearEnhancementExecutionDisposition.RequestHashConflict);

    public static GearEnhancementExecutionResult InvalidIntent() =>
        new(GearEnhancementExecutionDisposition.InvalidIntent);

    public static GearEnhancementExecutionResult PreconditionFailed() =>
        new(GearEnhancementExecutionDisposition.PreconditionFailed);
}
