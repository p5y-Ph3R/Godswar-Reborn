using Godswar.Server.Application.Reconciliation;

namespace Godswar.Server.ProtocolChecks;

internal static class B19ReconciliationContractChecks
{
    internal const string CheckName =
        "B19 reconciliation options and finite classification";

    public static Task RunAsync()
    {
        CheckSafeDefaults();
        CheckBounds();
        CheckFiniteCategories();
        return Task.CompletedTask;
    }

    private static void CheckSafeDefaults()
    {
        var options = new ReconciliationOptions();
        options.Validate();

        Check.True(
            !options.Enabled,
            "reconciliation is opt-in");
        Check.Equal(
            (int)ReconciliationMode.ReportOnly,
            (int)options.Mode,
            "reconciliation defaults to report-only");
        Check.True(
            options.BatchSize is > 0 and <= 500,
            "default batch size is finite");
        Check.True(
            options.MaximumCharactersPerRun >= options.BatchSize &&
            options.MaximumOutboxEventsPerRun >= options.BatchSize,
            "default run budgets cover at least one bounded page");
        Check.True(
            options.CommandTimeout > TimeSpan.Zero &&
            options.RunTimeout >= options.CommandTimeout &&
            options.PollInterval >= TimeSpan.FromSeconds(10),
            "default database, run, and polling deadlines are finite");
    }

    private static void CheckBounds()
    {
        ExpectInvalid(
            new ReconciliationOptions { BatchSize = 0 },
            "zero-sized batches are rejected");
        ExpectInvalid(
            new ReconciliationOptions { BatchSize = 501 },
            "oversized batches are rejected");
        ExpectInvalid(
            new ReconciliationOptions
            {
                MaximumCharactersPerRun = 0
            },
            "empty character run budgets are rejected");
        ExpectInvalid(
            new ReconciliationOptions
            {
                MaximumCharactersPerRun = 1_000_001
            },
            "unbounded character run budgets are rejected");
        ExpectInvalid(
            new ReconciliationOptions
            {
                MaximumOutboxEventsPerRun = 0
            },
            "empty outbox run budgets are rejected");
        ExpectInvalid(
            new ReconciliationOptions
            {
                MaximumOutboxEventsPerRun = 1_000_001
            },
            "unbounded outbox run budgets are rejected");
        ExpectInvalid(
            new ReconciliationOptions
            {
                PollIntervalMilliseconds = 9_999
            },
            "polling cannot become a tight database loop");
        ExpectInvalid(
            new ReconciliationOptions
            {
                CommandTimeoutMilliseconds = 99
            },
            "database commands retain a finite minimum deadline");
        ExpectInvalid(
            new ReconciliationOptions
            {
                CommandTimeoutMilliseconds = 1_001,
                RunTimeoutMilliseconds = 1_000
            },
            "run timeout cannot undercut a command timeout");
        ExpectInvalid(
            new ReconciliationOptions
            {
                RunTimeoutMilliseconds = 600_001
            },
            "run timeout has a finite upper bound");
        ExpectInvalid(
            new ReconciliationOptions
            {
                Mode = (ReconciliationMode)byte.MaxValue
            },
            "unknown or future repair modes fail closed");
    }

    private static void CheckFiniteCategories()
    {
        var categories = Enum.GetValues<ReconciliationCategory>();
        var protocolValues = categories
            .Select(static category => category.ToProtocolValue())
            .ToArray();

        Check.Equal(
            32,
            categories.Length,
            "reviewed reconciliation category cardinality");
        Check.Equal(
            categories.Length,
            protocolValues.Distinct(StringComparer.Ordinal).Count(),
            "every category has one unique protocol value");
        Check.True(
            protocolValues.All(static value =>
                value.Length is > 0 and <= 64 &&
                value.All(static character =>
                    character is >= 'a' and <= 'z' or
                        >= '0' and <= '9' or '_')),
            "category values are bounded low-cardinality tokens");
        Check.True(
            categories.All(static category =>
                (byte)category > 0),
            "zero is not a valid reconciliation category");
        Check.Throws<ArgumentOutOfRangeException>(
            () => ((ReconciliationCategory)byte.MaxValue)
                .ToProtocolValue(),
            "unknown categories fail closed");
    }

    private static void ExpectInvalid(
        ReconciliationOptions options,
        string description)
    {
        Check.Throws<InvalidOperationException>(
            options.Validate,
            description);
    }
}
