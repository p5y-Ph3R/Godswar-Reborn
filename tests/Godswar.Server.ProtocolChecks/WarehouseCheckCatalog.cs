namespace Godswar.Server.ProtocolChecks;

internal static class WarehouseCheckCatalog
{
    public static IReadOnlyList<(string Name, Func<Task> Run)> All { get; } =
    [
        (WarehouseContractChecks.CheckName, WarehouseContractChecks.RunAsync),
        (
            WarehouseWireProtocolChecks.CheckName,
            WarehouseWireProtocolChecks.RunAsync),
        (WarehouseHandlerChecks.CheckName, WarehouseHandlerChecks.RunAsync)
    ];
}
