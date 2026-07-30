namespace Godswar.Server.ProtocolChecks;

internal static class B13OperationsCheckCatalog
{
    public static readonly (string Name, Func<Task> Run)[] All =
    [
        (
            "B13 bounded structured logs, metrics, and traces",
            B13ObservabilityCoreChecks.RunAsync),
        (
            "B13 loopback management options",
            ManagementOptionsChecks.RunAsync),
        (
            "B13 aggregate server operational state",
            ServerOperationalStateChecks.RunAsync),
        (
            "B13 critical task supervision",
            CriticalTaskSupervisorChecks.RunAsync),
        (
            "B13 bounded management HTTP surface",
            ManagementHttpServerChecks.RunAsync),
        (
            "B13 in-image management health probe",
            ManagementProbeCommandChecks.RunAsync),
        (
            "B13 drain and persistence-worker runtime",
            B13PersistenceWorkerChecks.RunAsync),
        (
            "B13 privacy and deployment architecture ratchet",
            B13OperationalArchitectureChecks.RunAsync)
    ];
}
