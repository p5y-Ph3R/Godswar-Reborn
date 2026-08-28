namespace Godswar.Server.State;

internal enum PetMonsterExperienceStatus : byte
{
    Applied = 1,
    Duplicate = 2,
    NoSummonedPet = 3,
    CharacterNotFound = 4,
    RequestConflict = 5
}

internal sealed record PetMonsterExperienceResult(
    PetMonsterExperienceStatus Status,
    Guid DeathEventId,
    int AwardedExperience,
    long? PetId,
    long? TotalExperience,
    long? PetRevision)
{
    public bool HasPetProjection =>
        PetId.HasValue && TotalExperience.HasValue && PetRevision.HasValue;
}

internal enum MonsterLootPickupStatus : byte
{
    Added = 1,
    Duplicate = 2,
    InsufficientCapacity = 3,
    CharacterNotFound = 4,
    RequestConflict = 5,
    Unsupported = 6
}

internal sealed record MonsterLootPickupResult(
    MonsterLootPickupStatus Status,
    GameCharacter? Character)
{
    public bool Succeeded => Status is
        MonsterLootPickupStatus.Added or
        MonsterLootPickupStatus.Duplicate;
}
