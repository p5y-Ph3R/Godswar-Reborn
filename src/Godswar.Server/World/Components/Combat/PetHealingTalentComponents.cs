namespace Godswar.Server.World.Components.Combat;

/// <summary>
/// Bounded, scalar projection of the one pet that may act for its owner.
/// Durable pet rows and collections never cross into the combat ECS world.
/// </summary>
internal readonly record struct PetHealingTalentHydrationSnapshot(
    long PetId,
    short Level,
    short Aptitude,
    short TalentMask,
    bool IsCarried,
    bool IsSummoned);

internal readonly record struct ActivePetHealingTalentComponent(
    long PetId,
    short Level,
    short Aptitude,
    short TalentMask,
    bool IsCarried,
    bool IsSummoned);
