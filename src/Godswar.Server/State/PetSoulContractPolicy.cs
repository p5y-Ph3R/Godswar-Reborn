using Godswar.Server.Application.Pets;

namespace Godswar.Server.State;

/// <summary>
/// Installed-client Pet_Alter.xml Indenture table. Contract stage is the
/// selected Contract Spirit count plus one; its fixed value applies to each
/// of the six displayed Basic Savvy attributes and replaces the prior stage.
/// </summary>
internal static class PetSoulContractPolicy
{
    public const int ContractSpiritItemId =
        PetSoulContractRules.ContractSpiritItemId;
    public const int MaximumSpiritCount =
        PetSoulContractRules.MaximumSpiritCount;
    public const byte MaximumStage = PetSoulContractRules.MaximumStage;

    public static byte StageForSpiritCount(int spiritCount) =>
        PetSoulContractRules.StageForSpiritCount(spiritCount);

    public static int BasicSavvyIncreaseHundredths(byte stage) =>
        PetSoulContractRules.BasicSavvyIncreaseHundredths(stage);

    public static decimal BasicSavvyIncrease(byte stage) =>
        BasicSavvyIncreaseHundredths(stage) / 100m;

    /// <summary>
    /// Resolves the client-visible total without changing persisted Basic or
    /// Added values. Re-signing supplies one stage, so the contract bonus is
    /// replaced rather than accumulated.
    /// </summary>
    public static PetSavvy ResolveDisplayedTotal(
        PetSavvy rawTotal,
        byte stage)
    {
        if (!rawTotal.IsNonNegative)
        {
            throw new ArgumentOutOfRangeException(nameof(rawTotal));
        }

        if (stage == 0)
        {
            return rawTotal;
        }

        var increase = BasicSavvyIncrease(stage);
        return rawTotal + new PetSavvy(
            increase,
            increase,
            increase,
            increase,
            increase,
            increase);
    }
}
