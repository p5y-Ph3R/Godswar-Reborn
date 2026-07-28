namespace Godswar.Server.Phase5A;

internal static class Phase5ASelfCheck
{
    public static async Task<Phase5ASelfCheckReport> RunAsync(
        CancellationToken cancellationToken)
    {
        var checks = 0;
        var defaults = Phase5AOptions.Parse([]);
        Require(
            defaults.PlannedOperations ==
                Phase5AOptions.DefaultTotalOperations &&
            defaults.PlannedOperations <
                Phase5AOptions.MaximumTotalOperations,
            "default and hard operation budgets disagree");
        checks++;
        ExpectFailure(
            () => Phase5AOptions.Parse(
                ["--target", "127.0.0.1"]),
            "arbitrary targets must be unavailable");
        checks++;
        ExpectFailure(
            () => Phase5AOptions.Parse(
                ["--bots", "513"]),
            "bot hard cap");
        checks++;
        ExpectFailure(
            () => Phase5AOptions.Parse(
                ["--duration-seconds", "301"]),
            "duration hard cap");
        checks++;
        ExpectFailure(
            () => Phase5AOptions.Parse(
                ["--tick-rate", "21"]),
            "tick-rate hard cap");
        checks++;
        ExpectFailure(
            () => Phase5AOptions.Parse(
                [
                    "--bots", "512",
                    "--duration-seconds", "300"
                ]),
            "total-operation hard cap");
        checks++;

        var paced = Phase5AOptions.Parse(
            [
                "--mode", "paced-soak",
                "--bots", "1",
                "--duration-seconds", "1"
            ]);
        Require(
            paced.Mode == Phase5AMode.PacedSoak,
            "paced-soak mode did not parse");
        checks++;

        var budget = new OperationBudget(6);
        budget.Consume(6);
        ExpectFailure(
            () => budget.Consume(1),
            "runtime operation budget");
        checks++;

        var sampler = new BoundedPercentileSampler(7, 1);
        for (var index = 0; index < 100; index++)
        {
            sampler.Add(index);
        }
        Require(
            sampler.Count == 7 &&
            sampler.Seen == 100 &&
            sampler.Summarize().RetainedSamples == 7,
            "percentile reservoir was not bounded");
        checks++;

        var baseline = Phase5AOptions.Parse(
            [
                "--mode", "load",
                "--bots", "2",
                "--duration-seconds", "1",
                "--seed", "12345"
            ]);
        var differentSeed = baseline with { Seed = 54321 };
        var runner = new MovementLoadRunner();
        var first = await runner.RunAsync(
            baseline,
            cancellationToken);
        var repeat = await runner.RunAsync(
            baseline,
            cancellationToken);
        var different = await runner.RunAsync(
            differentSeed,
            cancellationToken);

        Require(
            first.Digest.Value == repeat.Digest.Value,
            "same configuration did not produce the same digest");
        checks++;
        Require(
            first.Digest.Value != different.Digest.Value,
            "different seed did not alter the digest");
        checks++;
        Require(
            first.Ticks.CompletedTicks == baseline.PlannedTicks &&
            first.Ticks.CompletedBotTicks == baseline.PlannedBotTicks &&
            first.Ticks.RejectedMovements == 0 &&
            first.Budget.Remaining == 0,
            "tiny workload did not complete its exact validated budget");
        checks++;
        Require(
            first.Packets.TotalPackets ==
                baseline.PlannedBotTicks * 2 &&
            first.Packets.TotalBytes ==
                first.Packets.InputBytes +
                first.Packets.SnapshotBytes,
            "packet accounting did not balance");
        checks++;

        return new Phase5ASelfCheckReport(
            "reborn.phase5a.self-check.v1",
            DateTimeOffset.UtcNow,
            "passed",
            checks,
            first.Digest.Value,
            different.Digest.Value,
            "in-process-only; no sockets and no configurable target");
    }

    private static void ExpectFailure(
        Action action,
        string description)
    {
        try
        {
            action();
        }
        catch (Exception error) when (
            error is ArgumentException or
                InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Self-check expected rejection: {description}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(
                $"Self-check failed: {message}.");
        }
    }
}
