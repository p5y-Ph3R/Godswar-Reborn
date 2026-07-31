using Godswar.Server.Networking.RelayGateway;
using Godswar.Server.Networking.SemanticGateway;
using Godswar.Server.Operations;
using Godswar.Server.State;

namespace Godswar.Server;

internal static class ServerStartupCommandDispatcher
{
    public static async ValueTask<bool> TryRunAsync(string[] args)
    {
        if (await ManagementProbeCommand.TryRunAsync(args) ||
            await ControlledHostValidationCommand.TryRunAsync(args) ||
            await RelayGatewayCommand.TryRunAsync(args) ||
            await PostgresReconciliationCommand.TryRunAsync(args))
        {
            return true;
        }

        return await SemanticGatewayCommand.TryRunAsync(
            args,
            LegacySemanticGatewayDataSession.OpenAsync,
            ServerCoordinationComposition
                .CreateSemanticGatewayCoordinationAsync);
    }
}
