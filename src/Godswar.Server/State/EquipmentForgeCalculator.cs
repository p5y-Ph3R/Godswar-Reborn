namespace Godswar.Server.State;

internal enum EquipmentForgeOperation
{
    Ruby = 1,
    Sapphire = 2,
    Emerald = 3
}

internal enum EquipmentForgeValidationError
{
    None = 0,
    RequestMissing,
    EquipmentMissing,
    EquipmentStackMustBeOne,
    EquipmentRuleNotFound,
    PrimaryMaterialMissing,
    PrimaryQuantityMustBeOne,
    PrimaryMaterialStackInsufficient,
    PrimaryMaterialRuleNotFound,
    UnsupportedPrimaryMaterialType,
    OddsMaterialMissing,
    OddsQuantityInvalid,
    OddsMaterialStackInsufficient,
    OddsMaterialRuleNotFound,
    OddsMaterialTypeMismatch,
    MaterialRoundNotAllowed,
    ProgressionOutOfRange,
    EmeraldRequiresAppendAttribute,
    MissingProbability,
    MissingSuccessTarget
}

internal readonly record struct EquipmentForgeMaterialSelection(
    CompactItemEntry Item,
    int Quantity);

internal sealed record EquipmentForgeRequest(
    CompactItemEntry Equipment,
    EquipmentForgeMaterialSelection PrimaryMaterial,
    EquipmentForgeMaterialSelection? OddsMaterial);

internal sealed record EquipmentForgeCalculation(
    EquipmentForgeOperation Operation,
    int SuccessProbability,
    int SilverCost,
    CompactItemEntry SuccessEquipment,
    CompactItemEntry FailureEquipment);

internal static class EquipmentForgeCalculator
{
    public const int MaximumOddsQuantity = 25;
    // Level-5 materials cover the locally extended ordinary-forge ceilings:
    // current Q19/G24 are the final eligible inputs and produce Q20
    // (Boundless)/G25. Progression vectors still provide the per-item guard.
    public const short MaximumQuality = 20;
    public const short MaximumGrade = 25;

    public static bool TryCalculate(
        EquipmentForgeRequest request,
        out EquipmentForgeCalculation? calculation,
        out EquipmentForgeValidationError error)
    {
        calculation = null;
        error = EquipmentForgeValidationError.None;

        if (request is null)
        {
            error = EquipmentForgeValidationError.RequestMissing;
            return false;
        }

        if (request.Equipment.IsEmpty)
        {
            error = EquipmentForgeValidationError.EquipmentMissing;
            return false;
        }

        // Equipment is never stackable. Enforce that invariant here as well
        // as in inventory creation so a malformed authoritative row cannot
        // upgrade multiple copies for one material/silver payment.
        if (request.Equipment.Stack != 1)
        {
            error = EquipmentForgeValidationError.EquipmentStackMustBeOne;
            return false;
        }

        if (!EquipmentForgeCatalog.TryGet(request.Equipment.Id, out var equipmentRule))
        {
            error = EquipmentForgeValidationError.EquipmentRuleNotFound;
            return false;
        }

        var primary = request.PrimaryMaterial;
        if (primary.Item.IsEmpty)
        {
            error = EquipmentForgeValidationError.PrimaryMaterialMissing;
            return false;
        }

        // The stock forge UI consumes one Ruby, Sapphire, or Emerald. Only the
        // optional odds-crystal slot has a selectable quantity.
        if (primary.Quantity != 1)
        {
            error = EquipmentForgeValidationError.PrimaryQuantityMustBeOne;
            return false;
        }

        if (primary.Item.Stack < primary.Quantity)
        {
            error = EquipmentForgeValidationError.PrimaryMaterialStackInsufficient;
            return false;
        }

        if (!ForgingMaterialRuleCatalog.TryGet(primary.Item.Id, out var primaryRule))
        {
            error = EquipmentForgeValidationError.PrimaryMaterialRuleNotFound;
            return false;
        }

        if (!TryGetOperation(primaryRule.MaterialType, out var operation))
        {
            error = EquipmentForgeValidationError.UnsupportedPrimaryMaterialType;
            return false;
        }

        if (!TryGetOddsBonus(request.OddsMaterial, out var oddsBonus, out error))
        {
            return false;
        }

        int baseProbability;
        int silverCost;
        CompactItemEntry successEquipment;
        CompactItemEntry failureEquipment;

        switch (operation)
        {
            case EquipmentForgeOperation.Ruby:
                if (!equipmentRule.Probability.HasValue)
                {
                    error = EquipmentForgeValidationError.MissingProbability;
                    return false;
                }

                if (!equipmentRule.NextItemId.HasValue)
                {
                    error = EquipmentForgeValidationError.MissingSuccessTarget;
                    return false;
                }

                baseProbability = equipmentRule.Probability.Value;
                silverCost = equipmentRule.Amoney;
                successEquipment = request.Equipment with { Id = equipmentRule.NextItemId.Value };
                // BadID is client rule data, but the native result handler does
                // not apply it for an ordinary failed roll.
                failureEquipment = request.Equipment;
                break;

            case EquipmentForgeOperation.Sapphire:
            {
                if (request.Equipment.Quality >= MaximumQuality)
                {
                    error = EquipmentForgeValidationError.ProgressionOutOfRange;
                    return false;
                }

                var progressionIndex = request.Equipment.Quality - 1;
                if (!TryGetProgressionValues(
                        progressionIndex,
                        request.Equipment.Quality,
                        equipmentRule.BaseProyAdd,
                        equipmentRule.Bmoney,
                        primaryRule,
                        out baseProbability,
                        out silverCost,
                        out error))
                {
                    return false;
                }

                successEquipment = request.Equipment with
                {
                    Quality = checked((short)(request.Equipment.Quality + 1))
                };
                failureEquipment = request.Equipment;
                break;
            }

            case EquipmentForgeOperation.Emerald:
            {
                if (!HasAppendAttribute(request.Equipment))
                {
                    error = EquipmentForgeValidationError.EmeraldRequiresAppendAttribute;
                    return false;
                }

                if (request.Equipment.Grade >= MaximumGrade)
                {
                    error = EquipmentForgeValidationError.ProgressionOutOfRange;
                    return false;
                }

                var progressionIndex = request.Equipment.Grade - 1;
                if (!TryGetProgressionValues(
                        progressionIndex,
                        request.Equipment.Grade,
                        equipmentRule.AppendProyAdd,
                        equipmentRule.Cmoney,
                        primaryRule,
                        out baseProbability,
                        out silverCost,
                        out error))
                {
                    return false;
                }

                successEquipment = request.Equipment with
                {
                    Grade = checked((short)(request.Equipment.Grade + 1))
                };
                failureEquipment = request.Equipment;
                break;
            }

            default:
                error = EquipmentForgeValidationError.UnsupportedPrimaryMaterialType;
                return false;
        }

        var probability = Math.Clamp(
            (long)baseProbability + primaryRule.ProbabilityBonus + oddsBonus,
            0L,
            100L);
        calculation = new EquipmentForgeCalculation(
            operation,
            checked((int)probability),
            silverCost,
            successEquipment,
            failureEquipment);
        return true;
    }

