namespace Godswar.Server.Application.Pets;

/// <summary>
/// Stable client-facing Soul Contract stages and material identity.
/// </summary>
internal static class PetSoulContractRules
{
    public const int ContractSpiritItemId = 10105;
    public const int MaximumSpiritCount = 5;
    public const byte MaximumStage = 6;

    private static readonly int[] IncreaseHundredths =
        [300, 400, 500, 600, 700, 800];

    public static byte StageForSpiritCount(int spiritCount) =>
        spiritCount is >= 0 and <= MaximumSpiritCount
            ? checked((byte)(spiritCount + 1))
            : throw new ArgumentOutOfRangeException(nameof(spiritCount));

    public static int BasicSavvyIncreaseHundredths(byte stage) =>
        stage is >= 1 and <= MaximumStage
            ? IncreaseHundredths[stage - 1]
            : throw new ArgumentOutOfRangeException(nameof(stage));
}
