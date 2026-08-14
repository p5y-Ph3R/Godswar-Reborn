using Godswar.Server.State;

namespace Godswar.Server.ProtocolChecks;

internal static class PetPhoenixRebirthModifierPolicyChecks
{
    public static Task RunAsync()
    {
        var minimum = PetPhoenixRebirthModifierPolicy.Roll(
            5,
            new ConstantRandom());
        var maximum = PetPhoenixRebirthModifierPolicy.Roll(
            5,
            new MaximumRandom());
        var firstMinimum = PetPhoenixRebirthModifierPolicy.Roll(
            1,
            new ConstantRandom());
        var firstMaximum = PetPhoenixRebirthModifierPolicy.Roll(
            1,
            new MaximumRandom());
        var capMinimum = PetPhoenixRebirthModifierPolicy.Roll(
            100,
            new ConstantRandom());
        var capMaximum = PetPhoenixRebirthModifierPolicy.Roll(
            100,
            new MaximumRandom());
        Check.Equal(
            PetSavvy.Zero,
            PetPhoenixRebirthModifierPolicy.Roll(0, new MaximumRandom()),
            "zero completed Rebirths have an exact zero Phoenix modifier");
        Check.True(
            firstMinimum.Agility == .10m &&
            firstMaximum.Agility == .20m &&
            capMinimum.Agility == 10m &&
            capMaximum.Agility == 20m &&
            PetPhoenixRebirthModifierPolicy.IsValid(1, firstMinimum) &&
            PetPhoenixRebirthModifierPolicy.IsValid(1, firstMaximum) &&
            PetPhoenixRebirthModifierPolicy.IsValid(100, capMinimum) &&
            PetPhoenixRebirthModifierPolicy.IsValid(100, capMaximum),
            "Phoenix modifier endpoints cover Rebirth counts 1 and 100");
        Check.Equal(
            new PetSavvy(.50m, .50m, .50m, .50m, .50m, .50m),
            minimum,
            "five Rebirths extend every Phoenix minimum by 0.50");
        Check.Equal(
            new PetSavvy(1m, 1m, 1m, 1m, 1m, 1m),
            maximum,
            "five Rebirths extend every Phoenix maximum by 1.00");
        Check.True(
            14.00m + minimum.Agility == 14.50m &&
            16.00m + maximum.Agility == 17.00m,
            "nature support 14.00-16.00 becomes 14.50-17.00 at five Rebirths");
        Check.True(
            PetPhoenixRebirthModifierPolicy.IsValid(5, minimum) &&
            PetPhoenixRebirthModifierPolicy.IsValid(5, maximum) &&
            !PetPhoenixRebirthModifierPolicy.IsValid(
                5,
                minimum with { Agility = .49m }) &&
            !PetPhoenixRebirthModifierPolicy.IsValid(
                5,
                maximum with { Luck = 1.01m }) &&
            !PetPhoenixRebirthModifierPolicy.IsValid(
                5,
                minimum with { Wisdom = .505m }),
            "Phoenix modifier validation enforces inclusive hundredth bounds");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetPhoenixRebirthModifierPolicy.Roll(-1, Random.Shared),
            "negative completed Rebirth count is rejected");
        Check.Throws<ArgumentOutOfRangeException>(
            () => PetPhoenixRebirthModifierPolicy.Roll(101, Random.Shared),
            "completed Rebirth count above the cap is rejected");
        return Task.CompletedTask;
    }

    private sealed class ConstantRandom : Random
    {
        public override int Next(int minValue, int maxValue) => minValue;
    }

    private sealed class MaximumRandom : Random
    {
        public override int Next(int minValue, int maxValue) => maxValue - 1;
    }
}
