using Godswar.Server.Application.World;
using Godswar.Server.Infrastructure.WorldContent;

namespace Godswar.Server;

internal static class ServerWorldContentComposition
{
    public static async ValueTask<IWorldContentReader?> TryLoadAsync(
        ValidatedServerRuntimeProfile runtimeProfile,
        ServerOptions options)
    {
        try
        {
            return runtimeProfile.StorageProvider switch
            {
                GameStorageProviderKind.Postgres =>
                    await PostgresWorldContentBootstrapper.LoadAsync(
                        options.Storage.PostgresConnectionString),
                GameStorageProviderKind.Json =>
                    await GeneratedWorldContentReaderLoader.LoadAsync(),
                _ => throw new InvalidOperationException(
                    "Validated storage provider has no " +
                    "world-content reader.")
            };
        }
        catch (WorldContentUnavailableException error)
        {
            Console.Error.WriteLine(
                "[world-content] startup rejected " +
                $"family={error.Family} reason={error.Reason}");
            Environment.ExitCode = 3;
            return null;
        }
    }
}
