using System.Diagnostics.Metrics;

namespace Godswar.Server.Security.Authentication;

internal enum AuthenticationMetricOutcome
{
    Accepted,
    Rejected,
    ResetRequired,
    InvalidInput,
    Busy,
    TimedOut,
    Cancelled
}

internal static class AuthenticationMetrics
{
    public const string MeterName =
        "Godswar.Server.Security.Authentication";

    private const string OutcomeTagName =
        "authentication.outcome";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Outcomes =
        Meter.CreateCounter<long>(
            "godswar.server.authentication.attempts",
            "{attempt}");
    private static readonly Histogram<double> Duration =
        Meter.CreateHistogram<double>(
            "godswar.server.authentication.duration",
            "ms");

    public static void Record(
        AuthenticationMetricOutcome outcome,
        TimeSpan duration)
    {
        var tag = new KeyValuePair<string, object?>(
            OutcomeTagName,
            ToMetricTag(outcome));
        Outcomes.Add(1, tag);
        Duration.Record(
            Math.Max(0, duration.TotalMilliseconds),
            tag);
    }

    internal static string ToMetricTag(
        AuthenticationMetricOutcome outcome) =>
        outcome switch
        {
            AuthenticationMetricOutcome.Accepted => "accepted",
            AuthenticationMetricOutcome.Rejected => "rejected",
            AuthenticationMetricOutcome.ResetRequired =>
                "reset_required",
            AuthenticationMetricOutcome.InvalidInput => "invalid_input",
            AuthenticationMetricOutcome.Busy => "busy",
            AuthenticationMetricOutcome.TimedOut => "timed_out",
            AuthenticationMetricOutcome.Cancelled => "cancelled",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
}
