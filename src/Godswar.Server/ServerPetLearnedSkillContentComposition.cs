using Godswar.Server.Application.Pets;
using Godswar.Server.Infrastructure.Pets;

namespace Godswar.Server;

internal static class ServerPetLearnedSkillContentComposition
{
    public static Task<PinnedPetLearnedSkillContentCatalog> LoadAsync(
        ServerOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return PostgresPetLearnedSkillContentBootstrapper.LoadAsync(
            options.Storage.PostgresConnectionString,
            cancellationToken);
    }
}
