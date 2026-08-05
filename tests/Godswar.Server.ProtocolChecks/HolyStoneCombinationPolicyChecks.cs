using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class HolyStoneCombinationPolicyChecks
{
    public const string CheckName =
        "Authoritative Holy Stone combination policy";

    public static Task RunAsync()
    {
        AssertSupportedGradeTransitionsAndMixedTemperatures();
        AssertUnsupportedGrades();
        AssertEveryMaterialMustMatchTheTargetGrade();
        AssertStackPreconditions();
        AssertTargetPreservationAndMaterialConsumption();
        AssertBoundPropagation();
        AssertOnlyHolyStonesAreAccepted();
        return Task.CompletedTask;
    }

    private static void AssertSupportedGradeTransitionsAndMixedTemperatures()
    {
        for (short sourceGrade = 4; sourceGrade <= 9; sourceGrade++)
        {
            var targetId = sourceGrade % 2 == 0 ? 9030u : 9031u;
            var target = Item(targetId, sourceGrade);
            var failure = HolyStoneCombinationPolicy.TryPrepare(
                target,
                Item(9031, sourceGrade),
                Item(9030, sourceGrade),
                Item(9031, sourceGrade),
                out var plan);

            Check.Equal(
                (int)HolyStoneCombinationEligibilityFailure.None,
                (int)failure,
                $"grade {sourceGrade} combination accepts mixed temperatures");
            Check.Equal(
                checked((short)(sourceGrade + 1)),
                plan.TargetAfter.Grade,
                $"grade {sourceGrade} combines into grade {sourceGrade + 1}");
            Check.Equal(
                targetId,
                plan.TargetAfter.Id,
                $"grade {sourceGrade} retains the primary stone type");
        }
    }

    private static void AssertUnsupportedGrades()
    {
        foreach (var sourceGrade in new short[] { 3, 10 })
        {
            var failure = HolyStoneCombinationPolicy.TryPrepare(
                Item(9030, sourceGrade),
                Item(9030, sourceGrade),
                Item(9031, sourceGrade),
                Item(9030, sourceGrade),
                out var plan);

            Check.Equal(
                (int)HolyStoneCombinationEligibilityFailure.InvalidLevel,
                (int)failure,
                $"grade {sourceGrade} is outside the 4-to-9 source range");
            Check.Equal(
                default(HolyStoneCombinationPlan),
                plan,
                $"rejected grade {sourceGrade} produces no mutation plan");
        }
    }

    private static void AssertEveryMaterialMustMatchTheTargetGrade()
    {
        for (var mismatchedIndex = 0; mismatchedIndex < 3;
             mismatchedIndex++)
        {
            var materials = new[]
            {
                Item(9030, 6),
                Item(9031, 6),
                Item(9030, 6)
            };
            materials[mismatchedIndex] =
                materials[mismatchedIndex] with { Grade = 7 };

            var failure = HolyStoneCombinationPolicy.TryPrepare(
                Item(9031, 6),
                materials[0],
                materials[1],
                materials[2],
                out var plan);

            Check.Equal(
                (int)HolyStoneCombinationEligibilityFailure.LevelMismatch,
                (int)failure,
                $"material {mismatchedIndex + 1} must match the target grade");
            Check.Equal(
                default(HolyStoneCombinationPlan),
                plan,
                $"material {mismatchedIndex + 1} mismatch has no mutation plan");
        }
    }

    private static void AssertStackPreconditions()
    {
        foreach (var targetStack in new short[] { 0, 2 })
        {
            var failure = HolyStoneCombinationPolicy.TryPrepare(
                Item(9030, 5, targetStack),
                Item(9030, 5),
                Item(9031, 5),
                Item(9030, 5),
                out _);
            Check.Equal(
                (int)HolyStoneCombinationEligibilityFailure.InvalidTargetStack,
                (int)failure,
                $"primary stack {targetStack} is rejected instead of merged");
        }

        for (var emptyMaterialIndex = 0; emptyMaterialIndex < 3;
             emptyMaterialIndex++)
        {
            var materials = new[]
            {
                Item(9030, 5),
                Item(9031, 5),
                Item(9030, 5)
            };
            materials[emptyMaterialIndex] =
                materials[emptyMaterialIndex] with { Stack = 0 };

            var failure = HolyStoneCombinationPolicy.TryPrepare(
                Item(9031, 5),
                materials[0],
                materials[1],
                materials[2],
                out _);
            Check.Equal(
                (int)HolyStoneCombinationEligibilityFailure.InvalidTargetStack,
                (int)failure,
                $"material {emptyMaterialIndex + 1} requires at least one item");
        }

        Check.Equal(
            (int)HolyStoneCombinationEligibilityFailure.None,
            (int)HolyStoneCombinationPolicy.TryPrepare(
                Item(9030, 5),
                Item(9030, 5, 1),
                Item(9031, 5, 2),
                Item(9030, 5, short.MaxValue),
                out _),
            "positive material stacks are accepted");
    }

    private static void AssertTargetPreservationAndMaterialConsumption()
    {
        var target = Item(9031, 8) with
        {
            Attribute1 = 101,
            Attribute2 = 102,
            Attribute3 = 103,
            Attribute4 = 104,
            Attribute5 = 105,
            Quality = 9,
            Bound = 1,
            Exp = 123456,
            HolySuitCode = 304,
            AttributeLevel1 = 1,
            AttributeLevel2 = 2,
            AttributeLevel3 = 3,
            AttributeLevel4 = 4,
            AttributeLevel5 = 5,
            SocketCount = 4,
            Socket1EffectId = 11,
            Socket1Level = 1,
            Socket2EffectId = 12,
            Socket2Level = 2,
            Socket3EffectId = 13,
            Socket3Level = 3,
            Socket4EffectId = 14,
            Socket4Level = 4,
            ClassAttribute1 = 200,
            ElementalAttribute1 = 300
        };
        var firstMaterial = Item(9030, 8, 1) with
        {
            Bound = 1,
            Exp = 111
        };
        var secondMaterial = Item(9031, 8, 2) with
        {
            Attribute1 = 202,
            Bound = 1,
            Exp = 222
        };
        var thirdMaterial = Item(9030, 8, 5) with
        {
            Attribute2 = 303,
            Quality = 7,
            Exp = 333
        };

        var failure = HolyStoneCombinationPolicy.TryPrepare(
            target,
            firstMaterial,
            secondMaterial,
            thirdMaterial,
            out var plan);

        Check.Equal(
            (int)HolyStoneCombinationEligibilityFailure.None,
            (int)failure,
            "valid rich stone combination prepares a mutation plan");
        Check.Equal(
            target with { Grade = 9 },
            plan.TargetAfter,
            "only the primary grade changes; ID, type, bound, stack and all other fields remain intact");
        Check.Equal(
            CompactItemEntry.Empty,
            plan.FirstMaterialAfter,
            "a one-item fodder stack is deleted");
        Check.Equal(
            secondMaterial with { Stack = 1 },
            plan.SecondMaterialAfter,
            "a two-item fodder stack decrements by exactly one");
        Check.Equal(
            thirdMaterial with { Stack = 4 },
            plan.ThirdMaterialAfter,
            "a larger fodder stack decrements by exactly one");
    }

    private static void AssertOnlyHolyStonesAreAccepted()
    {
        Check.Equal(
            (int)HolyStoneCombinationEligibilityFailure.TargetNotHolyStone,
            (int)HolyStoneCombinationPolicy.TryPrepare(
                Item(9040, 5),
                Item(9030, 5),
                Item(9031, 5),
                Item(9030, 5),
                out _),
            "a non-Holy-Stone primary item is rejected");

        for (var invalidMaterialIndex = 0; invalidMaterialIndex < 3;
             invalidMaterialIndex++)
        {
            var materials = new[]
            {
                Item(9030, 5),
                Item(9031, 5),
                Item(9030, 5)
            };
            materials[invalidMaterialIndex] = Item(9040, 5);

            Check.Equal(
                (int)HolyStoneCombinationEligibilityFailure.MaterialNotHolyStone,
                (int)HolyStoneCombinationPolicy.TryPrepare(
                    Item(9031, 5),
                    materials[0],
                    materials[1],
                    materials[2],
                    out _),
                $"non-Holy-Stone material {invalidMaterialIndex + 1} is rejected");
        }
    }

    private static void AssertBoundPropagation()
    {
        var unbound = Item(9030, 6) with { Bound = 0 };
        Check.Equal(
            (int)HolyStoneCombinationEligibilityFailure.None,
            (int)HolyStoneCombinationPolicy.TryPrepare(
                unbound,
                Item(9031, 6) with { Bound = 0 },
                Item(9030, 6) with { Bound = 1 },
                Item(9031, 6) with { Bound = 0 },
                out var boundPlan),
            "bound fodder can participate in a Combination");
        Check.Equal(
            1,
            boundPlan.TargetAfter.Bound,
            "any bound input makes the retained primary bound");

        HolyStoneCombinationPolicy.TryPrepare(
            unbound,
            Item(9031, 6) with { Bound = 0 },
            Item(9030, 6) with { Bound = 0 },
            Item(9031, 6) with { Bound = 0 },
            out var unboundPlan);
        Check.Equal(
            0,
            unboundPlan.TargetAfter.Bound,
            "four unbound inputs keep the retained primary unbound");
    }

    private static CompactItemEntry Item(
        uint id,
        short grade,
        short stack = 1) =>
        CompactItemEntry.Empty with
        {
            Id = id,
            Quality = 1,
            Grade = grade,
            Stack = stack
        };
}
