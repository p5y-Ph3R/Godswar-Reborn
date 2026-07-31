using Godswar.Server.Application.Gateway;
using Godswar.Server.Infrastructure.Gateway;
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
            OpenSemanticGatewayDataSessionAsync,
            ServerCoordinationComposition
                .CreateSemanticGatewayCoordinationAsync);
    }

    private static ValueTask<ISemanticGatewayDataSession>
        OpenSemanticGatewayDataSessionAsync(
            ServerOptions options,
            CancellationToken cancellationToken)
    {
        var profile = ServerRuntimeProfilePolicy.Validate(options);
        return profile.StorageProvider switch
        {
            GameStorageProviderKind.Postgres =>
                PostgresSemanticGatewayDataSession.OpenAsync(
                    options,
                    cancellationToken),
            GameStorageProviderKind.Json =>
                JsonSemanticGatewayDataSession.OpenAsync(
                    options,
                    cancellationToken),
            _ => throw new InvalidDataException(
                "The gateway storage provider is unsupported.")
        };
    }
}
