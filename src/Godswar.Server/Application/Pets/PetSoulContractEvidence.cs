namespace Godswar.Server.Application.Pets;

internal sealed record PetSoulContractEvidence(
    long PetId,
    byte PreviousStage,
    byte NewStage,
    int MaterialTemplateId,
    byte MaterialQuantity,
    int BasicSavvyIncreaseHundredths)
{
    public bool IsValid =>
        PetId > 0 &&
        PreviousStage <= PetSoulContractRules.MaximumStage &&
        NewStage is >= 1 and <= PetSoulContractRules.MaximumStage &&
        MaterialTemplateId == PetSoulContractRules.ContractSpiritItemId &&
        MaterialQuantity <= PetSoulContractRules.MaximumSpiritCount &&
        NewStage == MaterialQuantity + 1 &&
        BasicSavvyIncreaseHundredths ==
            PetSoulContractRules.BasicSavvyIncreaseHundredths(NewStage);
}
