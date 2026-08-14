namespace Godswar.Server.Application.Pets;

/// <summary>
/// Durable invariants shared by persistence commands and login snapshots.
/// Basic is independently persisted; Added is materialized from effective
/// Growth and the current pet level.
/// </summary>
internal static class PetSavvyPersistenceContract
{
    public const string SourceVersion = "basic-plus-scaled-growth-v3";

    public static decimal ResolveAdded(
        int petLevel,
        decimal baseGrowthRate,
        decimal growthAcceleration)
    {
        if (petLevel <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(petLevel));
        }
        if (baseGrowthRate < 0m || growthAcceleration < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseGrowthRate),
                "Pet Growth values cannot be negative.");
        }

        return checked(
            (baseGrowthRate + growthAcceleration) * petLevel);
    }
}
