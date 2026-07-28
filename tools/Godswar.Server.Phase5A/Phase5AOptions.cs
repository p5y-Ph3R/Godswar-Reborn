using System.Globalization;

namespace Godswar.Server.Phase5A;

internal enum Phase5AMode
{
    Load,
    PacedSoak
}

internal sealed record Phase5AOptions(
    Phase5AMode Mode,
    int Bots,
    int DurationSeconds,
    int TickRate,
    uint Seed)
{
    public const int DefaultBots = 64;
    public const int MaximumBots = 512;
    public const int DefaultDurationSeconds = 10;
    public const int MaximumDurationSeconds = 300;
    public const int FixedTickRate = 20;
    public const int OperationsPerBotTick = 6;
    public const long MaximumTotalOperations = 5_000_000;
    public const long DefaultTotalOperations =
        (long)DefaultBots *
        DefaultDurationSeconds *
        FixedTickRate *
        OperationsPerBotTick;
    public const int PercentileSampleCapacity = 2_048;
    public const uint DefaultSeed = 20_260_728;

    public long PlannedTicks =>
        checked((long)DurationSeconds * TickRate);

    public long PlannedBotTicks =>
        checked(PlannedTicks * Bots);

    public long PlannedOperations =>
        checked(PlannedBotTicks * OperationsPerBotTick);

    public static Phase5AOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length > 10)
        {
            throw new ArgumentException(
                "At most five bounded options may be supplied.");
        }

        var mode = Phase5AMode.Load;
        var bots = DefaultBots;
        var durationSeconds = DefaultDurationSeconds;
        var tickRate = FixedTickRate;
        var seed = DefaultSeed;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            if (name.Length is 0 or > 32)
            {
                throw new ArgumentException(
                    "Option names must contain from 1 through 32 characters.");
            }
            if (!seen.Add(name))
            {
                throw new ArgumentException(
                    $"Option '{Safe(name)}' was supplied more than once.");
            }

            var value = ReadValue(args, ref index, name);
            switch (name)
            {
                case "--mode":
                    mode = ParseMode(value);
                    break;
                case "--bots":
                    bots = ParseInt(
                        value,
                        name,
                        minimum: 1,
                        maximum: MaximumBots);
                    break;
                case "--duration-seconds":
                    durationSeconds = ParseInt(
                        value,
                        name,
                        minimum: 1,
                        maximum: MaximumDurationSeconds);
                    break;
                case "--tick-rate":
                    tickRate = ParseInt(
                        value,
                        name,
                        minimum: FixedTickRate,
                        maximum: FixedTickRate);
                    break;
                case "--seed":
                    seed = ParseSeed(value);
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown option '{Safe(name)}'. This tool has no target option.");
            }
        }

        var options = new Phase5AOptions(
            mode,
            bots,
            durationSeconds,
            tickRate,
            seed);
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (Bots is < 1 or > MaximumBots)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Bots),
                $"Bots must be from 1 through {MaximumBots}.");
        }
        if (DurationSeconds is < 1 or > MaximumDurationSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(DurationSeconds),
                $"Duration must be from 1 through {MaximumDurationSeconds} seconds.");
        }
        if (TickRate != FixedTickRate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TickRate),
                $"The authoritative movement workload is fixed at {FixedTickRate} Hz.");
        }
        if (Seed == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Seed),
                "The deterministic seed must be non-zero.");
        }

        long operations;
        try
        {
            operations = PlannedOperations;
        }
        catch (OverflowException error)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PlannedOperations),
                "The requested workload exceeds the operation budget. " +
                error.Message);
        }

        if (operations > MaximumTotalOperations)
        {
            throw new ArgumentOutOfRangeException(
                nameof(PlannedOperations),
                $"The requested {operations:N0} operations exceed the hard " +
                $"{MaximumTotalOperations:N0} operation cap.");
        }
    }

    private static string ReadValue(
        string[] args,
        ref int index,
        string name)
    {
        if (!name.StartsWith("--", StringComparison.Ordinal) ||
            index + 1 >= args.Length)
        {
            throw new ArgumentException(
                $"Option '{Safe(name)}' requires one value.");
        }

        var value = args[++index];
        if (value.Length is 0 or > 32 ||
            value.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Option '{Safe(name)}' has an invalid value.");
        }

        return value;
    }

    private static Phase5AMode ParseMode(string value) =>
        value switch
        {
            "load" => Phase5AMode.Load,
            "paced-soak" => Phase5AMode.PacedSoak,
            _ => throw new ArgumentException(
                "--mode must be 'load' or 'paced-soak'.")
        };

    private static int ParseInt(
        string value,
        string name,
        int minimum,
        int maximum)
    {
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed < minimum ||
            parsed > maximum)
        {
            throw new ArgumentOutOfRangeException(
                name,
                $"{name} must be from {minimum} through {maximum}.");
        }

        return parsed;
    }

    private static uint ParseSeed(string value)
    {
        if (!uint.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var seed) ||
            seed == 0)
        {
            throw new ArgumentOutOfRangeException(
                "--seed",
                "--seed must be a non-zero unsigned 32-bit integer.");
        }

        return seed;
    }

    private static string Safe(string value) =>
        value.Length <= 64 ? value : value[..64];
}
