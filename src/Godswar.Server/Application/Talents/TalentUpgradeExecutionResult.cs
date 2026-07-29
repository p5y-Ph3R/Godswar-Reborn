using System.Text;

namespace Godswar.Server.Application.Talents;

internal enum TalentUpgradeExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    RequestHashConflict = 3,
    InvalidIntent = 4,
    PreconditionFailed = 5
}

/// <summary>
/// Canonical durable result stored for both a newly committed command and an
/// exact duplicate. It contains no provider-specific row or transaction data.
/// </summary>
internal sealed record TalentUpgradeExecutionReceipt
{
    public const int MaximumAuditReferenceBytes = 256;

    public TalentUpgradeExecutionReceipt(
        int characterId,
        int talentId,
        int rank,
        int cost,
        int remainingTalentPoints,
        int displayValue,
        long aggregateRevision,
        string auditReference,
        Guid outboxEventId)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(characterId));
        }

        if (talentId is <
                TalentUpgradeCommandEnvelope.MinimumTalentId or
            > TalentUpgradeCommandEnvelope.MaximumTalentId)
        {
            throw new ArgumentOutOfRangeException(nameof(talentId));
        }

        if (rank is <= TalentUpgradeCommandEnvelope.MinimumExpectedRank or
            > TalentUpgradeCommandEnvelope.MaximumExpectedRank + 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rank));
        }

        if (cost <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cost));
        }

        if (remainingTalentPoints < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(remainingTalentPoints));
        }

        if (displayValue <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(displayValue));
        }

        if (aggregateRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(aggregateRevision));
        }

        AuditReference = RequireAuditReference(auditReference);
        if (outboxEventId == Guid.Empty)
        {
            throw new ArgumentException(
                "An outbox event ID is required.",
                nameof(outboxEventId));
        }

        CharacterId = characterId;
        TalentId = talentId;
        Rank = rank;
        Cost = cost;
        RemainingTalentPoints = remainingTalentPoints;
        DisplayValue = displayValue;
        AggregateRevision = aggregateRevision;
        OutboxEventId = outboxEventId;
    }

    public int CharacterId { get; }

    public int TalentId { get; }

    public int Rank { get; }

    public int Cost { get; }

    public int RemainingTalentPoints { get; }

    public int DisplayValue { get; }

    public long AggregateRevision { get; }

    public string AuditReference { get; }

    public Guid OutboxEventId { get; }

    private static string RequireAuditReference(string auditReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(auditReference);
        if (Encoding.UTF8.GetByteCount(auditReference) >
            MaximumAuditReferenceBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(auditReference),
                $"Audit references are limited to " +
                $"{MaximumAuditReferenceBytes} UTF-8 bytes.");
        }

        if (auditReference.Any(char.IsControl))
        {
            throw new ArgumentException(
                "Audit references cannot contain control characters.",
                nameof(auditReference));
        }

        return auditReference;
    }
}

/// <summary>
/// A bounded command outcome. Committed and duplicate outcomes always carry
/// the same canonical durable receipt; rejection outcomes never fabricate one.
/// </summary>
internal sealed record TalentUpgradeExecutionResult
{
    public TalentUpgradeExecutionResult(
        TalentUpgradeExecutionDisposition disposition,
        TalentUpgradeExecutionReceipt? receipt = null,
        int authoritativeRank = 0,
        int authoritativeTalentPoints = 0)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        var requiresReceipt =
            disposition is TalentUpgradeExecutionDisposition.Committed or
                TalentUpgradeExecutionDisposition.Duplicate;
        if (requiresReceipt != (receipt is not null))
        {
            throw new ArgumentException(
                requiresReceipt
                    ? "Successful talent outcomes require a durable receipt."
                    : "Rejected talent outcomes cannot carry a receipt.",
                nameof(receipt));
        }

        if (requiresReceipt &&
            (authoritativeRank < receipt!.Rank ||
             authoritativeRank > TalentProgression.RankCap ||
             authoritativeTalentPoints < 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(authoritativeRank),
                "Successful outcomes require a valid current state.");
        }

        Disposition = disposition;
        Receipt = receipt;
        AuthoritativeRank = authoritativeRank;
        AuthoritativeTalentPoints = authoritativeTalentPoints;
    }

    public TalentUpgradeExecutionDisposition Disposition { get; }

    public TalentUpgradeExecutionReceipt? Receipt { get; }

    public int AuthoritativeRank { get; }

    public int AuthoritativeTalentPoints { get; }

    public bool IsSuccess =>
        Disposition is TalentUpgradeExecutionDisposition.Committed or
            TalentUpgradeExecutionDisposition.Duplicate;

    public static TalentUpgradeExecutionResult Committed(
        TalentUpgradeExecutionReceipt receipt) =>
        new(
            TalentUpgradeExecutionDisposition.Committed,
            receipt ?? throw new ArgumentNullException(nameof(receipt)),
            receipt.Rank,
            receipt.RemainingTalentPoints);

    public static TalentUpgradeExecutionResult Duplicate(
        TalentUpgradeExecutionReceipt receipt,
        int authoritativeRank,
        int authoritativeTalentPoints) =>
        new(
            TalentUpgradeExecutionDisposition.Duplicate,
            receipt ?? throw new ArgumentNullException(nameof(receipt)),
            authoritativeRank,
            authoritativeTalentPoints);

    public static TalentUpgradeExecutionResult RequestHashConflict() =>
        new(TalentUpgradeExecutionDisposition.RequestHashConflict);

    public static TalentUpgradeExecutionResult InvalidIntent() =>
        new(TalentUpgradeExecutionDisposition.InvalidIntent);

    public static TalentUpgradeExecutionResult PreconditionFailed() =>
        new(TalentUpgradeExecutionDisposition.PreconditionFailed);
}
