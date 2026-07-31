namespace Godswar.Server.ProtocolChecks;

internal static class B19ReconciliationCheckCatalog
{
    public static readonly (string Name, Func<Task> Run)[] All =
    [
        (
            B19ReconciliationContractChecks.CheckName,
            B19ReconciliationContractChecks.RunAsync),
        (
            B19ReconciliationRunnerChecks.CheckName,
            B19ReconciliationRunnerChecks.RunAsync),
        (
            B19ReconciliationMetricsChecks.CheckName,
            B19ReconciliationMetricsChecks.RunAsync),
        (
            B19ReconciliationWorkerChecks.CheckName,
            B19ReconciliationWorkerChecks.RunAsync),
        (
            B19ReconciliationCliSafetyChecks.CheckName,
            B19ReconciliationCliSafetyChecks.RunAsync),
        (
            B19ReconciliationArchitectureChecks.CheckName,
            B19ReconciliationArchitectureChecks.RunAsync),
        (
            PostgresB19ReconciliationIntegrationChecks.BoundedCheckName,
            PostgresB19ReconciliationIntegrationChecks.RunBoundedAsync),
        (
            PostgresB19ReconciliationIntegrationChecks.RestoredCheckName,
            PostgresB19ReconciliationIntegrationChecks.RunRestoredAsync)
    ];
}
