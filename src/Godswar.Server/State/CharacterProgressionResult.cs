namespace Godswar.Server.State;

internal sealed record CharacterProgressionResult(
    int ExperienceGained,
    int PreviousLevel,
    int CurrentLevel,
    long CurrentExperience,
    int NextLevelExperience,
    IReadOnlyList<PlayerLevelUpProgression> LevelUps,
    int TalentExperienceGained,
    int CurrentTalentExperience,
    int TalentPointsGained,
    int CurrentTalentPoints);