    private static bool TryGetOperation(int materialType, out EquipmentForgeOperation operation)
    {
        operation = materialType switch
        {
            (int)EquipmentForgeOperation.Ruby => EquipmentForgeOperation.Ruby,
            (int)EquipmentForgeOperation.Sapphire => EquipmentForgeOperation.Sapphire,
            (int)EquipmentForgeOperation.Emerald => EquipmentForgeOperation.Emerald,
            _ => default
        };
        return materialType is >= (int)EquipmentForgeOperation.Ruby and <= (int)EquipmentForgeOperation.Emerald;
    }

    private static bool TryGetOddsBonus(
        EquipmentForgeMaterialSelection? selection,
        out long bonus,
        out EquipmentForgeValidationError error)
    {
        bonus = 0;
        error = EquipmentForgeValidationError.None;
        if (!selection.HasValue)
        {
            return true;
        }

        var odds = selection.Value;
        if (odds.Item.IsEmpty)
        {
            error = EquipmentForgeValidationError.OddsMaterialMissing;
            return false;
        }

        if (odds.Quantity is <= 0 or > MaximumOddsQuantity)
        {
            error = EquipmentForgeValidationError.OddsQuantityInvalid;
            return false;
        }

        if (odds.Item.Stack < odds.Quantity)
        {
            error = EquipmentForgeValidationError.OddsMaterialStackInsufficient;
            return false;
        }

        if (!ForgingMaterialRuleCatalog.TryGet(odds.Item.Id, out var oddsRule))
        {
            error = EquipmentForgeValidationError.OddsMaterialRuleNotFound;
            return false;
        }

        if (oddsRule.MaterialType != 4)
        {
            error = EquipmentForgeValidationError.OddsMaterialTypeMismatch;
            return false;
        }

        bonus = (long)oddsRule.ProbabilityBonus * odds.Quantity;
        return true;
    }

    private static bool HasAppendAttribute(CompactItemEntry equipment)
    {
        // Attribute ID zero (AttackA) is valid in the native catalog. Null is
        // the absence sentinel; negative imported values remain invalid.
        return equipment.Attribute1 is >= 0 ||
               equipment.Attribute2 is >= 0 ||
               equipment.Attribute3 is >= 0 ||
               equipment.Attribute4 is >= 0 ||
               equipment.Attribute5 is >= 0 ||
               equipment.ClassAttribute1 is >= 0 ||
               equipment.ClassAttribute2 is >= 0 ||
               equipment.ElementalAttribute1 is >= 0 ||
               equipment.ElementalAttribute2 is >= 0;
    }

    private static bool TryGetProgressionValues(
        int progressionIndex,
        int materialRound,
        IReadOnlyList<int> probabilityAdds,
        IReadOnlyList<int> costs,
        ForgingMaterialRule materialRule,
        out int baseProbability,
        out int silverCost,
        out EquipmentForgeValidationError error)
    {
        baseProbability = 0;
        silverCost = 0;
        error = EquipmentForgeValidationError.None;

        if (progressionIndex < 0 ||
            progressionIndex >= probabilityAdds.Count ||
            progressionIndex >= costs.Count)
        {
            error = EquipmentForgeValidationError.ProgressionOutOfRange;
            return false;
        }

        // Native material eligibility compares the current quality/grade
        // directly. Only probability and cost vectors use current minus one.
        if (!materialRule.AllowsRound(materialRound))
        {
            error = EquipmentForgeValidationError.MaterialRoundNotAllowed;
            return false;
        }

        baseProbability = probabilityAdds[progressionIndex];
        silverCost = costs[progressionIndex];
        return true;
    }
}
