using Godswar.Server.Application.Items;
using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;

namespace Godswar.Server;

internal static class ServerPetContentComposition
{
    public static async Task<PinnedPetContentCatalog> LoadAsync(
        ServerOptions options,
        IItemTemplateCatalog itemCatalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(itemCatalog);
        return await PostgresPetContentBootstrapper.LoadAsync(
            options.Storage.PostgresConnectionString,
            itemCatalog,
            cancellationToken);
    }
}
