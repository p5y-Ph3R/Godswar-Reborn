using Godswar.Server.Infrastructure.Items;
using Godswar.Server.State;

namespace Godswar.Server;

internal static class ServerItemContentComposition
{
    public static async Task<GameplayItemContent> LoadAsync(
        ServerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var templates =
            await PostgresItemTemplateContentBootstrapper.LoadAsync(
                options.Storage.PostgresConnectionString,
                cancellationToken);
        return new GameplayItemContent(templates);
    }
}
