namespace Godswar.Server.ProtocolChecks;

internal static class B13ObservabilityCoreChecks
{
    public static async Task RunAsync()
    {
        await B13StructuredLoggingChecks.RunAsync();
        await B13PrometheusCollectorChecks.RunAsync();
        await ServerOperationsMetricsChecks.RunAsync();
        await B13ServerActivityChecks.RunAsync();
    }
}
