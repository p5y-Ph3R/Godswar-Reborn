using Godswar.Server.Domain.World.Instances;

namespace Godswar.Server.Application.WorldInstances;

/// <summary>
/// Authored gameplay difficulties. Enhanced is the public name for the stock
/// client's legacy Advanced selection.
/// </summary>
internal enum MedusaEncounterDifficulty : byte
{
    Normal = 1,
    Enhanced = 2,
    Mythic = 3
}

internal enum MedusaEncounterEnemyRole : byte
{
    Ordinary = 1,
    UtilityCarrier = 2,
    Elite = 3,
    Euryale = 4,
    Chrysaor = 5,
    Stheno = 6,
    Medusa = 7
}

internal enum MedusaDamageChannel : byte
{
    Physical = 1,
    Magical = 2
}

internal enum MedusaEncounterTitle : byte
{
    MedusaChallengers = 1,
    MedusaSlayers = 2,
    MedusaExecutioners = 3,
    GorgonBreaker = 4,
    BaneOfTheThreeSisters = 5,
    HeirOfPerseus = 6
}

/// <summary>
/// A zero rating means this role does not use that attack channel. Roles that
/// can be backed by either physical or magical monster templates publish both.
/// </summary>
internal readonly record struct MedusaAttackRatings(
    int Physical,
    int Magical);

internal readonly record struct MedusaEncounterEnemyDefinition(
    MedusaEncounterEnemyRole Role,
    MedusaMonsterRank Rank,
    int Count,
    int MaximumHealth,
    MedusaAttackRatings AttackRatings);

internal sealed record MedusaEncounterDifficultyDefinition(
    MedusaEncounterDifficulty Difficulty,
    string DifficultyLabel,
    string PublicName,
    MapId ContentMapId,
    string SceneKey,
    int HealthMultiplier,
    IReadOnlyList<MedusaEncounterEnemyDefinition> Enemies);

internal readonly record struct MedusaEncounterTitleAward(
    MedusaEncounterDifficulty Difficulty,
    TimeSpan MaximumCompletionTime,
    MedusaEncounterTitle Title,
    string DisplayName);
