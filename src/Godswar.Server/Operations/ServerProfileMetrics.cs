using System.Diagnostics.Metrics;

namespace Godswar.Server.Operations;

internal static class ServerProfileMetrics
{
    internal const string MeterName =
        "Godswar.Server.RuntimeProfile";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> StartupRejections =
        Meter.CreateCounter<long>(
            "godswar.server.startup.rejections");
    private static readonly Counter<long> LegacyAuthenticationAttempts =
        Meter.CreateCounter<long>(
            "godswar.server.legacy_auth.attempts");

    public static void RecordStartupRejection(string reason)
    {
        StartupRejections.Add(
            1,
            new KeyValuePair<string, object?>("reason", reason));
    }

    public static void RecordLegacyAuthenticationAttempt(
        string endpoint,
        string outcome)
    {
        LegacyAuthenticationAttempts.Add(
            1,
            new KeyValuePair<string, object?>("endpoint", endpoint),
            new KeyValuePair<string, object?>("outcome", outcome));
    }
}
