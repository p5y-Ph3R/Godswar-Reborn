using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Zodiac;

internal enum ZodiacSkillGridSelectionExecutionDisposition : byte
{
    Committed = 1,
    Duplicate,
    TerminalRejected,
    RequestHashConflict,
    InvalidIntent,
    PreconditionFailed
}

internal enum ZodiacSkillGridSelectionReceiptStatus : byte
{
    Succeeded = 1,
    InactiveGrid,
    SkillKindNotAllowedForGrid,
    SkillKindNotAllowedForClass,
    SkillNotLearned,
    DuplicateSkillInRow,
    AlreadySelected
}

internal sealed record ZodiacSkillGridSelectionExecutionReceipt
{
    public ZodiacSkillGridSelectionExecutionReceipt(
        int characterId,
        ZodiacSkillGridSelectionReceiptStatus status,
        int gridIndex,
        byte currentLevel,
        int previousSkillKind,
        int selectedSkillKind,
        long? aggregateRevision,
        string auditReference,
        Guid? outboxEventId)
    {
        if (characterId <= 0 ||
            !Enum.IsDefined(status) ||
            gridIndex is
                < ZodiacSkillGridSelectionCommandEnvelope.MinimumGridIndex or
                > ZodiacSkillGridSelectionCommandEnvelope.MaximumGridIndex ||
            currentLevel > 50 ||
            previousSkillKind <
                ZodiacSkillGridSelectionCommandEnvelope.ClearSelection ||
            selectedSkillKind <
                ZodiacSkillGridSelectionCommandEnvelope.ClearSelection ||
            string.IsNullOrWhiteSpace(auditReference))
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }

        var succeeded =
            status == ZodiacSkillGridSelectionReceiptStatus.Succeeded;
        if (succeeded != aggregateRevision.HasValue ||
            succeeded != outboxEventId.HasValue ||
            aggregateRevision is <= 0 ||
            succeeded && (
                currentLevel == 0 ||
                previousSkillKind == selectedSkillKind) ||
            !succeeded && selectedSkillKind != previousSkillKind)
        {
            throw new ArgumentException(
                "The Zodiac selection receipt is inconsistent.");
        }

        CharacterId = characterId;
        Status = status;
        GridIndex = gridIndex;
        CurrentLevel = currentLevel;
        PreviousSkillKind = previousSkillKind;
        SelectedSkillKind = selectedSkillKind;
        AggregateRevision = aggregateRevision;
        AuditReference = auditReference;
        OutboxEventId = outboxEventId;
    }

    public CommandFamily Family =>
        CommandFamily.ZodiacSkillGridSelection;
    public int CharacterId { get; }
    public ZodiacSkillGridSelectionReceiptStatus Status { get; }
    public int GridIndex { get; }
    public byte CurrentLevel { get; }
    public int PreviousSkillKind { get; }
    public int SelectedSkillKind { get; }
    public long? AggregateRevision { get; }
    public string AuditReference { get; }
    public Guid? OutboxEventId { get; }
    public bool Succeeded =>
        Status == ZodiacSkillGridSelectionReceiptStatus.Succeeded;
}

internal sealed record ZodiacSkillGridSelectionExecutionResult
{
    private ZodiacSkillGridSelectionExecutionResult(
        ZodiacSkillGridSelectionExecutionDisposition disposition,
        ZodiacSkillGridSelectionExecutionReceipt? receipt = null,
        byte currentLevel = 0,
        int selectedSkillKind =
            ZodiacSkillGridSelectionCommandEnvelope.ClearSelection,
        long currentRevision = 0)
    {
        var projected = disposition is
            ZodiacSkillGridSelectionExecutionDisposition.Committed or
            ZodiacSkillGridSelectionExecutionDisposition.Duplicate or
            ZodiacSkillGridSelectionExecutionDisposition.TerminalRejected;
        if (projected != (receipt is not null) ||
            currentLevel > 50 ||
            selectedSkillKind <
                ZodiacSkillGridSelectionCommandEnvelope.ClearSelection ||
            currentRevision < 0 ||
            disposition ==
                ZodiacSkillGridSelectionExecutionDisposition.Committed &&
            (receipt?.Succeeded != true ||
             currentRevision != receipt.AggregateRevision) ||
            disposition ==
                ZodiacSkillGridSelectionExecutionDisposition
                    .TerminalRejected &&
            receipt?.Succeeded != false)
        {
            throw new ArgumentException(
                "The Zodiac selection execution result is inconsistent.");
        }

        Disposition = disposition;
        Receipt = receipt;
        CurrentLevel = currentLevel;
        SelectedSkillKind = selectedSkillKind;
        CurrentRevision = currentRevision;
    }

    public ZodiacSkillGridSelectionExecutionDisposition Disposition { get; }
    public ZodiacSkillGridSelectionExecutionReceipt? Receipt { get; }
    public byte CurrentLevel { get; }
    public int SelectedSkillKind { get; }
    public long CurrentRevision { get; }
    public bool HasAuthoritativeProjection => Receipt is not null;

    public static ZodiacSkillGridSelectionExecutionResult Committed(
        ZodiacSkillGridSelectionExecutionReceipt receipt) =>
        new(
            ZodiacSkillGridSelectionExecutionDisposition.Committed,
            receipt,
            receipt.CurrentLevel,
            receipt.SelectedSkillKind,
            receipt.AggregateRevision!.Value);

    public static ZodiacSkillGridSelectionExecutionResult Duplicate(
        ZodiacSkillGridSelectionExecutionReceipt receipt,
        byte currentLevel,
        int selectedSkillKind,
        long currentRevision) =>
        new(
            ZodiacSkillGridSelectionExecutionDisposition.Duplicate,
            receipt,
            currentLevel,
            selectedSkillKind,
            currentRevision);

    public static ZodiacSkillGridSelectionExecutionResult TerminalRejected(
        ZodiacSkillGridSelectionExecutionReceipt receipt) =>
        new(
            ZodiacSkillGridSelectionExecutionDisposition.TerminalRejected,
            receipt,
            receipt.CurrentLevel,
            receipt.SelectedSkillKind);

    public static ZodiacSkillGridSelectionExecutionResult
        RequestHashConflict() =>
        new(
            ZodiacSkillGridSelectionExecutionDisposition
                .RequestHashConflict);

    public static ZodiacSkillGridSelectionExecutionResult InvalidIntent() =>
        new(ZodiacSkillGridSelectionExecutionDisposition.InvalidIntent);

    public static ZodiacSkillGridSelectionExecutionResult
        PreconditionFailed() =>
        new(
            ZodiacSkillGridSelectionExecutionDisposition
                .PreconditionFailed);
}
