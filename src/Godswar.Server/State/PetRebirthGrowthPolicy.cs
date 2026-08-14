namespace Godswar.Server.State;

internal sealed record PetRebirthTier(
    int FirstRebirth,
    int LastRebirth,
    uint ChanceItemId,
    string ChanceItemName);

/// <summary>
/// Cold baseline declarations for the rebirth-attempt ladder and its item
/// names. Runtime eligibility resolves the process-pinned content catalog.
/// </summary>
internal static class PetRebirthGrowthPolicy
{
    public const string Version = "stock-client-v1";
    public const int MaximumRebirthCount = 100;
    // Compatibility name used by the published settings schema. This is a
    // maximum, not a required quantity.
    public const int RequiredSpiritCount = 5;
    public const uint AmbrosiaOfRebirthItemId =
        PetItemCatalog.AmbrosiaOfRebirth;

    public static IReadOnlyList<PetRebirthTier> Tiers { get; } =
        Array.AsReadOnly(
        new PetRebirthTier[]
        {
            new(
                1,
                30,
                PetItemCatalog.SpringWater,
                "Spring Water"),
            new(
                31,
                60,
                PetItemCatalog.JuiceOfRebirth,
                "Juice of Rebirth"),
            new(
                61,
                100,
                AmbrosiaOfRebirthItemId,
                "Ambrosia of Rebirth")
        });

    public static bool TryGetTier(
        int rebirthNumber,
        out PetRebirthTier tier)
    {
        tier = Tiers.FirstOrDefault(
            candidate =>
                rebirthNumber >= candidate.FirstRebirth &&
                rebirthNumber <= candidate.LastRebirth)!;
        return tier is not null;
    }

}
