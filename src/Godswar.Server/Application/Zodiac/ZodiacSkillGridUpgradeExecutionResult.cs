namespace Godswar.Server.Application.Zodiac;

internal enum ZodiacSkillGridUpgradeExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    TerminalRejected = 3,
    RequestHashConflict = 4,
    InvalidIntent = 5,
    PreconditionFailed = 6
}

internal sealed record ZodiacSkillGridUpgradeExecutionResult
{
    private ZodiacSkillGridUpgradeExecutionResult(
        ZodiacSkillGridUpgradeExecutionDisposition disposition,
        ZodiacSkillGridUpgradeExecutionReceipt? receipt = null,
        bool hasAuthoritativeProjection = false,
        int currentEnergy = 0,
        int currentEnergyRemainderX100 = 0,
        int currentTalentPoints = 0,
        byte currentLevel = 0,
        int selectedSkillId =
            ZodiacSkillGridUpgradeCommandEnvelope.NoSelectedSkillId)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        var requiresReceipt = disposition is
            ZodiacSkillGridUpgradeExecutionDisposition.Committed or
            ZodiacSkillGridUpgradeExecutionDisposition.Duplicate or
            ZodiacSkillGridUpgradeExecutionDisposition.TerminalRejected;
        if (requiresReceipt != (receipt is not null) ||
            requiresReceipt && !hasAuthoritativeProjection ||
            !requiresReceipt && hasAuthoritativeProjection ||
            disposition ==
                ZodiacSkillGridUpgradeExecutionDisposition.Committed &&
                !receipt!.Succeeded ||
            disposition ==
                ZodiacSkillGridUpgradeExecutionDisposition
                    .TerminalRejected &&
                receipt!.Succeeded)
        {
            throw new ArgumentException(
                "The upgrade result evidence does not match its disposition.");
        }
        if (hasAuthoritativeProjection &&
            (currentEnergy < 0 ||
             currentEnergyRemainderX100 is < 0 or > 99 ||
             currentTalentPoints < 0 ||
             currentLevel >
                ZodiacSkillGridUpgradeCommandEnvelope.MaximumGridLevel ||
             selectedSkillId <
                ZodiacSkillGridUpgradeCommandEnvelope.NoSelectedSkillId))
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentEnergy));
        }
        if (disposition is
                ZodiacSkillGridUpgradeExecutionDisposition.Committed or
                ZodiacSkillGridUpgradeExecutionDisposition
                    .TerminalRejected &&
            !ProjectionMatchesReceipt(
                receipt!,
                currentEnergy,
                currentEnergyRemainderX100,
                currentTalentPoints,
                currentLevel,
                selectedSkillId))
        {
            throw new ArgumentException(
                "A new durable projection must match its receipt.");
        }
        if (disposition ==
                ZodiacSkillGridUpgradeExecutionDisposition.Duplicate &&
            currentLevel < receipt!.CurrentLevel)
        {
            throw new ArgumentException(
                "A duplicate projection cannot predate its receipt.");
        }

        Disposition = disposition;
        Receipt = receipt;
        HasAuthoritativeProjection = hasAuthoritativeProjection;
        CurrentEnergy = currentEnergy;
        CurrentEnergyRemainderX100 = currentEnergyRemainderX100;
        CurrentTalentPoints = currentTalentPoints;
        CurrentLevel = currentLevel;
        SelectedSkillId = selectedSkillId;
    }

    public ZodiacSkillGridUpgradeExecutionDisposition Disposition { get; }
    public ZodiacSkillGridUpgradeExecutionReceipt? Receipt { get; }
    public bool HasAuthoritativeProjection { get; }
    public int CurrentEnergy { get; }
    public int CurrentEnergyRemainderX100 { get; }
    public int CurrentTalentPoints { get; }
    public byte CurrentLevel { get; }
    public int SelectedSkillId { get; }
    public bool IsDurable => Receipt is not null;
    public bool IsSuccess =>
        Receipt?.Succeeded == true &&
        Disposition is
            ZodiacSkillGridUpgradeExecutionDisposition.Committed or
            ZodiacSkillGridUpgradeExecutionDisposition.Duplicate;

    public static ZodiacSkillGridUpgradeExecutionResult Committed(
        ZodiacSkillGridUpgradeExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return CreateFromReceipt(
            ZodiacSkillGridUpgradeExecutionDisposition.Committed,
            receipt);
    }

    public static ZodiacSkillGridUpgradeExecutionResult TerminalRejected(
        ZodiacSkillGridUpgradeExecutionReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return CreateFromReceipt(
            ZodiacSkillGridUpgradeExecutionDisposition.TerminalRejected,
            receipt);
    }

    public static ZodiacSkillGridUpgradeExecutionResult Duplicate(
        ZodiacSkillGridUpgradeExecutionReceipt receipt,
        int currentEnergy,
        int currentEnergyRemainderX100,
        int currentTalentPoints,
        byte currentLevel,
        int selectedSkillId) =>
        new(
            ZodiacSkillGridUpgradeExecutionDisposition.Duplicate,
            receipt ?? throw new ArgumentNullException(nameof(receipt)),
            hasAuthoritativeProjection: true,
            currentEnergy,
            currentEnergyRemainderX100,
            currentTalentPoints,
            currentLevel,
            selectedSkillId);

    public static ZodiacSkillGridUpgradeExecutionResult
        RequestHashConflict() =>
        new(
            ZodiacSkillGridUpgradeExecutionDisposition
                .RequestHashConflict);

    public static ZodiacSkillGridUpgradeExecutionResult InvalidIntent() =>
        new(ZodiacSkillGridUpgradeExecutionDisposition.InvalidIntent);

    public static ZodiacSkillGridUpgradeExecutionResult
        PreconditionFailed() =>
        new(
            ZodiacSkillGridUpgradeExecutionDisposition
                .PreconditionFailed);

    private static ZodiacSkillGridUpgradeExecutionResult
        CreateFromReceipt(
            ZodiacSkillGridUpgradeExecutionDisposition disposition,
            ZodiacSkillGridUpgradeExecutionReceipt receipt) =>
        new(
            disposition,
            receipt,
            hasAuthoritativeProjection: true,
            receipt.EnergyAfter,
            receipt.EnergyRemainderAfterX100,
            receipt.TalentPointsAfter,
            receipt.CurrentLevel,
            receipt.SelectedSkillId);

    private static bool ProjectionMatchesReceipt(
        ZodiacSkillGridUpgradeExecutionReceipt receipt,
        int currentEnergy,
        int currentEnergyRemainderX100,
        int currentTalentPoints,
        byte currentLevel,
        int selectedSkillId) =>
        currentEnergy == receipt.EnergyAfter &&
        currentEnergyRemainderX100 ==
            receipt.EnergyRemainderAfterX100 &&
        currentTalentPoints == receipt.TalentPointsAfter &&
        currentLevel == receipt.CurrentLevel &&
        selectedSkillId == receipt.SelectedSkillId;
}
