namespace Godswar.Server.Application.Rewards;

internal enum MonsterDeathRewardExecutionDisposition : byte
{
    Committed = 1,
    Duplicate = 2,
    RequestHashConflict = 3,
    InvalidIntent = 4,
    PreconditionFailed = 5,
    RevisionConflict = 6
}

internal sealed record MonsterDeathRewardProjection(
    int Level,
    int Experience,
    int NextLevelExperience,
    int TalentExperience,
    int TalentPoints,
    long Revision);

internal sealed record MonsterDeathRewardExecutionResult
{
    private MonsterDeathRewardExecutionResult(
        MonsterDeathRewardExecutionDisposition disposition,
        MonsterDeathRewardExecutionReceipt? receipt = null,
        MonsterDeathRewardProjection? projection = null)
    {
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition));
        }

        var durable = disposition is
            MonsterDeathRewardExecutionDisposition.Committed or
            MonsterDeathRewardExecutionDisposition.Duplicate;
        if (durable != (receipt is not null && projection is not null) ||
            durable &&
            (!IsValidProjection(projection!) ||
             projection!.Revision < receipt!.ProgressionRevision))
        {
            throw new ArgumentException(
                "Reward execution evidence does not match its disposition.");
        }

        Disposition = disposition;
        Receipt = receipt;
        Projection = projection;
    }

    public MonsterDeathRewardExecutionDisposition Disposition { get; }
    public MonsterDeathRewardExecutionReceipt? Receipt { get; }
    public MonsterDeathRewardProjection? Projection { get; }
    public bool IsDurable => Receipt is not null;

    public static MonsterDeathRewardExecutionResult Committed(
        MonsterDeathRewardExecutionReceipt receipt) =>
        new(
            MonsterDeathRewardExecutionDisposition.Committed,
            receipt,
            receipt.ToProjection());

    public static MonsterDeathRewardExecutionResult Duplicate(
        MonsterDeathRewardExecutionReceipt receipt,
        MonsterDeathRewardProjection projection) =>
        new(
            MonsterDeathRewardExecutionDisposition.Duplicate,
            receipt,
            projection);

    public static MonsterDeathRewardExecutionResult
        RequestHashConflict() =>
        new(
            MonsterDeathRewardExecutionDisposition
                .RequestHashConflict);

    public static MonsterDeathRewardExecutionResult InvalidIntent() =>
        new(MonsterDeathRewardExecutionDisposition.InvalidIntent);

    public static MonsterDeathRewardExecutionResult
        PreconditionFailed() =>
        new(
            MonsterDeathRewardExecutionDisposition
                .PreconditionFailed);

    public static MonsterDeathRewardExecutionResult RevisionConflict() =>
        new(
            MonsterDeathRewardExecutionDisposition
                .RevisionConflict);

    private static bool IsValidProjection(
        MonsterDeathRewardProjection projection) =>
        projection.Level is
            >= 1 and <=
                MonsterDeathRewardProgressionContract
                    .MaximumCharacterLevel &&
        projection.Experience >= 0 &&
        projection.NextLevelExperience >= 0 &&
        projection.TalentExperience is >= 0 and < 100 &&
        projection.TalentPoints >= 0 &&
        projection.Revision > 0;
}
