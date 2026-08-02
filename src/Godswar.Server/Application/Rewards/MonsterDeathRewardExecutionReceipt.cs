using System.Text;
using Godswar.Server.Application.Commands;

namespace Godswar.Server.Application.Rewards;

internal static class MonsterDeathRewardProgressionContract
{
    public const int MaximumCharacterLevel = 200;
}

internal readonly record struct MonsterDeathRewardLevelUp(
    int Level,
    long CurrentExperience,
    int NextLevelExperience);

internal sealed record MonsterDeathRewardExecutionReceipt
{
    public const int MaximumAuditReferenceUtf8Bytes = 256;

    public MonsterDeathRewardExecutionReceipt(
        Guid deathEventId,
        Guid runtimeInstanceId,
        byte mapId,
        uint monsterObjectId,
        uint spawnGeneration,
        ulong deathHealthRevision,
        int characterId,
        int requestedExperience,
        int requestedTalentExperience,
        int experienceGained,
        int previousLevel,
        int currentLevel,
        long previousExperience,
        long currentExperience,
        int nextLevelExperience,
        IReadOnlyList<MonsterDeathRewardLevelUp> levelUps,
        int talentExperienceGained,
        int previousTalentExperience,
        int currentTalentExperience,
        int talentPointsGained,
        int previousTalentPoints,
        int currentTalentPoints,
        long progressionRevision,
        string auditReference,
        Guid outboxEventId)
    {
        if (deathEventId == Guid.Empty ||
            runtimeInstanceId == Guid.Empty ||
            monsterObjectId == 0 ||
            spawnGeneration == 0 ||
            deathHealthRevision is 0 or >
                MonsterDeathRewardCommandEnvelope
                    .MaximumPersistedHealthRevision ||
            characterId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(deathEventId));
        }
        if (requestedExperience is < 0 or >
                MonsterDeathRewardCommandEnvelope
                    .MaximumAwardedExperience ||
            requestedTalentExperience is < 0 or >
                MonsterDeathRewardCommandEnvelope
                    .MaximumAwardedTalentExperience ||
            experienceGained < 0 ||
            talentExperienceGained < 0 ||
            previousLevel is < 1 or >
                MonsterDeathRewardProgressionContract
                    .MaximumCharacterLevel ||
            currentLevel < previousLevel ||
            currentLevel >
                MonsterDeathRewardProgressionContract
                    .MaximumCharacterLevel ||
            previousExperience is < 0 or > uint.MaxValue ||
            currentExperience is < 0 or > uint.MaxValue ||
            nextLevelExperience < 0 ||
            previousTalentExperience is < 0 or >= 100 ||
            currentTalentExperience is < 0 or >= 100 ||
            talentPointsGained < 0 ||
            previousTalentPoints < 0 ||
            currentTalentPoints < previousTalentPoints ||
            progressionRevision <= 0 ||
            outboxEventId == Guid.Empty)
        {
            throw new ArgumentOutOfRangeException(
                nameof(progressionRevision));
        }
        ArgumentNullException.ThrowIfNull(levelUps);
        if (levelUps.Count >
                MonsterDeathRewardProgressionContract
                    .MaximumCharacterLevel ||
            levelUps.Any(levelUp =>
                levelUp.Level <= previousLevel ||
                levelUp.Level > currentLevel ||
                levelUp.CurrentExperience is < 0 or > uint.MaxValue ||
                levelUp.NextLevelExperience < 0))
        {
            throw new ArgumentException(
                "The level-up evidence is invalid.",
                nameof(levelUps));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(auditReference);
        if (auditReference.Any(char.IsControl) ||
            Encoding.UTF8.GetByteCount(auditReference) >
                MaximumAuditReferenceUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(auditReference));
        }

        DeathEventId = deathEventId;
        RuntimeInstanceId = runtimeInstanceId;
        MapId = mapId;
        MonsterObjectId = monsterObjectId;
        SpawnGeneration = spawnGeneration;
        DeathHealthRevision = deathHealthRevision;
        CharacterId = characterId;
        RequestedExperience = requestedExperience;
        RequestedTalentExperience = requestedTalentExperience;
        ExperienceGained = experienceGained;
        PreviousLevel = previousLevel;
        CurrentLevel = currentLevel;
        PreviousExperience = previousExperience;
        CurrentExperience = currentExperience;
        NextLevelExperience = nextLevelExperience;
        LevelUps = levelUps.ToArray();
        TalentExperienceGained = talentExperienceGained;
        PreviousTalentExperience = previousTalentExperience;
        CurrentTalentExperience = currentTalentExperience;
        TalentPointsGained = talentPointsGained;
        PreviousTalentPoints = previousTalentPoints;
        CurrentTalentPoints = currentTalentPoints;
        ProgressionRevision = progressionRevision;
        AuditReference = auditReference;
        OutboxEventId = outboxEventId;
    }

    public CommandFamily Family =>
        CommandFamily.MonsterRewardSettlement;
    public Guid DeathEventId { get; }
    public Guid RuntimeInstanceId { get; }
    public byte MapId { get; }
    public uint MonsterObjectId { get; }
    public uint SpawnGeneration { get; }
    public ulong DeathHealthRevision { get; }
    public int CharacterId { get; }
    public int RequestedExperience { get; }
    public int RequestedTalentExperience { get; }
    public int ExperienceGained { get; }
    public int PreviousLevel { get; }
    public int CurrentLevel { get; }
    public long PreviousExperience { get; }
    public long CurrentExperience { get; }
    public int NextLevelExperience { get; }
    public IReadOnlyList<MonsterDeathRewardLevelUp> LevelUps { get; }
    public int TalentExperienceGained { get; }
    public int PreviousTalentExperience { get; }
    public int CurrentTalentExperience { get; }
    public int TalentPointsGained { get; }
    public int PreviousTalentPoints { get; }
    public int CurrentTalentPoints { get; }
    public long ProgressionRevision { get; }
    public string AuditReference { get; }
    public Guid OutboxEventId { get; }

    public MonsterDeathRewardProjection ToProjection() =>
        new(
            CurrentLevel,
            CurrentExperience,
            NextLevelExperience,
            CurrentTalentExperience,
            CurrentTalentPoints,
            ProgressionRevision);
}
