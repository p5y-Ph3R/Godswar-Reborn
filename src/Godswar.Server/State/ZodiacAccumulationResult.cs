namespace Godswar.Server.State;

internal sealed record ZodiacAccumulationResult(
    int ExperienceGainedX100,
    int TalentExperienceGainedX100,
    int CurrentExperienceX100,
    int CurrentTalentExperienceX100);
