using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Zodiac;

internal sealed record ZodiacSkillGridActivationExecutionReceipt
{
    public const int MaximumAuditReferenceUtf8Bytes = 256;

    public ZodiacSkillGridActivationExecutionReceipt(
        int characterId,
        int gridIndex,
        int goldCost,
        int goldBefore,
        int goldAfter,
        byte currentLevel,
        int selectedSkillId,
        long walletRevision,
        string auditReference,
        Guid outboxEventId)
    {
        if (characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(characterId));
        }
        if (gridIndex is
            < ZodiacSkillGridActivationCommandEnvelope.MinimumGridIndex or
            > ZodiacSkillGridActivationCommandEnvelope.MaximumGridIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(gridIndex));
        }
        if (goldCost < 0 ||
            goldBefore < 0 ||
            goldAfter < 0 ||
            goldBefore < goldCost ||
            goldAfter != goldBefore - goldCost)
        {
            throw new ArgumentOutOfRangeException(nameof(goldCost));
        }
        if (currentLevel !=
            ZodiacSkillGridActivationCommandEnvelope.ActivatedLevel)
        {
            throw new ArgumentOutOfRangeException(nameof(currentLevel));
        }
        if (selectedSkillId <
            ZodiacSkillGridActivationCommandEnvelope.NoSelectedSkillId)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedSkillId));
        }
        if (walletRevision < 0 ||
            goldCost > 0 && walletRevision == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(walletRevision));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(auditReference);
        if (Encoding.UTF8.GetByteCount(auditReference) >
                MaximumAuditReferenceUtf8Bytes ||
            auditReference.Any(char.IsControl))
        {
            throw new ArgumentOutOfRangeException(
                nameof(auditReference));
        }
        if (outboxEventId == Guid.Empty)
        {
            throw new ArgumentException(
                "An outbox event ID is required.",
                nameof(outboxEventId));
        }

        CharacterId = characterId;
        GridIndex = gridIndex;
        GoldCost = goldCost;
        GoldBefore = goldBefore;
        GoldAfter = goldAfter;
        CurrentLevel = currentLevel;
        SelectedSkillId = selectedSkillId;
        WalletRevision = walletRevision;
        AuditReference = auditReference;
        OutboxEventId = outboxEventId;
    }

    public CommandFamily Family =>
        CommandFamily.ZodiacSkillGridActivation;
    public int CharacterId { get; }
    public int GridIndex { get; }
    public int GoldCost { get; }
    public int GoldBefore { get; }
    public int GoldAfter { get; }
    public byte CurrentLevel { get; }
    public int SelectedSkillId { get; }
    public long WalletRevision { get; }
    public string AuditReference { get; }
    public Guid OutboxEventId { get; }
}

internal enum ZodiacSkillGridActivationExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    RequestHashConflict = 3,
    InvalidIntent = 4,
    PreconditionFailed = 5
}

