using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;


internal static partial class EquipmentForgeCatalogChecks
{
    private static void CheckRubyCalculation()
    {
        var request = new EquipmentForgeRequest(
            Item(1001, quality: 4, grade: 6),
            new EquipmentForgeMaterialSelection(Item(4200), 1),
            new EquipmentForgeMaterialSelection(Item(4230, stack: 5), 5));

        Check.True(
            EquipmentForgeCalculator.TryCalculate(request, out var calculation, out var error),
            $"Ruby calculation succeeds ({error})");
        Check.Equal((int)EquipmentForgeOperation.Ruby, (int)calculation!.Operation, "Ruby operation");
        Check.Equal(100, calculation.SuccessProbability, "Ruby probability clamps to 100");
        Check.Equal(177, calculation.SilverCost, "Ruby uses Amoney");
        Check.Equal(1002u, calculation.SuccessEquipment.Id, "Ruby success uses NextID");
        Check.Equal(
            request.Equipment with { Id = 1002 },
            calculation.SuccessEquipment,
            "Ruby success preserves every non-template equipment field");
        Check.Equal(1001u, calculation.FailureEquipment.Id, "ordinary Ruby failure preserves equipment ID");
        Check.Equal((short)4, calculation.SuccessEquipment.Quality, "Ruby preserves quality");
        Check.Equal((short)6, calculation.SuccessEquipment.Grade, "Ruby preserves grade");

        var cappedAxesRequest = request with
        {
            Equipment = Item(
                1001,
                quality: EquipmentForgeCalculator.MaximumQuality,
                grade: EquipmentForgeCalculator.MaximumGrade),
            OddsMaterial = null
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(cappedAxesRequest, out calculation, out error),
            $"Ruby remains valid on Q20/G25 equipment ({error})");
        Check.Equal(EquipmentForgeCalculator.MaximumQuality, calculation!.SuccessEquipment.Quality, "Ruby preserves Boundless Q20");
        Check.Equal(EquipmentForgeCalculator.MaximumGrade, calculation.SuccessEquipment.Grade, "Ruby preserves G25");
    }