internal sealed record ZodiacSkillGridActivationExecutionResult
{
    public ZodiacSkillGridActivationExecutionResult(
        ZodiacSkillGridActivationExecutionDisposition disposition,
        ZodiacSkillGridActivationExecutionReceipt? receipt = null,
        bool hasAuthoritativeProjection = false,
        int currentGold = 0,
        byte currentLevel = 0,
        int selectedSkillId =
            ZodiacSkillGridActivationCommandEnvelope.NoSelectedSkillId,
        long currentWalletRevision = 0)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        var requiresReceipt = disposition is
            ZodiacSkillGridActivationExecutionDisposition.Committed or
            ZodiacSkillGridActivationExecutionDisposition.Duplicate;
        var permitsOptionalProjection = disposition ==
            ZodiacSkillGridActivationExecutionDisposition
                .PreconditionFailed;
        if (requiresReceipt != (receipt is not null) ||
            requiresReceipt && !hasAuthoritativeProjection ||
            !requiresReceipt &&
                !permitsOptionalProjection &&
                hasAuthoritativeProjection)
        {
            throw new ArgumentException(
                "The activation result evidence does not match its " +
                "disposition.");
        }
        if (hasAuthoritativeProjection &&
            (currentGold < 0 ||
             currentLevel >
                ZodiacSkillGridActivationCommandEnvelope.MaximumGridLevel ||
             selectedSkillId <
                ZodiacSkillGridActivationCommandEnvelope.NoSelectedSkillId ||
             currentWalletRevision < 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentGold));
        }
        if (disposition ==
                ZodiacSkillGridActivationExecutionDisposition.Committed &&
            (currentGold != receipt!.GoldAfter ||
             currentLevel != receipt.CurrentLevel ||
             selectedSkillId != receipt.SelectedSkillId ||
             currentWalletRevision != receipt.WalletRevision))
        {
            throw new ArgumentException(
                "A committed projection must match its receipt.");
        }
        if (disposition ==
                ZodiacSkillGridActivationExecutionDisposition.Duplicate &&
            (currentLevel <
                ZodiacSkillGridActivationCommandEnvelope.ActivatedLevel ||
             currentWalletRevision < receipt!.WalletRevision))
        {
            throw new ArgumentException(
                "A duplicate projection cannot predate its receipt.");
        }

        Disposition = disposition;
        Receipt = receipt;
        HasAuthoritativeProjection = hasAuthoritativeProjection;
        CurrentGold = currentGold;
        CurrentLevel = currentLevel;
        SelectedSkillId = selectedSkillId;
        CurrentWalletRevision = currentWalletRevision;
    }

    public ZodiacSkillGridActivationExecutionDisposition Disposition
    {
        get;
    }
    public ZodiacSkillGridActivationExecutionReceipt? Receipt { get; }
    public bool HasAuthoritativeProjection { get; }
    public int CurrentGold { get; }
    public byte CurrentLevel { get; }
    public int SelectedSkillId { get; }
    public long CurrentWalletRevision { get; }
    public bool IsDurable => Receipt is not null;
    public bool IsSuccess => Disposition is
        ZodiacSkillGridActivationExecutionDisposition.Committed or
        ZodiacSkillGridActivationExecutionDisposition.Duplicate;

    public static ZodiacSkillGridActivationExecutionResult Committed(
        ZodiacSkillGridActivationExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new(
            ZodiacSkillGridActivationExecutionDisposition.Committed,
            receipt,
            hasAuthoritativeProjection: true,
            receipt.GoldAfter,
            receipt.CurrentLevel,
            receipt.SelectedSkillId,
            receipt.WalletRevision);
    }

    public static ZodiacSkillGridActivationExecutionResult Duplicate(
        ZodiacSkillGridActivationExecutionReceipt receipt,
        int currentGold,
        byte currentLevel,
        int selectedSkillId,
        long currentWalletRevision) =>
        new(
            ZodiacSkillGridActivationExecutionDisposition.Duplicate,
            receipt ?? throw new ArgumentNullException(nameof(receipt)),
            hasAuthoritativeProjection: true,
            currentGold,
            currentLevel,
            selectedSkillId,
            currentWalletRevision);

    public static ZodiacSkillGridActivationExecutionResult
        RequestHashConflict() =>
        new(
            ZodiacSkillGridActivationExecutionDisposition
                .RequestHashConflict);

    public static ZodiacSkillGridActivationExecutionResult
        InvalidIntent() =>
        new(
            ZodiacSkillGridActivationExecutionDisposition.InvalidIntent);

    public static ZodiacSkillGridActivationExecutionResult
        PreconditionFailed() =>
        new(
            ZodiacSkillGridActivationExecutionDisposition
                .PreconditionFailed);

    public static ZodiacSkillGridActivationExecutionResult
        PreconditionFailed(
            int currentGold,
            byte currentLevel,
            int selectedSkillId,
            long currentWalletRevision) =>
        new(
            ZodiacSkillGridActivationExecutionDisposition
                .PreconditionFailed,
            receipt: null,
            hasAuthoritativeProjection: true,
            currentGold,
            currentLevel,
            selectedSkillId,
            currentWalletRevision);
}