    private static void CheckSapphireCalculation()
    {
        var request = new EquipmentForgeRequest(
            Item(1000, quality: 1),
            new EquipmentForgeMaterialSelection(Item(4210), 1),
            new EquipmentForgeMaterialSelection(Item(4230, stack: 25), 25));

        Check.True(
            EquipmentForgeCalculator.TryCalculate(request, out var calculation, out var error),
            $"Sapphire calculation succeeds ({error})");
        Check.Equal((int)EquipmentForgeOperation.Sapphire, (int)calculation!.Operation, "Sapphire operation");
        Check.Equal(100, calculation.SuccessProbability, "Sapphire tutorial recipe reaches 100 percent");
        Check.Equal(1, calculation.SilverCost, "Sapphire uses Bmoney at quality minus one");
        Check.Equal((short)2, calculation.SuccessEquipment.Quality, "Sapphire success increments quality");
        Check.Equal((short)1, calculation.FailureEquipment.Quality, "Sapphire failure preserves quality");

        var superiorToClassic = new EquipmentForgeRequest(
            Item(1000, quality: 5),
            new EquipmentForgeMaterialSelection(Item(4212), 1),
            new EquipmentForgeMaterialSelection(Item(4232, stack: 17), 17));
        Check.True(
            EquipmentForgeCalculator.TryCalculate(superiorToClassic, out calculation, out error),
            $"Superior-to-Classic recipe succeeds ({error})");
        Check.Equal(100, calculation!.SuccessProbability, "seventeen Level 3 Crystals make Q5 recipe authoritative 100 percent");
        Check.Equal(3, calculation.SilverCost, "Q5-to-Q6 recipe costs native three silver");
        Check.Equal((short)6, calculation.SuccessEquipment.Quality, "Superior upgrades to Classic quality");

        var lowSapphireBoundary = request with
        {
            Equipment = Item(1000, quality: 7),
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4212), 1),
            OddsMaterial = null
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(lowSapphireBoundary, out _, out error),
            $"low Sapphire remains valid at current Q7 ({error})");

        lowSapphireBoundary = lowSapphireBoundary with { Equipment = Item(1000, quality: 8) };
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(lowSapphireBoundary, out _, out error) &&
            error == EquipmentForgeValidationError.MaterialRoundNotAllowed,
            "low Sapphire stops at current Q8");
        Check.True(
            EquipmentForgeCalculator.TryCalculate(
                lowSapphireBoundary with
                {
                    PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4213), 1)
                },
                out _,
                out error),
            $"high Sapphire starts at current Q8 ({error})");

        var lowChance = request with
        {
            Equipment = Item(1000, quality: 9),
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4213), 1),
            OddsMaterial = null
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(lowChance, out calculation, out error),
            $"high-round Sapphire calculation succeeds ({error})");
        Check.Equal(0, calculation!.SuccessProbability, "negative Sapphire probability clamps to zero");
        Check.Equal(18, calculation.SilverCost, "Sapphire Q9 cost uses index eight");

        var mysticToDivine = request with
        {
            Equipment = Item(1000, quality: 10),
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4213), 1),
            OddsMaterial = new EquipmentForgeMaterialSelection(Item(4233, stack: 25), 25)
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(mysticToDivine, out calculation, out error),
            $"Mystic-to-Divine recipe succeeds ({error})");
        Check.Equal(99, calculation!.SuccessProbability, "Q10 recipe follows extended native probability pattern");
        Check.Equal(20, calculation.SilverCost, "Q10 recipe replaces the terminal zero-cost sentinel");
        Check.Equal((short)11, calculation.SuccessEquipment.Quality, "Mystic upgrades to Divine quality");

        var divineToCelestial = mysticToDivine with { Equipment = Item(1000, quality: 11) };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(divineToCelestial, out calculation, out error),
            $"Divine-to-Celestial recipe succeeds ({error})");
        Check.Equal(89, calculation!.SuccessProbability, "Q11 recipe follows extended native probability pattern");
        Check.Equal(25, calculation.SilverCost, "Q11 recipe uses the extended economy cost");
        Check.Equal((short)12, calculation.SuccessEquipment.Quality, "Divine upgrades to Celestial quality");

        var celestialToMythical = mysticToDivine with { Equipment = Item(1000, quality: 12) };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(celestialToMythical, out calculation, out error),
            $"Celestial-to-Mythical recipe succeeds ({error})");
        Check.Equal(79, calculation!.SuccessProbability, "Q12 recipe follows extended native probability pattern");
        Check.Equal(30, calculation.SilverCost, "Q12 recipe uses the extended economy cost");
        Check.Equal((short)13, calculation.SuccessEquipment.Quality, "Celestial upgrades to Mythical quality");

        var levelFiveCelestialToMythical = celestialToMythical with
        {
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4215), 1),
            OddsMaterial = new EquipmentForgeMaterialSelection(Item(4234, stack: 25), 25)
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(levelFiveCelestialToMythical, out calculation, out error),
            $"Level-5 Sapphire remains valid in the Q12 overlap band ({error})");
        Check.Equal(100, calculation!.SuccessProbability, "25 Level-5 Crystals guarantee the Q12 overlap-band attempt");

        var levelFourAtMythical = celestialToMythical with
        {
            Equipment = Item(1000, quality: 13),
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4213), 1),
            OddsMaterial = null
        };
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(levelFourAtMythical, out _, out error) &&
            error == EquipmentForgeValidationError.MaterialRoundNotAllowed,
            "Level 4 Sapphire stops when the current equipment reaches Q13");

        var primordialToBoundless = celestialToMythical with
        {
            Equipment = Item(1000, quality: 19, grade: EquipmentForgeCalculator.MaximumGrade),
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4215), 1),
            OddsMaterial = new EquipmentForgeMaterialSelection(Item(4234, stack: 25), 25)
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(primordialToBoundless, out calculation, out error),
            $"Level 5 Sapphire reaches the Q19-to-Q20 boundary ({error})");
        Check.Equal(100, calculation!.SuccessProbability, "25 Level-5 Crystals guarantee the Q19 maximum-quality attempt");
        Check.Equal(65, calculation.SilverCost, "Q19 attempt uses the authoritative economy endpoint");
        Check.Equal(EquipmentForgeCalculator.MaximumQuality, calculation.SuccessEquipment.Quality, "Q19 equipment upgrades to Boundless Q20");
        Check.Equal(EquipmentForgeCalculator.MaximumGrade, calculation.SuccessEquipment.Grade, "Sapphire preserves the cross-axis G25 ceiling");
    }

    private static void CheckEmeraldCalculation()
    {
        var request = new EquipmentForgeRequest(
            Item(1000, grade: 1) with { Attribute1 = 24 },
            new EquipmentForgeMaterialSelection(Item(4220), 1),
            new EquipmentForgeMaterialSelection(Item(4230, stack: 20), 20));

        Check.True(
            EquipmentForgeCalculator.TryCalculate(request, out var calculation, out var error),
            $"Emerald calculation succeeds ({error})");
        Check.Equal((int)EquipmentForgeOperation.Emerald, (int)calculation!.Operation, "Emerald operation");
        Check.Equal(100, calculation.SuccessProbability, "Emerald tutorial recipe reaches 100 percent");
        Check.Equal(0, calculation.SilverCost, "Emerald uses Cmoney at grade minus one");
        Check.Equal((short)2, calculation.SuccessEquipment.Grade, "Emerald success increments grade");
        Check.Equal((short)1, calculation.FailureEquipment.Grade, "Emerald failure preserves grade");

        var lowEmeraldBoundary = request with
        {
            Equipment = Item(1000, grade: 9) with { Attribute1 = 24 },
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4222), 1),
            OddsMaterial = null
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(lowEmeraldBoundary, out _, out error),
            $"low Emerald remains valid at current grade 9 ({error})");
        lowEmeraldBoundary = lowEmeraldBoundary with
        {
            Equipment = Item(1000, grade: 10) with { Attribute1 = 24 }
        };
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(lowEmeraldBoundary, out _, out error) &&
            error == EquipmentForgeValidationError.MaterialRoundNotAllowed,
            "low Emerald stops at current grade 10");
        Check.True(
            EquipmentForgeCalculator.TryCalculate(
                lowEmeraldBoundary with
                {
                    PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4223), 1)
                },
                out _,
                out error),
            $"high Emerald starts at current grade 10 ({error})");

        var celestialToGradeThirteen = request with
        {
            Equipment = Item(1000, grade: 12) with { Attribute1 = 24 },
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4223), 1),
            OddsMaterial = new EquipmentForgeMaterialSelection(Item(4234, stack: 25), 25)
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(celestialToGradeThirteen, out calculation, out error),
            $"Level 4 Emerald remains valid above the former G12 ceiling ({error})");
        Check.Equal(100, calculation!.SuccessProbability, "G12 attempt clamps the level-5 Crystal-assisted chance");
        Check.Equal(25, calculation.SilverCost, "G12 attempt begins the authored high-grade economy band");
        Check.Equal((short)13, calculation.SuccessEquipment.Grade, "G12 equipment upgrades to G13");

        var levelFourAtGradeEighteen = celestialToGradeThirteen with
        {
            Equipment = Item(1000, grade: 18) with { Attribute1 = 24 },
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4223), 1),
            OddsMaterial = null
        };
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(levelFourAtGradeEighteen, out _, out error) &&
            error == EquipmentForgeValidationError.MaterialRoundNotAllowed,
            "Level 4 Emerald stops when the current equipment reaches G18");

        var gradeTwentyFourToTwentyFive = celestialToGradeThirteen with
        {
            Equipment = Item(1000, quality: EquipmentForgeCalculator.MaximumQuality, grade: 24) with { Attribute1 = 24 },
            PrimaryMaterial = new EquipmentForgeMaterialSelection(Item(4225), 1),
            OddsMaterial = new EquipmentForgeMaterialSelection(Item(4234, stack: 25), 25)
        };
        var gradeTwentyFourWithTwentyFourCrystals = gradeTwentyFourToTwentyFive with
        {
            OddsMaterial = new EquipmentForgeMaterialSelection(Item(4234, stack: 24), 24)
        };
        Check.True(
            EquipmentForgeCalculator.TryCalculate(gradeTwentyFourWithTwentyFourCrystals, out calculation, out error),
            $"Level-5 G24 boundary remains calculable with 24 Crystals ({error})");
        Check.Equal(87, calculation!.SuccessProbability, "24 Level-5 Crystals remain below certainty at the maximum grade");

        Check.True(
            EquipmentForgeCalculator.TryCalculate(gradeTwentyFourToTwentyFive, out calculation, out error),
            $"Level 5 Emerald reaches the G24-to-G25 boundary ({error})");
        Check.Equal(100, calculation!.SuccessProbability, "25 Level-5 Crystals guarantee the maximum-grade attempt");
        Check.Equal(85, calculation.SilverCost, "G24 attempt uses the authored high-grade economy endpoint");
        Check.Equal(EquipmentForgeCalculator.MaximumGrade, calculation.SuccessEquipment.Grade, "G24 equipment upgrades to the G25 ceiling");
        Check.Equal(EquipmentForgeCalculator.MaximumQuality, calculation.SuccessEquipment.Quality, "Emerald preserves the cross-axis Boundless Q20 ceiling");
    }

    private static void CheckValidation()
    {
        var stackedEquipment = new EquipmentForgeRequest(
            Item(1000, stack: 2),
            new EquipmentForgeMaterialSelection(Item(4200), 1),
            null);
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(stackedEquipment, out _, out var error) &&
            error == EquipmentForgeValidationError.EquipmentStackMustBeOne,
            "stacked equipment cannot multiply one forge payment across multiple items");

        var invalidQuantity = new EquipmentForgeRequest(
            Item(1000),
            new EquipmentForgeMaterialSelection(Item(4200, stack: 2), 2),
            null);
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(invalidQuantity, out _, out error) &&
            error == EquipmentForgeValidationError.PrimaryQuantityMustBeOne,
            "primary material quantity must be one");

        var wrongRound = new EquipmentForgeRequest(
            Item(1000, quality: 9),
            new EquipmentForgeMaterialSelection(Item(4210), 1),
            null);
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(wrongRound, out _, out error) &&
            error == EquipmentForgeValidationError.MaterialRoundNotAllowed,
            "material Round restricts quality progression");

        var tooManyOdds = new EquipmentForgeRequest(
            Item(1000),
            new EquipmentForgeMaterialSelection(Item(4200), 1),
            new EquipmentForgeMaterialSelection(Item(4230, stack: 26), 26));
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(tooManyOdds, out _, out error) &&
            error == EquipmentForgeValidationError.OddsQuantityInvalid,
            "odds-crystal quantity is capped at 25");

        var noAppendAttribute = new EquipmentForgeRequest(
            Item(1000),
            new EquipmentForgeMaterialSelection(Item(4220), 1),
            null);
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(noAppendAttribute, out _, out error) &&
            error == EquipmentForgeValidationError.EmeraldRequiresAppendAttribute,
            "Emerald forging requires an append attribute");

        var zeroIdAppendAttribute = new EquipmentForgeRequest(
            Item(1000) with { Attribute1 = 0 },
            new EquipmentForgeMaterialSelection(Item(4220), 1),
            null);
        Check.True(
            EquipmentForgeCalculator.TryCalculate(zeroIdAppendAttribute, out _, out error),
            $"append attribute ID zero remains Emerald-forgeable ({error})");

        var qualityCap = new EquipmentForgeRequest(
            Item(1000, quality: EquipmentForgeCalculator.MaximumQuality),
            new EquipmentForgeMaterialSelection(Item(4215), 1),
            null);
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(qualityCap, out _, out error) &&
            error == EquipmentForgeValidationError.ProgressionOutOfRange,
            "Sapphire forging stops at the Boundless Q20 quality cap");

        var gradeCap = new EquipmentForgeRequest(
            Item(1000, grade: EquipmentForgeCalculator.MaximumGrade) with { Attribute1 = 24 },
            new EquipmentForgeMaterialSelection(Item(4225), 1),
            null);
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(gradeCap, out _, out error) &&
            error == EquipmentForgeValidationError.ProgressionOutOfRange,
            "Emerald forging stops at the G25 grade cap");

        var piece = new EquipmentForgeRequest(
            Item(1000),
            new EquipmentForgeMaterialSelection(Item(4214), 1),
            null);
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(piece, out _, out error) &&
            error == EquipmentForgeValidationError.PrimaryMaterialRuleNotFound,
            "material pieces cannot be used directly");

        var terminalRuby = new EquipmentForgeRequest(
            Item(1013),
            new EquipmentForgeMaterialSelection(Item(4202), 1),
            null);
        Check.True(
            !EquipmentForgeCalculator.TryCalculate(terminalRuby, out _, out error) &&
            error == EquipmentForgeValidationError.MissingProbability,
            "terminal Ruby rule is rejected without inventing a probability");
    }

    private static CompactItemEntry Item(
        uint itemId,
        short quality = 1,
        short grade = 1,
        short stack = 1)
    {
        return CompactItemEntry.Empty with
        {
            Id = itemId,
            Quality = quality,
            Grade = grade,
            Stack = stack
        };
    }
}
